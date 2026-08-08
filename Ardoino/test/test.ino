#include <WiFi.h>
#include <driver/i2s.h>

#define I2S_WS   5
#define I2S_SD   6
#define I2S_SCK  7
#define I2S_PORT I2S_NUM_0
#define SAMPLE_RATE 16000
#define BUF_LEN 512

#define PEAK_THRESHOLD    11000
#define SILENCE_TIMEOUT_MS 3000

const char* ssid     = "";
const char* password = "";
const char* host      = ""; // your PC's IP
const uint16_t port   = 5000;

WiFiClient client;
int32_t raw_samples[BUF_LEN];
int16_t send_buf[BUF_LEN];

bool recording = false;
unsigned long lastAboveThreshold = 0;

// Packet types
#define PKT_AUDIO 0
#define PKT_START 1
#define PKT_END   2

void connectWiFi() {
  WiFi.begin(ssid, password);
  Serial.print("Connecting to WiFi");
  while (WiFi.status() != WL_CONNECTED) {
    delay(500);
    Serial.print(".");
  }
  Serial.println("\nWiFi connected, IP: " + WiFi.localIP().toString());
}

void connectServer() {
  while (!client.connected()) {
    Serial.println("Connecting to server...");
    if (client.connect(host, port)) {
      Serial.println("Connected to server");
    } else {
      Serial.println("Connection failed, retrying...");
      delay(1000);
    }
  }
}

bool sendPacket(uint8_t type, const uint8_t* data, uint32_t len) {
  uint8_t header[5];
  header[0] = type;
  header[1] = len & 0xFF;
  header[2] = (len >> 8) & 0xFF;
  header[3] = (len >> 16) & 0xFF;
  header[4] = (len >> 24) & 0xFF;

  if (client.write(header, sizeof(header)) != sizeof(header)) return false;
  if (len > 0 && client.write(data, len) != len) return false;
  return true;
}

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
  digitalWrite(LED_BUILTIN, HIGH); // off (inverted logic)

  connectWiFi();
  connectServer();
}

void loop() {
  if (WiFi.status() != WL_CONNECTED) {
    connectWiFi();
  }
  if (!client.connected()) {
    recording = false; // connection dropped mid-recording, reset state
    connectServer();
  }

  size_t bytes_read;
  i2s_read(I2S_PORT, raw_samples, sizeof(raw_samples), &bytes_read, portMAX_DELAY);
  int samples_read = bytes_read / sizeof(int32_t);

  int32_t peak = 0;
  for (int i = 0; i < samples_read; i++) {
    int32_t s = raw_samples[i] >> 14;
    if (s > 32767) s = 32767;
    if (s < -32768) s = -32768;
    send_buf[i] = (int16_t)s;

    int32_t v = abs(s);
    if (v > peak) peak = v;
  }

  if (peak > PEAK_THRESHOLD) {
    lastAboveThreshold = millis();

    if (!recording) {
      Serial.println("Peak detected, starting recording");
      if (sendPacket(PKT_START, nullptr, 0)) {
        recording = true;
        digitalWrite(LED_BUILTIN, LOW); // on
      }
    }
  }

  if (recording) {
    bool ok = sendPacket(PKT_AUDIO, (uint8_t*)send_buf, samples_read * sizeof(int16_t));
    if (!ok) {
      Serial.println("Send failed, dropping connection");
      client.stop();
      recording = false;
      digitalWrite(LED_BUILTIN, HIGH);
    }

    if (millis() - lastAboveThreshold > SILENCE_TIMEOUT_MS) {
      Serial.println("Silence timeout, ending recording");
      sendPacket(PKT_END, nullptr, 0);
      recording = false;
      digitalWrite(LED_BUILTIN, HIGH); // off
    }
  }
}