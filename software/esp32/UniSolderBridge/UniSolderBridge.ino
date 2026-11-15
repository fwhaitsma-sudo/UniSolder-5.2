// UniSolder Bluetooth bridge for an ESP32 (ESP32-WROOM-32 dev board).
// Transparent link: Bluetooth Classic (SPP) <-> UART2 to the station, behind a PIN.
//   ESP GPIO16 (RX2) <- station UART2 TX (PIC pin 32)
//   ESP GPIO17 (TX2) -> station UART2 RX (PIC pin 31)
// The framing (0x7E, 64 bytes, CRC-8) is handled by the station and the phone; the bridge only moves bytes.
// Bluetooth pairing itself is "just works" (the precompiled Arduino core has no legacy PIN support), so the
// bridge asks for a PIN: after a phone connects, nothing is passed in either direction until the phone has sent
// the line "PIN:<pin>\n". Answers: "OK\n", "NO\n" (wrong, the link is closed) or "LOCK\n" (too many wrong tries).
// The PIN is 9442 until it is changed from the app (stored in the ESP32 flash).
// Wi-Fi is never started, to keep current draw low.
#include "BluetoothSerial.h"
#include <Preferences.h>

static const char *BT_NAME = "UniSolder";
static const char *DEFAULT_PIN = "9442";   // used until the PIN is changed from the app; flashing the full file again resets it
static const int MAX_TRIES = 5;                 // wrong PINs in a row before the link is locked
static const unsigned long LOCK_MS = 60000;     // lock time

BluetoothSerial SerialBT;
Preferences prefs;
static String pin;
static uint8_t buf[256];
static bool unlocked = false;
static bool hadClient = false;
static char line[24];
static int lineLen = 0;
static int tries = 0;
static bool locked = false;
static int frameLeft = 0;
static bool inCtrl = false;
static char ctrl[24];
static int ctrlLen = 0;
static unsigned long lastByte = 0;
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
  String expected = String("PIN:") + pin;
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

static void handleControl() {
  ctrl[ctrlLen] = 0;
  ctrlLen = 0;
  if (strncmp(ctrl, "SETPIN:", 7) == 0) {
    const char *p = ctrl + 7;
    int n = strlen(p);
    bool ok = n >= 4 && n <= 8;
    for (int i = 0; i < n; i++) if (p[i] < '0' || p[i] > '9') ok = false;
    if (ok) {
      pin = String(p);
      prefs.putString("pin", pin);
      SerialBT.print("OK\n");
      return;
    }
  }
  SerialBT.print("NO\n");
}

void setup() {
  prefs.begin("bridge", false);
  pin = prefs.getString("pin", DEFAULT_PIN);
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
    frameLeft = 0;
    inCtrl = false;
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
    // phone -> station. Frames (0x7E + 64 bytes + CRC = 66 bytes) pass untouched. Outside a frame a line
    // "#SETPIN:<4 to 8 digits>\n" changes the PIN; any other stray byte is dropped.
    while (SerialBT.available()) {
      int b = SerialBT.read();
      lastByte = millis();
      if (frameLeft > 0) { Serial2.write((uint8_t)b); frameLeft--; }
      else if (inCtrl) {
        if (b == '\n') { inCtrl = false; handleControl(); }
        else if (b != '\r' && ctrlLen < (int)sizeof(ctrl) - 1) ctrl[ctrlLen++] = (char)b;
        else if (b != '\r') inCtrl = false;
      }
      else if (b == 0x7E) { Serial2.write((uint8_t)b); frameLeft = 65; }
      else if (b == '#') { inCtrl = true; ctrlLen = 0; }
    }
    if ((frameLeft > 0 || inCtrl) && millis() - lastByte > 200) { frameLeft = 0; inCtrl = false; }   // cut-off frame
    int n = Serial2.available();
    if (n > 0) {
      if (n > (int)sizeof(buf)) n = sizeof(buf);
      n = Serial2.readBytes(buf, n);
      SerialBT.write(buf, n);
    }
  }
  digitalWrite(2, (client && unlocked) ? HIGH : LOW);
  delay(1);
}
