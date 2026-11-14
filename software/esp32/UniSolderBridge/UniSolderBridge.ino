// UniSolder Bluetooth bridge for an ESP32 (ESP32-WROOM-32 dev board).
// Transparent link: Bluetooth Classic (SPP) <-> UART2 to the station.
//   ESP GPIO16 (RX2) <- station UART2 TX (PIC pin 32)
//   ESP GPIO17 (TX2) -> station UART2 RX (PIC pin 31)
// The framing (0x7E, 64 bytes, CRC-8) is handled by the station and the phone; the bridge only moves bytes.
// Wi-Fi is never started, to keep current draw low.
#include "BluetoothSerial.h"

static const char *BT_NAME = "UniSolder";
static const char *BT_PIN = "1234";     // legacy pairing PIN; change it here before building if wanted

BluetoothSerial SerialBT;
static uint8_t buf[256];

void setup() {
  Serial.begin(115200);                                  // USB debug only; unused when installed in the station
  Serial2.setRxBufferSize(1024);
  Serial2.begin(115200, SERIAL_8N1, 16, 17);
  SerialBT.setPin(BT_PIN, strlen(BT_PIN));
  SerialBT.begin(BT_NAME);
  pinMode(2, OUTPUT);                                    // on-board LED: on while a phone is connected
}

void loop() {
  int n = SerialBT.available();
  if (n > 0) {
    if (n > (int)sizeof(buf)) n = sizeof(buf);
    n = SerialBT.readBytes(buf, n);
    Serial2.write(buf, n);
  }
  n = Serial2.available();
  if (n > 0) {
    if (n > (int)sizeof(buf)) n = sizeof(buf);
    n = Serial2.readBytes(buf, n);
    if (SerialBT.hasClient()) SerialBT.write(buf, n);    // dropped when nobody is connected
  }
  digitalWrite(2, SerialBT.hasClient() ? HIGH : LOW);
  delay(1);
}
