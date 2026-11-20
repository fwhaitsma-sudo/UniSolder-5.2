# Changelog

Features and changes implemented in the `display-step` branch of the fork, on top of sparkybg/UniSolder-5.2 `main`.
Only what is in the firmware now is listed. Reverted and superseded changes are left out.

## Display and settings

- **DISP.STEP** (menu, 2–5 degrees): rounds the measured temperature shown on the main screen, with half a degree of hysteresis. Works in C and F. The value 1 keeps the original display. (f40e69d)
- **Standby screen:** the text "ready when you are" replaces HOT and ZZZ. It stays still and jumps to a random position every 30 seconds. There is no longer a HOT warning on this screen.
- **Welcome screen with your name:** at power-up the boot logo is replaced by "Hi <name>!" (for example "Hi John!"). The name is set in the new NAME menu item, up to 10 characters: turn the knob to change the character, press for the next one, hold 1 s or press on the last one to save. A changed name shows at the next boot. With no name set, the logo is shown as before. The name is stored in the EEPROM when saved.
- **Menu pincode:** a PINCODE menu item sets or clears a 4-digit pincode (four dashes clear it). With a pincode set, opening the menu asks for it first. Without one the menu opens as before. Holding the button for the whole boot screen clears a forgotten pincode.
- **Set temperature per degree:** the set temperature is kept in whole degrees. TEMP.STEP is the knob step in degrees (1–50, default 2), so it can match DISP.STEP. T1–T4, HOLDER T and SLEEP T are also edited and shown in degrees (1 degree steps). The control itself is unchanged. The odd degree is stored in separate EEPROM bytes, so existing settings load as before. After flashing, choose TEMP.STEP again: an old value of 5 used to mean 10 °C.
- **Beep pitch and sound on/off:** a SND PITCH menu item (1–9, about 800 Hz to 5.2 kHz, default 7, close to the old fixed tone) sets the pitch of all beeps, with a test beep when it is changed. A SOUND menu item turns all beeps off or on and comes before SND PITCH in the menu. The settings are saved with the others.
- **Left-aligned menu labels:** every label in the settings menu starts at the same position, instead of being right-aligned against the value column. Labels are at most 10 characters so they fit the label area.
- **Right-aligned menu values:** the values in the settings menu end at the same right edge, with the degree unit (°C or °F) right behind temperatures and degree steps.
- **Faster display refresh:** only rows that changed are sent to the screen. Fill and invert are faster. (e65ddb1)

## Buttons and holds

- **Short press** (under 1 s): confirms the current preset. Each further press cycles to the next active preset.
- **Hold 1 s and release:** opens the menu. (b16acf9)
- **Hold 3 s:** enters standby (the "ready when you are" screen), with the heater off. (b16acf9, baa0e79, 6ceb1bd)
- **Wake from standby:** turning the knob, pressing the button, or lifting the iron from its stand (if it was in the stand when standby started). (baa0e79)
- **Wake from automatic sleep (SLEEP T):** a button press wakes the iron. A press under 1 s heats the iron on release. A press of 1 s or longer leaves the heater off until the knob is turned or a new press is made. This wake only applies while SLEEP T is in effect, so a menu press after a timeout does not go to standby. (7ac2890, 1186af0, bbe9340, b59a217)
- **Wake from standby (zzz):** a button press heats the iron at once, as before.
- **Menu operations do not heat:** turning or pressing the knob in the menu never activates the heater. (7c7c555)
- **Leaving the menu:** by timeout or any other exit, the temperature returns to the Holder T or Sleep T in effect before the menu was entered. Sleep timers do not run while in the menu. (1b2ffe2)

## Presets (T1–T4)

- **Four presets:** T4 added, default 400 °C. (5efe9e7)
- **OFF:** any preset can be set to OFF in the settings menu. OFF presets are skipped when cycling. (eb3edd4)
- **Quick OFF:** the first click from a temperature goes straight to OFF, once per edit. The shortcut is re-armed when the setting is confirmed. After that, OFF is reached only by going below 100 °C or above 450 °C. (b8ae597)
- **Turning OFF back on:** restores the last temperature the preset had before it was set to OFF. Kept in EEPROM per preset. (07eb72d)

## Two-iron adapter (JBC C245 + C210)

- **Adapter support:** two irons on one adapter, only the active one heated. A double-press of the encoder switches irons. Each iron keeps its own set temperature. See `TWO_IRON_ADAPTER.md`. (36feb2d)
- **Automatic switching on 5.2C boards:** lifting an iron from its stand makes it the active one. (36e4dd2)
- **Iron re-identification on switch:** switching irons runs the normal identification again, which fixed intermittent single-iron recognition. (75a1f28)

## Ramp mode (soldering profiles run by the station)

- **RAMP MODE** (menu item, never saved: after power-up the station is always the normal station): the station runs a soldering profile by itself, like JBC's Dynamic Soldering Profiles. It holds the idle temperature (default 165 °C, below the melting point of Sn62) until a profile starts. The knob, sleep, stand-by and HOLDER T do not change the temperature in this mode; it ends after the idle time-out (default 10 minutes) and falls back to the normal sleep logic.
- **Start by lifting the iron:** the profile starts a set delay after the iron leaves its stand (start delay 0–10 s, default 3 s, time to wipe the tip and put it on the pad). A short knob press, or Start in the app, also starts it. If the tip is above the ready limit (default 200 °C) the station shows WAIT and starts as soon as it has cooled to it. The stand is the same signal as HLD on the display, whatever the HOLDER setting is.
- **Step profiles:** up to 6 steps, each with a temperature, a rate (°C per second, up to the maximum rate, default 4 °C/s) and a hold time. Each step ramps from the previous temperature and then holds. After the last step the heater goes back to idle and a lockout (default 5 s) runs. Default profile (tin-lead, checked against the Kyocera AVX limits for a TCJ polymer tantalum D-case): 190 °C at 2 °C/s held 20 s, then 250 °C at 2 °C/s held 5 s.
- **Abort:** a knob press, the app, putting the iron back in its stand or opening the menu stops a running profile.
- **Display:** the top line shows READY LIFT, START IN 3s, WAIT, the step and the seconds left in the whole profile (for example S1/2 50s HOLD) and LOCKOUT.
- **Stored profiles:** up to 10 named profiles are kept in the station's EEPROM. The new menu item **R.PROFILE** shows them one at a time (name, steps, ACT for the active one): turn to pick, press to load, hold 1 s to go back. Profiles are stored, loaded and deleted from the app.
- **Commands (protocol 7–10):** ramp values 0x50/0x51, enter/leave/start/abort 0x52, status 0x53, steps 0x56/0x57, stored profiles 0x58–0x5B. Values are in the free EEPROM area at 384, stored profiles at 2048.

## Remote control and wireless link

- **Remote control commands:** the station answers USB commands to read its state, set the temperature (same limits as the knob), select a preset, sleep/wake, switch iron, and read/write the settings block (backup and restore).
- **Settings over USB/Bluetooth (protocol 4):** read all menu settings and change one at a time, applied at once with the same rules as the menu (commands 0x40/0x41), and read/set the power-up name (0x42/0x43). The station refuses while it is in its own menu. INPUT, calibration, instrument info, rotation, buttons, menu down and the pincode stay device-only.
- **UART2 link for a Bluetooth bridge:** the same commands also work over UART2 (RF4 receive, RF5 transmit, 115200 baud), framed with a CRC. Live data is sent only while the app is sending. Firmware update is only possible over USB.
- **ESP32 Bluetooth bridge** (`software/esp32/UniSolderBridge`): Bluetooth Classic link between phone and station, protected by a PIN (default 0000, changeable from the app). The workflow "Build ESP32 bridge" builds the file to flash.
- **Saved PID values (protocol 2):** the PID and Gain values in use for the connected iron can be saved in the station and are applied whenever that iron is identified (8 slots, one per iron ID). "Restore the standard values" forgets them (commands 0x15–0x17).
- **Backup of the saved PID values (protocol 5):** the saved PID slots can be read and written (commands 0x44/0x45), so the settings backup file includes them.
- **Iron in its stand** is reported in the state reply (flag 32, protocol 6).
- **Room sensor glitch filter:** single bad readings of the room temperature sensor (more than about 4 degrees from the running average) are ignored, which removes short spikes in the displayed temperature.

## Windows application build

- **Build from the solution file** (`UniSolder.sln`, Release). (a242419)
- **No ClickOnce manifest signing**, so the build succeeds on the CI machine. (3afd75c)
- **Tabs like the Android app:** Live, Control, PID and Firmware, with the remote control commands and the Control tab. The temperature and PID sliders are yellow.
- **.NET 10 build:** the Windows application is migrated to .NET 10 (SDK-style projects, HidSharp instead of the old USB layer), with USB communication fixes. A separate workflow, "Windows Unisolder application (.NET 10)", publishes a single-file exe that runs without installing .NET. (68e7b9f)

## Android application

- **Station settings page** (Control tab, needs firmware protocol 4 for the name, 3 for the rest): T1-T4, holder and sleep temperature, sleep and stand-by time, wake up, holder sensor, degrees, brightness, sound and pitch, temperature and display step, sensor filter and the power-up name, in grouped cards. Swipe right or Back closes it.
- **Android version of the Windows application** (`software/Android`, needs a phone with USB host / OTG): connects to the station over USB and has three tabs. *Live* shows a scrolling temperature chart (set temperature, actual temperature, duty, power, reached, filtered and ADC, each switchable, with pause). *PID* reads and sets Kp, Ki, DGain, OVFGain and Gain. *Firmware* checks a `.hex` file and updates the station through the bootloader, with a confirmation and a progress bar.
- **Later additions:** *Control* tab (set temperature, presets, sleep, switch iron, station info, settings backup/restore), *Tools* tab (session recording with CSV export, tip health, alerts, bench dashboard), PID step-response analysis and saved PID sets, extra diagnostic chart traces, and a Bluetooth connection with PIN to the ESP32 bridge.
- **Ramp mode page** (Control tab, button Ramp mode): enter and leave ramp mode, start and abort, live state (step, time left, start delay), the ramp values, the start setting and delay, the maximum rate, a step table, and the profiles stored in the station with Load, Save here (under a name) and Delete.
- **Settings backup** also holds the saved PID values, the ramp mode values, the active profile and the stored profiles. Restoring writes them back; older backup files leave them as they are.
- **Yellow accent:** sliders, controls and the firmware update progress bar use the icon's yellow (#FFC107) instead of the system blue.
- **App icon:** a yellow U that bends into a temperature curve up to a dashed set-temperature line.
- **Build:** the workflow "Build Android app" builds the APK and publishes it as a GitHub Release. Builds are signed with a fixed key so each can be installed over the previous one.

## Build workflows

- **Firmware test builds:** a push to any branch other than `display-step` that changes the firmware builds it automatically (artifact only). Builds of `display-step` are started by hand and publish the release with the `.hex`.
