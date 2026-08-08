#include <driver/i2s.h>

#define I2S_WS   5
#define I2S_SD   6
#define I2S_SCK  7
#define I2S_PORT I2S_NUM_0
#define SAMPLE_RATE 16000
#define BUF_LEN 512

unsigned long ledTime;

int32_t raw_samples[BUF_LEN];

void setup() {
  Serial.begin(115200);

  i2s_config_t i2s_config = {
    .mode = (i2s_mode_t)(I2S_MODE_MASTER | I2S_MODE_RX),
    .sample_rate = SAMPLE_RATE,
    .bits_per_sample = I2S_BITS_PER_SAMPLE_32BIT,
    .channel_format = I2S_CHANNEL_FMT_ONLY_LEFT,
    .communication_format = I2S_COMM_FORMAT_STAND_I2S,
    .intr_alloc_flags = ESP_INTR_FLAG_LEVEL1,
    .dma_buf_count = 4,
    .dma_buf_len = BUF_LEN,
    .use_apll = false
  };
  i2s_pin_config_t pin_config = {
    .bck_io_num = I2S_SCK,
    .ws_io_num = I2S_WS,
    .data_out_num = I2S_PIN_NO_CHANGE,
    .data_in_num = I2S_SD
  };

  i2s_driver_install(I2S_PORT, &i2s_config, 0, NULL);
  i2s_set_pin(I2S_PORT, &pin_config);

  pinMode(LED_BUILTIN, OUTPUT);
}

void loop() {
  size_t bytes_read;
  i2s_read(I2S_PORT, raw_samples, sizeof(raw_samples), &bytes_read, portMAX_DELAY);
  int samples_read = bytes_read / sizeof(int32_t);

  int32_t peak = 0;
  for (int i = 0; i < samples_read; i++) {
    int32_t v = abs(raw_samples[i] >> 14); // shift the 24-bit sample down
    if (v > peak) peak = v;
  }

  if (peak > 6000) {
    Serial.println(peak); // watch this jump when you tap/talk near the mic
    digitalWrite(LED_BUILTIN, HIGH); // turn the LED on
    ledTime = millis();
  }

  if (millis() - ledTime > 1000) {
    digitalWrite(LED_BUILTIN, LOW); // turn the LED off
  }
  // digitalWrite(LED_BUILTIN, LOW);   // LOW = ON
  // delay(500);
  // digitalWrite(LED_BUILTIN, HIGH);  // HIGH = OFF
  // delay(500);

  delay(50);
}
