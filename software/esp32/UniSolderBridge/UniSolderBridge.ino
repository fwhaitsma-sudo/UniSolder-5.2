// UniSolder Bluetooth bridge for an ESP32 (ESP32-WROOM-32 dev board).
// Transparent link: Bluetooth Classic (SPP) <-> UART2 to the station, behind a PIN.
//   ESP GPIO16 (RX2) <- station UART2 TX (PIC pin 32)
//   ESP GPIO17 (TX2) -> station UART2 RX (PIC pin 31)
// The framing (0x7E, 64 bytes, CRC-8) is handled by the station and the phone; the bridge only moves bytes.
// Bluetooth pairing itself is "just works" (the precompiled Arduino core has no legacy PIN support), so the
// bridge asks for a PIN: after a phone connects, nothing is passed in either direction until the phone has sent
// the line "PIN:<pin>\n". Answers: "OK\n", "NO\n" (wrong, the link is closed) or "LOCK\n" (too many wrong tries).
// Wi-Fi is never started, to keep current draw low.
#include "BluetoothSerial.h"

static const char *BT_NAME = "UniSolder";
static const char *BT_PIN = "0000";
static const int MAX_TRIES = 5;                 // wrong PINs in a row before the link is locked
static const unsigned long LOCK_MS = 60000;     // lock time

BluetoothSerial SerialBT;
static uint8_t buf[256];
static bool unlocked = false;
static bool hadClient = false;
static char line[24];
static int lineLen = 0;
static int tries = 0;
static bool locked = false;
static unsigned long lockStart = 0;

static void checkLine() {
  line[lineLen] = 0;
  lineLen = 0;
  if (locked && millis() - lockStart < LOCK_MS) {
    SerialBT.print("LOCK\n");
    delay(50);
    SerialBT.disconnect();
    return;
  }
  locked = false;
  String expected = String("PIN:") + BT_PIN;
  if (expected.equals(line)) {
    unlocked = true;
    tries = 0;
    SerialBT.print("OK\n");
    return;
  }
  if (++tries >= MAX_TRIES) {
    locked = true;
    lockStart = millis();
    tries = 0;
  }
  SerialBT.print("NO\n");
  delay(50);
  SerialBT.disconnect();
}

void setup() {
  Serial.begin(115200);                                  // USB debug only; unused when installed in the station
  Serial2.setRxBufferSize(1024);
  Serial2.begin(115200, SERIAL_8N1, 16, 17);
  SerialBT.begin(BT_NAME);
  pinMode(2, OUTPUT);                                    // on-board LED: on while a phone is connected and unlocked
}

void loop() {
  bool client = SerialBT.hasClient();
  if (client != hadClient) {                             // a phone connected or left: lock again, drop old bytes
    hadClient = client;
    unlocked = false;
    lineLen = 0;
    while (SerialBT.available()) SerialBT.read();
    while (Serial2.available()) Serial2.read();
  }
  if (!client) {
    while (Serial2.available()) Serial2.read();
  } else if (!unlocked) {
    while (SerialBT.available() && !unlocked) {
      int b = SerialBT.read();
      if (b == '\n') checkLine();
      else if (b != '\r' && lineLen < (int)sizeof(line) - 1) line[lineLen++] = (char)b;
      else if (b != '\r') lineLen = 0;
    }
    while (Serial2.available()) Serial2.read();          // nothing goes to a phone that has not given the PIN
  } else {
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
      SerialBT.write(buf, n);
    }
  }
  digitalWrite(2, (client && unlocked) ? HIGH : LOW);
  delay(1);
}
