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
  
  uint8_t buffer[1024];

  while (client.available()) {
      int count = client.read(buffer, sizeof(buffer));

      if (count > 0) {
          Serial0.write(buffer, count);
      }
  }

  while (Serial0.available()) {
      int count = Serial0.read(buffer, sizeof(buffer));

      if (count > 0) {
          client.write(buffer, count);
      }
  }
}