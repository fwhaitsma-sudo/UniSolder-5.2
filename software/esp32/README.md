# ESP32 Bluetooth bridge

Lets the Android app talk to the station over Bluetooth Classic (SPP) instead of the USB cable.
The ESP32 only passes bytes between the phone and UART2 of the front board. It does not change any command.

## What you need

- ESP32 DevKit, ESP32-WROOM-32 (38 pin, CP2102 USB chip)
- Three wires, optionally 100 uF + 100 nF across 3V3 and GND at the ESP32

## Wiring

| Front board | ESP32 |
| --- | --- |
| PIC pin 32 (RF5, UART2 TX) | GPIO16 (RX2) |
| PIC pin 31 (RF4, UART2 RX) | GPIO17 (TX2) |
| J9 pin 1 or 5 (GND), or 2 (DGND) | GND |
| J9 pin 6 (3.3 V) | 3V3 pin (bypasses the ESP32 regulator) |

- The pins marked TXD/RXD on the ESP32 board are the USB pins. Do not use them.
- The station has no 5 V rail, so the ESP32 runs from 3.3 V. Never connect the ESP32 USB cable while the station powers the board.
- Use the USB cable only with the station switched off, to flash the ESP32.

## Flashing

1. Download `UniSolder-ESP32-bridge-<tag>.bin` from the latest `esp-*` release (workflow "Build ESP32 bridge").
2. Install the CP2102 USB driver if the board does not show up as a COM port.
3. Flash the merged file at address **0x0** (for example with the Espressif web flasher or esptool). If the board does not enter flashing mode, hold BOOT while pressing RST.
4. A full flash at 0x0 resets the PIN to the default.

## Pairing and PIN

1. Pair the phone with **UniSolder** in Android's Bluetooth settings (just-works, no code).
2. In the app press **Bluetooth**, choose the device and enter the PIN. The default PIN is **0000**.
3. The PIN is changed from the app (Bluetooth > Change the bridge PIN, 4 to 8 digits) and is stored in the ESP32 flash.
4. After 5 wrong PINs the bridge locks for 60 seconds.

The blue LED on GPIO2 is lit while a phone is connected and the PIN was accepted.

## Protocol

- Phone to bridge: `PIN:<pin>\n` first. The bridge answers `OK\n`, `NO\n` (and disconnects) or `LOCK\n`.
- After `OK` the link is transparent: frames are `0x7E` + 64-byte USB packet + CRC-8 (polynomial 0x07).
- `#SETPIN:<4-8 digits>\n` changes the PIN. It is accepted only between frames.
- The station sends live data only while a valid frame arrived in the last 3 seconds (the app sends an empty keepalive packet every second).
- Firmware updates (bootloader jump) are blocked over UART and only work over USB.
