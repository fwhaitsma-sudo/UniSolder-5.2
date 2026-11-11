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
- [x] 1. Study: EEPROM layout, how the set temperature / presets / sleep are held, how settings are loaded (see Facts)
- [x] 2. 0x80 app info (version), 0x10 get state (set temp, preset, sleep, iron, flags)
- [x] 3. 0x11 set temperature, 0x12 select preset, 0x13 sleep/wake, 0x14 switch iron
- [x] 4. 0x20/0x21 read / write settings block (backup / restore), 0x22 restart (firmware builds OK on GitHub; NOT yet tested on hardware)
- [ ] 5. User test of the firmware on hardware (needs the app Control tab, item 6), then changelog

### App (`software/Android`)
- [x] 6. DeviceLink: new commands; Control tab (set temp, presets, sleep/wake, switch iron, info) (ControlPage.kt; builds OK, untested on hardware)
- [x] 7. Settings backup/restore (file on the phone; BackupFile in ControlPage.kt; untested on hardware)
- [x] 8. Session logging with CSV export (Recorder.kt, Tools tab + Live Record button; builds OK, untested on device)
- [x] 9. Tip health: heater resistance avg/range + baseline per iron ID (ToolsPage.kt; no chart, resistance is also in the CSV)
- [x] 10. Alerts (reached, overheat; ToolsPage.kt) and bench dashboard (DashboardView.kt, Live tab button, back closes)
- [ ] 11. PID: step-response analysis (overshoot, settling time, steady error), saved PID sets per tip, revert
- [ ] 12. Calibration helper (suggests Gain from a reference temperature)
- [ ] 13. Fill the instruction manual (Claude Doc "UniSolder Android app: instruction manual", outline exists, sections empty)
- [ ] 14. Changelog entries, user test, merge when the user says so

## Facts found (do not re-research)
- USB protocol so far (app side `DeviceLink.kt`): live data type 3 (offsets: set 3, temp 4-5, adc 6-7, avgF 8-9, heater resistance x10 10-11 (signed), avgP 12-13, heater on 14, WSDelta 15-30, destination reached 31, duty 32-33 with 512 = 100%), iron change type 1, PID get 4 / set 3, op mode 0x61, jump to bootloader 0x81, bootloader 0xE0..0xE5.
- The PID set command writes RAM only (io.c case 3): values are not saved and revert at restart or iron change. PID get beeps.
- Gain = sensor amplifier gain (x*4, x = 8..256 in iron.h comment), KP/KI = coefficient * 32768, DGain 0-32, OVSGain overshoot prevention.
- Earlier messages that listed things "the app can do" were ideas, not existing features.

- Firmware USB commands added (io.c, helpers in menu.c `Remote*`, menu.h `RemoteState`). All replies echo the command byte; data starts after it (little endian):
  - 0x80 info -> [ver minor, ver major, board (0 = 5.2, 1 = 5.2C), protocol (1)]. Old firmware does not answer (timeout = no remote features).
  - 0x10 state -> u16 set, u16 target (0 = heater off), 4 x u16 presets T1-T4 (0 = OFF), u16 HOLDER T, u16 SLEEP T, u16 min (100), u16 max (450), then bytes: mode, flags (bit0 manual sleep, bit1 standby, bit2 two-iron adapter, bit3 iron on stand, bit4 Fahrenheit), active iron, temp step. Offsets 0,2,4,6,8,10,12,14,16,18 then 20,21,22,23.
  - 0x11 set temp [u16 degC] -> [u16 degC applied, clamped 100..450]; 0x12 preset [0..3] -> [u16 set]; 0x13 [1 sleep / 0 wake] -> [1 if sleeping]; 0x14 switch iron -> [active iron].
  - 0x20 read settings [addr, len<=48] -> [addr, len, status (2 = bad range), data...]; 0x21 write [0x5A, 0xA5, addr, len<=48, data...] -> [status 0 ok / 1 bad key / 2 bad range]; 0x22 restart [0x5A, 0xA5] (no reply, device re-enumerates). Settings = EEPROM bytes 0..63 (the pincode bytes 44..49 are read as 0 and never written). Restore = write 0..63 in two chunks, then 0x22.
- Temperatures: TTemp is in whole degrees C (MINTEMP/MAXTEMP are 2 C units: limits 100..450 C). The live-data set temperature is TTemp/2 per byte, so the app shows it in 2 C steps.

## Log
- 2026-11-10 (later): Control tab + backup/restore built (app commit c596c0d). Firmware test builds of `remote-control` are published as pre-releases `fw-rc-*`, app builds as `android-*` pre-releases.
- 2026-11-10: roadmap created. Firmware commands implemented and built (commit 32ad428, build OK).
- 2026-11-11: items 8-10 done (commit 7b6f2a0, Android CI build OK on remote-control). Tabs now Live/Control/PID/Tools/Firmware. Helpers: Prefs.kt (SharedPreferences). Next: item 11 (PID analysis, saved sets, revert) in a new PidTools; then 12, 13.
