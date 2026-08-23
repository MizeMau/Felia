/* USER CODE BEGIN Header */
/**
  ******************************************************************************
  * @file           : main.c
  * @brief          : Main program body
  *
  *  MODIFIED FOR AMP TEST:
  *  Streams raw 16-bit stereo PCM audio from a PC over USART2 straight into
  *  the MAX98357 I2S amp (hi2s3 / SPI3) via HAL_I2S_Transmit.
  *
  *  USART2 (PA2/PA3, ST-Link VCP) = binary audio link to PC - NO text/debug
  *  is ever sent on this UART, since any stray bytes would corrupt the
  *  handshake/stream protocol.
  *
  *  USART1 (PA9/PA10) = free for the ESP32 later; used here only for
  *  human-readable debug prints during bring-up.
  *
  *  Protocol (PC -> STM32, over USART2 @ 460800 8N1):
  *    1. PC sends 0xAA                         (handshake)
  *    2. STM32 replies 0x55                    (ready)
  *    3. PC sends 4 bytes little-endian uint32  (total mono sample/frame count)
  *    4. PC streams raw 16-bit STEREO interleaved PCM data,
  *       AUDIO_CHUNK_FRAMES frames per chunk (L,R,L,R,... as uint16_t each)
  *    5. STM32 replies 0x44 ('D') once all frames received/played
  ******************************************************************************
  * @attention
  *
  * Copyright (c) 2026 STMicroelectronics.
  * All rights reserved.
  *
  * This software is licensed under terms that can be found in the LICENSE file
  * in the root directory of this software component.
  * If no LICENSE file comes with this software, it is provided AS-IS.
  *
  ******************************************************************************
  */
/* USER CODE END Header */
/* Includes ------------------------------------------------------------------*/
#include "main.h"

/* Private includes ----------------------------------------------------------*/
/* USER CODE BEGIN Includes */
#include <stdio.h>
#include <sys/_intsup.h>
/* USER CODE END Includes */

/* Private typedef -----------------------------------------------------------*/
/* USER CODE BEGIN PTD */

/* USER CODE END PTD */

/* Private define ------------------------------------------------------------*/
/* USER CODE BEGIN PD */

/* USER CODE END PD */

/* Private macro -------------------------------------------------------------*/
/* USER CODE BEGIN PM */

/* USER CODE END PM */

/* Private variables ---------------------------------------------------------*/

I2S_HandleTypeDef hi2s2;
I2S_HandleTypeDef hi2s3;

/* USER CODE BEGIN PV */
#define MIC_I2S      hi2s2
#define SPK_I2S      hi2s3

/* ---- Audio streaming test config ---- */
#define HANDSHAKE_BYTE   0xAA
#define READY_BYTE       0x55
#define DONE_BYTE        0x44   // 'D'

#define AUDIO_CHUNK_FRAMES   256                       // stereo frames per chunk
static uint16_t audioBuf[AUDIO_CHUNK_FRAMES * 2];       // interleaved L,R uint16_t samples

/* ---- Interrupt-driven ring buffer for USART2 (audio) RX ----
 * HAL_I2S_Transmit() is a blocking polling call (~16ms per chunk). During
 * that time nothing services the UART RX register in software, and STM32
 * USART hardware only buffers 1 byte - so a plain polling receive loses
 * hundreds of bytes per chunk and desyncs the whole stream (heard as
 * clicking/"bonking" garbled audio). An RXNE interrupt fires and drains
 * the register immediately regardless of what the main loop is doing, so
 * we buffer received bytes here and let AudioStream_Run() pull from it.
 */
#define AUDIO_RXBUF_SIZE   16384u   /* must be a power of two */
static volatile uint8_t  audioRxBuf[AUDIO_RXBUF_SIZE];
static volatile uint16_t audioRxHead = 0; /* ISR writes here */
static volatile uint16_t audioRxTail = 0; /* main loop reads here */
/* USER CODE END PV */

/* Private function prototypes -----------------------------------------------*/
void SystemClock_Config(void);
void PeriphCommonClock_Config(void);
static void MX_GPIO_Init(void);
static void MX_USART2_UART_Init(void);
static void MX_I2S2_Init(void);
static void MX_I2C1_Init(void);
static void MX_I2S3_Init(void);
static void MX_USART1_UART_Init(void);
/* USER CODE BEGIN PFP */
static void AudioStream_Run(void);
void Audio_UART_ISR_Handler(void); /* called from USART2_IRQHandler in stm32f4xx_it.c */
/* USER CODE END PFP */

/* Private user code ---------------------------------------------------------*/
/* USER CODE BEGIN 0 */

/* ---- Debug UART: USART1 (PA9/PA10) - human readable text only ---- */
static void Debug_SendString(const char *str)
{
    while (*str)
    {
        while (!LL_USART_IsActiveFlag_TXE(USART1))
        {
        }

        LL_USART_TransmitData8(USART1, (uint8_t)*str++);
    }

    while (!LL_USART_IsActiveFlag_TC(USART1))
    {
    }
}

/*
 * USART2 RX interrupt handler body. Add this call inside the real
 * USART2_IRQHandler() in stm32f4xx_it.c (see notes at bottom of file).
 */
void Audio_UART_ISR_Handler(void)
{
    if (LL_USART_IsActiveFlag_RXNE(USART2))
    {
        uint8_t b = LL_USART_ReceiveData8(USART2); /* also clears RXNE */
        uint16_t next = (uint16_t)((audioRxHead + 1) & (AUDIO_RXBUF_SIZE - 1));
        if (next != audioRxTail) /* drop byte only if buffer truly full */
        {
            audioRxBuf[audioRxHead] = b;
            audioRxHead = next;
        }
    }

    if (LL_USART_IsActiveFlag_ORE(USART2))
    {
        /* Clear overrun error flag so RX doesn't get stuck. Should not
         * happen anymore with the ISR draining bytes promptly, but clear
         * it defensively. */
        LL_USART_ClearFlag_ORE(USART2);
    }
}

/* ---- Audio UART: USART2 (PA2/PA3, ST-Link VCP) - binary protocol only ----
 * Reads pull from the interrupt-filled ring buffer above, never poll the
 * USART peripheral directly.
 */
static uint8_t Audio_RecvByte(void)
{
    while (audioRxHead == audioRxTail)
    {
        /* wait for the ISR to deliver a byte */
    }
    uint8_t b = audioRxBuf[audioRxTail];
    audioRxTail = (uint16_t)((audioRxTail + 1) & (AUDIO_RXBUF_SIZE - 1));
    return b;
}

static void Audio_SendByte(uint8_t b)
{
    while (!LL_USART_IsActiveFlag_TXE(USART2))
    {
    }
    LL_USART_TransmitData8(USART2, b);
}

static void Audio_RecvBytes(uint8_t *buf, uint32_t len)
{
    for (uint32_t i = 0; i < len; i++)
    {
        buf[i] = Audio_RecvByte();
    }
}

/*
 * Main audio streaming test routine.
 * Waits for a handshake from the PC, then receives a PCM stream and
 * forwards it directly to the I2S amp in fixed-size chunks.
 */
static void AudioStream_Run(void)
{
    /* Wait for handshake byte from PC (blocking - this is fine, it's all
     * this firmware does) */
    if (Audio_RecvByte() != HANDSHAKE_BYTE)
    {
        return; // not our protocol, ignore
    }

    Debug_SendString("Handshake OK, sending READY\r\n");
    Audio_SendByte(READY_BYTE);

    LL_GPIO_ResetOutputPin(LD2_GPIO_Port, LD2_Pin); // LED on while streaming

    /* Read 4-byte little-endian total frame count */
    uint8_t hdr[4];
    Audio_RecvBytes(hdr, 4);
    uint32_t totalFrames = (uint32_t)hdr[0]
                          | ((uint32_t)hdr[1] << 8)
                          | ((uint32_t)hdr[2] << 16)
                          | ((uint32_t)hdr[3] << 24);

    char msg[48];
    sprintf(msg, "Streaming %lu frames\r\n", (unsigned long)totalFrames);
    Debug_SendString(msg);

    uint32_t framesRemaining = totalFrames;

    while (framesRemaining > 0)
    {
        uint32_t framesThisChunk = (framesRemaining >= AUDIO_CHUNK_FRAMES)
                                        ? AUDIO_CHUNK_FRAMES
                                        : framesRemaining;

        /* Receive framesThisChunk stereo frames = framesThisChunk*4 bytes */
        Audio_RecvBytes((uint8_t *)audioBuf, framesThisChunk * 4);

        /* Size param for HAL_I2S_Transmit = number of 16-bit words to send
         * (L+R per frame), matches I2S_DATAFORMAT_16B config on hi2s3 */
        if (HAL_I2S_Transmit(&SPK_I2S, audioBuf, (uint16_t)(framesThisChunk * 2), 2000) != HAL_OK)
        {
            Debug_SendString("I2S TX ERROR\r\n");
            break;
        }

        framesRemaining -= framesThisChunk;
    }

    LL_GPIO_SetOutputPin(LD2_GPIO_Port, LD2_Pin); // LED off

    Debug_SendString("Stream complete\r\n");
    Audio_SendByte(DONE_BYTE);
}

/* USER CODE END 0 */

/**
  * @brief  The application entry point.
  * @retval int
  */
int main(void)
{

  /* USER CODE BEGIN 1 */

  /* USER CODE END 1 */

  /* MCU Configuration--------------------------------------------------------*/

  /* Reset of all peripherals, Initializes the Flash interface and the Systick. */
  HAL_Init();

  /* USER CODE BEGIN Init */

  /* USER CODE END Init */

  /* Configure the system clock */
  SystemClock_Config();

  /* Configure the peripherals common clocks */
  PeriphCommonClock_Config();

  /* USER CODE BEGIN SysInit */

  /* USER CODE END SysInit */

  /* Initialize all configured peripherals */
  MX_GPIO_Init();
  MX_USART2_UART_Init();
  MX_I2S2_Init();
  MX_I2C1_Init();
  MX_I2S3_Init();
  MX_USART1_UART_Init();
  /* USER CODE BEGIN 2 */
  /* Switch USART2 to interrupt-driven RX for the audio link (see the ring
   * buffer + Audio_UART_ISR_Handler() above for why this matters). */
  LL_USART_EnableIT_RXNE(USART2);
  NVIC_SetPriority(USART2_IRQn, 0); /* highest priority - must preempt the
                                      * blocking I2S transmit busy-loop */
  NVIC_EnableIRQ(USART2_IRQn);

  Debug_SendString("Debug UART OK - Amp streaming test ready\r\n");
  /* USER CODE END 2 */

  /* Infinite loop */
  /* USER CODE BEGIN WHILE */
  while (1)
  {
    /* USER CODE END WHILE */

    /* USER CODE BEGIN 3 */
    AudioStream_Run();
  }
  /* USER CODE END 3 */
}

/**
  * @brief System Clock Configuration
  * @retval None
  */
void SystemClock_Config(void)
{
  LL_FLASH_SetLatency(LL_FLASH_LATENCY_2);
  while(LL_FLASH_GetLatency()!= LL_FLASH_LATENCY_2)
  {
  }
  LL_PWR_SetRegulVoltageScaling(LL_PWR_REGU_VOLTAGE_SCALE1);
  LL_RCC_HSI_SetCalibTrimming(16);
  LL_RCC_HSI_Enable();

   /* Wait till HSI is ready */
  while(LL_RCC_HSI_IsReady() != 1)
  {

  }
  LL_RCC_PLL_ConfigDomain_SYS(LL_RCC_PLLSOURCE_HSI, LL_RCC_PLLM_DIV_16, 336, LL_RCC_PLLP_DIV_4);
  LL_RCC_PLL_Enable();

   /* Wait till PLL is ready */
  while(LL_RCC_PLL_IsReady() != 1)
  {

  }
  while (LL_PWR_IsActiveFlag_VOS() == 0)
  {
  }
  LL_RCC_SetAHBPrescaler(LL_RCC_SYSCLK_DIV_1);
  LL_RCC_SetAPB1Prescaler(LL_RCC_APB1_DIV_2);
  LL_RCC_SetAPB2Prescaler(LL_RCC_APB2_DIV_1);
  LL_RCC_SetSysClkSource(LL_RCC_SYS_CLKSOURCE_PLL);

   /* Wait till System clock is ready */
  while(LL_RCC_GetSysClkSource() != LL_RCC_SYS_CLKSOURCE_STATUS_PLL)
  {

  }
  LL_SetSystemCoreClock(84000000);

   /* Update the time base */
  if (HAL_InitTick (TICK_INT_PRIORITY) != HAL_OK)
  {
    Error_Handler();
  }
  LL_RCC_SetTIMPrescaler(LL_RCC_TIM_PRESCALER_TWICE);
}

/**
  * @brief Peripherals Common Clock Configuration
  * @retval None
  */
void PeriphCommonClock_Config(void)
{
  LL_RCC_PLLI2S_ConfigDomain_I2S(LL_RCC_PLLSOURCE_HSI, LL_RCC_PLLI2SM_DIV_16, 192, LL_RCC_PLLI2SR_DIV_2);
  LL_RCC_PLLI2S_Enable();

   /* Wait till PLL is ready */
  while(LL_RCC_PLLI2S_IsReady() != 1)
  {

  }
}

/**
  * @brief I2C1 Initialization Function
  * @param None
  * @retval None
  */
static void MX_I2C1_Init(void)
{

  /* USER CODE BEGIN I2C1_Init 0 */

  /* USER CODE END I2C1_Init 0 */

  LL_I2C_InitTypeDef I2C_InitStruct = {0};

  LL_GPIO_InitTypeDef GPIO_InitStruct = {0};

  LL_AHB1_GRP1_EnableClock(LL_AHB1_GRP1_PERIPH_GPIOB);
  /**I2C1 GPIO Configuration
  PB6   ------> I2C1_SCL
  PB7   ------> I2C1_SDA
  */
  GPIO_InitStruct.Pin = LL_GPIO_PIN_6|LL_GPIO_PIN_7;
  GPIO_InitStruct.Mode = LL_GPIO_MODE_ALTERNATE;
  GPIO_InitStruct.Speed = LL_GPIO_SPEED_FREQ_VERY_HIGH;
  GPIO_InitStruct.OutputType = LL_GPIO_OUTPUT_OPENDRAIN;
  GPIO_InitStruct.Pull = LL_GPIO_PULL_NO;
  GPIO_InitStruct.Alternate = LL_GPIO_AF_4;
  LL_GPIO_Init(GPIOB, &GPIO_InitStruct);

  /* Peripheral clock enable */
  LL_APB1_GRP1_EnableClock(LL_APB1_GRP1_PERIPH_I2C1);

  /* USER CODE BEGIN I2C1_Init 1 */

  /* USER CODE END I2C1_Init 1 */

  /** I2C Initialization
  */
  LL_I2C_DisableOwnAddress2(I2C1);
  LL_I2C_DisableGeneralCall(I2C1);
  LL_I2C_EnableClockStretching(I2C1);
  I2C_InitStruct.PeripheralMode = LL_I2C_MODE_I2C;
  I2C_InitStruct.ClockSpeed = 100000;
  I2C_InitStruct.DutyCycle = LL_I2C_DUTYCYCLE_2;
  I2C_InitStruct.OwnAddress1 = 0;
  I2C_InitStruct.TypeAcknowledge = LL_I2C_ACK;
  I2C_InitStruct.OwnAddrSize = LL_I2C_OWNADDRESS1_7BIT;
  LL_I2C_Init(I2C1, &I2C_InitStruct);
  LL_I2C_SetOwnAddress2(I2C1, 0);
  /* USER CODE BEGIN I2C1_Init 2 */

  /* USER CODE END I2C1_Init 2 */

}

/**
  * @brief I2S2 Initialization Function
  * @param None
  * @retval None
  */
static void MX_I2S2_Init(void)
{

  /* USER CODE BEGIN I2S2_Init 0 */

  /* USER CODE END I2S2_Init 0 */

  /* USER CODE BEGIN I2S2_Init 1 */

  /* USER CODE END I2S2_Init 1 */
  hi2s2.Instance = SPI2;
  hi2s2.Init.Mode = I2S_MODE_MASTER_RX;
  hi2s2.Init.Standard = I2S_STANDARD_PHILIPS;
  hi2s2.Init.DataFormat = I2S_DATAFORMAT_24B;
  hi2s2.Init.MCLKOutput = I2S_MCLKOUTPUT_DISABLE;
  hi2s2.Init.AudioFreq = I2S_AUDIOFREQ_16K;
  hi2s2.Init.CPOL = I2S_CPOL_LOW;
  hi2s2.Init.ClockSource = I2S_CLOCK_PLL;
  hi2s2.Init.FullDuplexMode = I2S_FULLDUPLEXMODE_DISABLE;
  if (HAL_I2S_Init(&hi2s2) != HAL_OK)
  {
    Error_Handler();
  }
  /* USER CODE BEGIN I2S2_Init 2 */

  /* USER CODE END I2S2_Init 2 */

}

/**
  * @brief I2S3 Initialization Function
  * @param None
  * @retval None
  */
static void MX_I2S3_Init(void)
{

  /* USER CODE BEGIN I2S3_Init 0 */

  /* USER CODE END I2S3_Init 0 */

  /* USER CODE BEGIN I2S3_Init 1 */

  /* USER CODE END I2S3_Init 1 */
  hi2s3.Instance = SPI3;
  hi2s3.Init.Mode = I2S_MODE_MASTER_TX;
  hi2s3.Init.Standard = I2S_STANDARD_PHILIPS;
  hi2s3.Init.DataFormat = I2S_DATAFORMAT_16B;
  hi2s3.Init.MCLKOutput = I2S_MCLKOUTPUT_DISABLE;
  hi2s3.Init.AudioFreq = I2S_AUDIOFREQ_16K;
  hi2s3.Init.CPOL = I2S_CPOL_LOW;
  hi2s3.Init.ClockSource = I2S_CLOCK_PLL;
  hi2s3.Init.FullDuplexMode = I2S_FULLDUPLEXMODE_DISABLE;
  if (HAL_I2S_Init(&hi2s3) != HAL_OK)
  {
    Error_Handler();
  }
  /* USER CODE BEGIN I2S3_Init 2 */

  /* USER CODE END I2S3_Init 2 */

}

/**
  * @brief USART1 Initialization Function
  * @param None
  * @retval None
  *
  * Debug UART only in this test build (kept at 115200, reserved for the
  * ESP32 link later). All human-readable status messages go here.
  */
static void MX_USART1_UART_Init(void)
{

  /* USER CODE BEGIN USART1_Init 0 */

  /* USER CODE END USART1_Init 0 */

  LL_USART_InitTypeDef USART_InitStruct = {0};

  LL_GPIO_InitTypeDef GPIO_InitStruct = {0};

  /* Peripheral clock enable */
  LL_APB2_GRP1_EnableClock(LL_APB2_GRP1_PERIPH_USART1);

  LL_AHB1_GRP1_EnableClock(LL_AHB1_GRP1_PERIPH_GPIOA);
  /**USART1 GPIO Configuration
  PA9   ------> USART1_TX
  PA10   ------> USART1_RX
  */
  GPIO_InitStruct.Pin = LL_GPIO_PIN_9|LL_GPIO_PIN_10;
  GPIO_InitStruct.Mode = LL_GPIO_MODE_ALTERNATE;
  GPIO_InitStruct.Speed = LL_GPIO_SPEED_FREQ_VERY_HIGH;
  GPIO_InitStruct.OutputType = LL_GPIO_OUTPUT_PUSHPULL;
  GPIO_InitStruct.Pull = LL_GPIO_PULL_NO;
  GPIO_InitStruct.Alternate = LL_GPIO_AF_7;
  LL_GPIO_Init(GPIOA, &GPIO_InitStruct);

  /* USER CODE BEGIN USART1_Init 1 */

  /* USER CODE END USART1_Init 1 */
  USART_InitStruct.BaudRate = 921600;
  USART_InitStruct.DataWidth = LL_USART_DATAWIDTH_8B;
  USART_InitStruct.StopBits = LL_USART_STOPBITS_1;
  USART_InitStruct.Parity = LL_USART_PARITY_NONE;
  USART_InitStruct.TransferDirection = LL_USART_DIRECTION_TX_RX;
  USART_InitStruct.HardwareFlowControl = LL_USART_HWCONTROL_NONE;
  USART_InitStruct.OverSampling = LL_USART_OVERSAMPLING_16;
  LL_USART_Init(USART1, &USART_InitStruct);
  LL_USART_ConfigAsyncMode(USART1);
  LL_USART_Enable(USART1);
  /* USER CODE BEGIN USART1_Init 2 */

  /* USER CODE END USART1_Init 2 */

}

/**
  * @brief USART2 Initialization Function
  * @param None
  * @retval None
  *
  * NOTE: Baud rate raised to 460800 for this test - USART2 (PA2/PA3, the
  * ST-Link virtual COM port) is now the dedicated high-throughput binary
  * audio link to the PC. 16kHz stereo 16-bit PCM needs ~64KB/s;
  * 115200 baud (~11.5KB/s) is far too slow. 460800 is used instead of
  * 921600 since ST-Link VCP bridges are not always reliable at very high
  * rates - bump it up once you've confirmed the basic test works.
  */
static void MX_USART2_UART_Init(void)
{

  /* USER CODE BEGIN USART2_Init 0 */

  /* USER CODE END USART2_Init 0 */

  LL_USART_InitTypeDef USART_InitStruct = {0};

  LL_GPIO_InitTypeDef GPIO_InitStruct = {0};

  /* Peripheral clock enable */
  LL_APB1_GRP1_EnableClock(LL_APB1_GRP1_PERIPH_USART2);

  LL_AHB1_GRP1_EnableClock(LL_AHB1_GRP1_PERIPH_GPIOA);
  /**USART2 GPIO Configuration
  PA2   ------> USART2_TX
  PA3   ------> USART2_RX
  */
  GPIO_InitStruct.Pin = USART_TX_Pin|USART_RX_Pin;
  GPIO_InitStruct.Mode = LL_GPIO_MODE_ALTERNATE;
  GPIO_InitStruct.Speed = LL_GPIO_SPEED_FREQ_VERY_HIGH;
  GPIO_InitStruct.OutputType = LL_GPIO_OUTPUT_PUSHPULL;
  GPIO_InitStruct.Pull = LL_GPIO_PULL_NO;
  GPIO_InitStruct.Alternate = LL_GPIO_AF_7;
  LL_GPIO_Init(GPIOA, &GPIO_InitStruct);

  /* USER CODE BEGIN USART2_Init 1 */

  /* USER CODE END USART2_Init 1 */
  USART_InitStruct.BaudRate = 921600;
  USART_InitStruct.DataWidth = LL_USART_DATAWIDTH_8B;
  USART_InitStruct.StopBits = LL_USART_STOPBITS_1;
  USART_InitStruct.Parity = LL_USART_PARITY_NONE;
  USART_InitStruct.TransferDirection = LL_USART_DIRECTION_TX_RX;
  USART_InitStruct.HardwareFlowControl = LL_USART_HWCONTROL_NONE;
  USART_InitStruct.OverSampling = LL_USART_OVERSAMPLING_16;
  LL_USART_Init(USART2, &USART_InitStruct);
  LL_USART_ConfigAsyncMode(USART2);
  LL_USART_Enable(USART2);
  /* USER CODE BEGIN USART2_Init 2 */

  /* USER CODE END USART2_Init 2 */

}

/**
  * @brief GPIO Initialization Function
  * @param None
  * @retval None
  */
static void MX_GPIO_Init(void)
{
  LL_EXTI_InitTypeDef EXTI_InitStruct = {0};
  LL_GPIO_InitTypeDef GPIO_InitStruct = {0};
  /* USER CODE BEGIN MX_GPIO_Init_1 */

  /* USER CODE END MX_GPIO_Init_1 */

  /* GPIO Ports Clock Enable */
  LL_AHB1_GRP1_EnableClock(LL_AHB1_GRP1_PERIPH_GPIOC);
  LL_AHB1_GRP1_EnableClock(LL_AHB1_GRP1_PERIPH_GPIOH);
  LL_AHB1_GRP1_EnableClock(LL_AHB1_GRP1_PERIPH_GPIOA);
  LL_AHB1_GRP1_EnableClock(LL_AHB1_GRP1_PERIPH_GPIOB);

  /**/
  LL_GPIO_ResetOutputPin(LD2_GPIO_Port, LD2_Pin);

  /**/
  LL_SYSCFG_SetEXTISource(LL_SYSCFG_EXTI_PORTC, LL_SYSCFG_EXTI_LINE13);

  /**/
  EXTI_InitStruct.Line_0_31 = LL_EXTI_LINE_13;
  EXTI_InitStruct.LineCommand = ENABLE;
  EXTI_InitStruct.Mode = LL_EXTI_MODE_IT;
  EXTI_InitStruct.Trigger = LL_EXTI_TRIGGER_FALLING;
  LL_EXTI_Init(&EXTI_InitStruct);

  /**/
  LL_GPIO_SetPinPull(B1_GPIO_Port, B1_Pin, LL_GPIO_PULL_NO);

  /**/
  LL_GPIO_SetPinMode(B1_GPIO_Port, B1_Pin, LL_GPIO_MODE_INPUT);

  /**/
  GPIO_InitStruct.Pin = LD2_Pin;
  GPIO_InitStruct.Mode = LL_GPIO_MODE_OUTPUT;
  GPIO_InitStruct.Speed = LL_GPIO_SPEED_FREQ_LOW;
  GPIO_InitStruct.OutputType = LL_GPIO_OUTPUT_PUSHPULL;
  GPIO_InitStruct.Pull = LL_GPIO_PULL_NO;
  LL_GPIO_Init(LD2_GPIO_Port, &GPIO_InitStruct);

  /**/
  GPIO_InitStruct.Pin = LL_GPIO_PIN_4;
  GPIO_InitStruct.Mode = LL_GPIO_MODE_INPUT;
  GPIO_InitStruct.Pull = LL_GPIO_PULL_NO;
  LL_GPIO_Init(GPIOC, &GPIO_InitStruct);

  /**/
  GPIO_InitStruct.Pin = LL_GPIO_PIN_4|LL_GPIO_PIN_5;
  GPIO_InitStruct.Mode = LL_GPIO_MODE_INPUT;
  GPIO_InitStruct.Pull = LL_GPIO_PULL_NO;
  LL_GPIO_Init(GPIOB, &GPIO_InitStruct);

  /* USER CODE BEGIN MX_GPIO_Init_2 */

  /* USER CODE END MX_GPIO_Init_2 */
}

/* USER CODE BEGIN 4 */

/* USER CODE END 4 */

/**
  * @brief  This function is executed in case of error occurrence.
  * @retval None
  */
void Error_Handler(void)
{
  /* USER CODE BEGIN Error_Handler_Debug */
  /* User can add his own implementation to report the HAL error return state */
  __disable_irq();
  while (1)
  {
  }
  /* USER CODE END Error_Handler_Debug */
}
#ifdef USE_FULL_ASSERT
/**
  * @brief  Reports the name of the source file and the source line number
  *         where the assert_param error has occurred.
  * @param  file: pointer to the source file name
  * @param  line: assert_param error line source number
  * @retval None
  */
void assert_failed(uint8_t *file, uint32_t line)
{
  /* USER CODE BEGIN 6 */
  /* User can add his own implementation to report the file name and line number,
     ex: printf("Wrong parameters value: file %s on line %d\r\n", file, line) */
  /* USER CODE END 6 */
}
#endif /* USE_FULL_ASSERT */

/* =============================================================================
 * IMPORTANT - ONE MANUAL STEP REQUIRED
 * =============================================================================
 * CubeMX generates the actual USART2_IRQHandler() in stm32f4xx_it.c, not
 * here in main.c. Open stm32f4xx_it.c and find (or add) the handler, then
 * call Audio_UART_ISR_Handler() from inside it:
 *
 *   void USART2_IRQHandler(void)
 *   {
 *     Audio_UART_ISR_Handler();
 *   }
 *
 * (If CubeMX already generated a body with HAL_UART_IRQHandler(&huart2) in
 * it because interrupt mode was enabled in the .ioc, replace that call with
 * Audio_UART_ISR_Handler() instead - we're using LL calls directly here,
 * not the HAL UART IRQ machinery.)
 *
 * Without this, USART2_IRQn will fire but jump to the default weak handler
 * (infinite loop / hard fault territory), or nothing will happen and the
 * ring buffer will simply never fill.
 * ===========================================================================
 */