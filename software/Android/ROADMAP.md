# Android app + firmware remote control: roadmap

Branch: `remote-control` (based on `android-app`). Both the firmware workflow and the Android workflow build it on every push.
Update this file and commit after each finished item, so a new session can continue without redoing work.

## Requests from the user (2026-11-10)
- New firmware must be able to **save and restore settings** and be **remote controlled by the phone**.
- The Android app must do everything below.
- Remember progress (this file).

## Safety rules for the firmware commands
- The set temperature from the phone is clamped to the same range as the knob (MINTEMP..MAXTEMP) and uses the same code path as the knob.
- No command may switch the heater on by itself or bypass the PID, the sensor checks or the over-temperature handling.
- Settings restore only accepts a block that passes a marker + checksum check, and applies it with a restart.

## Plan
### Firmware (`software/front/US_Firmware.X`, `io.c` command handler)
- [ ] 1. Study: EEPROM layout, how the set temperature / presets / sleep are held, how settings are loaded
- [ ] 2. 0x80 app info (version), 0x10 get state (set temp, preset, sleep, iron, flags)
- [ ] 3. 0x11 set temperature, 0x12 select preset, 0x13 sleep/wake
- [ ] 4. 0x20/0x21 read / write settings block (backup / restore) + restart
- [ ] 5. Build, user test on hardware, changelog

### App (`software/Android`)
- [ ] 6. DeviceLink: new commands; Control tab (set temp, presets, sleep/wake, info)
- [ ] 7. Settings backup/restore (file on the phone)
- [ ] 8. Session logging with CSV export
- [ ] 9. Tip health: heater resistance chart + trend
- [ ] 10. Alerts (target reached, stays above set temperature) and bench dashboard (big landscape view)
- [ ] 11. PID: step-response analysis (overshoot, settling time, steady error), saved PID sets per tip, revert
- [ ] 12. Calibration helper (suggests Gain from a reference temperature)
- [ ] 13. Fill the instruction manual (Claude Doc "UniSolder Android app: instruction manual", outline exists, sections empty)
- [ ] 14. Changelog entries, user test, merge when the user says so

## Facts found (do not re-research)
- USB protocol so far (app side `DeviceLink.kt`): live data type 3 (offsets: set 3, temp 4-5, adc 6-7, avgF 8-9, heater resistance x10 10-11 (signed), avgP 12-13, heater on 14, WSDelta 15-30, destination reached 31, duty 32-33 with 512 = 100%), iron change type 1, PID get 4 / set 3, op mode 0x61, jump to bootloader 0x81, bootloader 0xE0..0xE5.
- The PID set command writes RAM only (io.c case 3): values are not saved and revert at restart or iron change. PID get beeps.
- Gain = sensor amplifier gain (x*4, x = 8..256 in iron.h comment), KP/KI = coefficient * 32768, DGain 0-32, OVSGain overshoot prevention.
- Earlier messages that listed things "the app can do" were ideas, not existing features.

## Log
- 2026-11-10: roadmap created.
