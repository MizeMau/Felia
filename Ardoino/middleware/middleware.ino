#include <WiFi.h>
#include <lwip/sockets.h>

#include "config.h"

WiFiClient client;

void enableKeepAlive(WiFiClient &c) {
  int sock = c.fd();
  if (sock < 0) {
    Serial.println("Could not get socket fd for keepalive");
    return;
  }

  int enable = 1;
  setsockopt(sock, SOL_SOCKET, SO_KEEPALIVE, &enable, sizeof(enable));

  int idle = 5;
  int interval = 2; 
  int count = 3; 

  setsockopt(sock, IPPROTO_TCP, TCP_KEEPIDLE, &idle, sizeof(idle));
  setsockopt(sock, IPPROTO_TCP, TCP_KEEPINTVL, &interval, sizeof(interval));
  setsockopt(sock, IPPROTO_TCP, TCP_KEEPCNT, &count, sizeof(count));

  Serial.println("TCP keepalive enabled");
}

void connectWiFi() {
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
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
    if (client.connect(WIFI_HOST, WIFI_PORT)) {
      enableKeepAlive(client);
      Serial.println("Connected to server");
    } else {
      Serial.println("Connection failed, retrying...");
      delay(5000);
    }
  }
}

bool sendPacket(const uint8_t* data, uint32_t len) {
  if (len > 0 && client.write(data, len) != len) return false;
  return true;
}

void setup() {
  Serial.begin(115200);
  Serial0.begin(921600);

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
    connectServer();
  }
  
  while (client.available()) {
    int byte = client.read();
    if (byte == -1)
      continue;
    Serial0.write(byte);
  }
}
































