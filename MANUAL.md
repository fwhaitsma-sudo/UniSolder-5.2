# UniSolder app manual (Android and Windows)

Last updated 2026-11-25. This manual covers the Android app and, at the end, the Windows app of this fork.

## What the app is

The app connects to the UniSolder station over a USB cable or Bluetooth and works as a remote panel and workbench. It has six tabs: Live, Control, PID, Tools, Firmware and More.

- Works with any firmware: Live, PID, Tools, Firmware.
- Needs the remote-control firmware (the newest `fw-*` release): the Control tab, station settings, the power-up name and settings backup. Station settings need remote control v3, the name v4. The Control tab shows the version.
- The station keeps its own limits. The app cannot switch the heater off or exceed the station's maximum.

## What you need

- An Android phone or tablet with USB host (OTG) support, and a USB cable or OTG adapter that fits the station's USB port.
- A UniSolder 5.2 or 5.2C station. The Control tab and station settings need the remote-control firmware.
- For firmware updates: a `.hex` file built for the bootloader (`PIC32_with_bootloader`), stored on the phone.

## Installing

1. On GitHub, open the fork's Releases page and download the `.apk` of the newest `android-*` release.
2. Open the file on the phone and allow installing from that source when Android asks.
3. To update, install a newer APK over the old one. All builds are signed with the same key, so settings, saved PID sets and recordings are kept.

The app asks for no storage access. Files you export or back up are written through Android's file picker, where you choose the location.

## Connecting

1. Switch the station on and plug it into the phone. Android asks for USB access for the app: allow it.
2. The line at the top shows the connection status. If it says "Not connected", press **Connect** to try again.
3. When the station's iron is changed (two-iron adapter), the app notices, re-reads the PID values and forgets the values it remembered for the previous iron.

While a firmware update runs, the app keeps the screen on and ignores the back button.

## Bluetooth

With an ESP32 bridge built into the station, the app can connect over Bluetooth instead of the USB cable. Everything on the Live, Control, PID and Tools tabs works the same. A firmware update does not: it is refused over Bluetooth and needs the USB cable.

### Unlocking Bluetooth

Bluetooth needs extra hardware (the ESP32 module in the station), so it is locked in a new installation: the **Bluetooth** button is hidden and the app never tries it. To unlock it, open the **More** tab (the last tab), press **I have the hardware: unlock Bluetooth** and confirm. The same tab has a button that opens the build instructions on GitHub, and **Turn Bluetooth off** locks it again and hides the button.

### Pairing and connecting

1. In Android's Bluetooth settings, pair the phone with the device named **UniSolder**. No code is asked at this step.
2. In the app, press **Bluetooth** (top right) and pick **UniSolder** from the list of paired devices. On Android 12 and newer, allow the Bluetooth permission when asked.
3. Enter the PIN. The default is **0000**.
4. The app remembers the device and the PIN and connects by itself each time it starts, retrying every 8 seconds while the station is out of reach. The range is several metres, also through a wall.

The status line says **Connected**. The button at the top right shows the connection in use: **USB** with the cable plugged in (pressing it does nothing), **Bluetooth** while connected over Bluetooth (pressing it opens the bridge settings, only possible with the cable unplugged). A USB cable always takes priority over Bluetooth. To stop using Bluetooth, press **Bluetooth** and choose **Do not use Bluetooth**.

### Changing the PIN

While connected over Bluetooth, press **Bluetooth** and choose **Change the bridge PIN…**. A PIN is 4 to 8 digits and is stored in the bridge, not the station. After the change the app disconnects and reconnects with the new PIN. Flashing the full bridge file again resets the PIN to 0000.

### If it does not connect

| Message or symptom | What to do |
| --- | --- |
| Wrong PIN | The app does not retry on its own. Press **Bluetooth**, choose the device and enter the PIN again. |
| Locked | After 5 wrong PINs the bridge refuses all attempts for 60 seconds. Wait, then try again. |
| The bridge did not answer | The bridge has old software without the PIN check. Flash the current bridge file. |
| Device not in the list | Pair it first in Android's Bluetooth settings. |
| Nothing happens, status stays at connecting | Check that the station is switched on. The bridge LED is lit while a phone is connected and the PIN was accepted. |

## Live tab

- **Readout**: set and actual temperature in °C. The actual temperature has 2 °C resolution.
- **Heater bar**: heater power in percent; 100% means the heater is on all the time.
- **Chart**: the last 500 samples. Tap a series name under the chart to show or hide it: Set, Temp, Power, PID input (the temperature the PID loop reacts to, off by default; useful when tuning), Filtered, ADC Δ, Ω (the heater resistance, on its own axis on the right in ohm; readings of 50 Ω and more are ignored), Wave sh. and Flag.
- **Tip change**: the station sends no flag when the tip is out, and the sensor then reads nonsense. The app notices a jump of more than 40 °C or a reading outside 1–480 °C, shows "No tip" and leaves a gap in the traces instead of spikes. It continues 8 calm samples after the new tip is in. Nothing is recorded or checked (tip health, overheat alert) while the tip is out.
- **Pause** freezes the chart and readout (recording and alerts keep running).
- **Record** starts or stops a recording (see the Tools tab).
- **Dashboard** opens a full-screen bench view: large actual temperature (green when the set temperature is reached), set temperature and a heater power bar. The screen stays on; tap it or press back to close.

## Control tab (needs the remote-control firmware)

- **Set temperature**: slider and −/+ buttons that move in the station's own step. Changes are sent after a short pause; the station clamps them to 100–450 °C and beeps like a normal change.
- **T1–T4**: select a preset. **Sleep / Wake**: put the iron to sleep or wake it, as the button on the station does.
- **Switch iron**: shown only when the two-iron adapter is present.
- **Ramp mode**: opens the ramp mode page (needs the ramp mode firmware, protocol 7); see the Ramp mode section below.
- Remote changes are refused while the station is in its menu.
- **Save settings / Restore settings**: backs up the station's stored settings (presets, holder and sleep temperatures, name and the other options), the saved PID values, and the ramp mode values, active profile and stored profiles to a text file you choose, and writes such a file back. The PIN code is never read or written. After a restore the station restarts to load the values.

## Station settings (needs remote control v3)

Press **Station settings** on the Control tab. The page shows the station's current values and applies every change at once. Use the slider and −/+ buttons for numbers, the buttons for choices.

- **Temperatures:** T1–T4 (100–450 °C, or OFF), Holder temperature, Sleep temperature.
- **Sleep and stand-by:** Sleep time (seconds with the iron in the stand, minutes otherwise; OFF), Stand-by time (minutes; OFF), Wake up (OFF, KEY, HOLD, BOTH), Holder sensor (OFF, ON, AUTO).
- **Display and sound:** Degrees (Celsius or Fahrenheit), Brightness (1–15), Sound (OFF or ON), Sound pitch (1–9).
- **Knob and sensor:** Temperature step (1–50°), Display step (1–5°), Sensor filter (0–255, 0 = off).
- **Name** (needs remote control v4): up to 10 characters (letters, digits, space, - and .). Type the name and press **Save**; the station shows it at power-up.

Close the page with **Back**, the Android back button, or by swiping right (not starting on a slider). Changes are refused while the station is in its menu: leave the menu first.

Input type, calibration, instrument info, display rotation, button direction, menu down and the menu PIN code can only be set on the station itself.

## Ramp mode (needs protocol 7; profile steps need 9, stored profiles 10)

Ramp mode makes the station run a soldering profile by itself: it holds a low idle temperature, runs the steps when you start it, then locks out briefly. Press **Ramp mode** on the Control tab, or choose RAMP MODE in the station's own menu. The phone is only an editor and a viewer: the profile runs in the station. After a power cycle the station is a normal station again.

- **Enter ramp mode / Leave ramp mode**, **Start ramp** and **Abort**. The state line shows what the station is doing: heating to idle, ready, starting in n s, step n ramping or holding, lockout. Cooling or WAIT means the tip is still above the ready limit.
- **Starting:** with Start set to 1 (the default), lift the iron from its stand: the profile starts after the start delay, so you can wipe the tip and put it on the pad. A short press of the knob or **Start ramp** also starts it. A press while it runs, or putting the iron back in its stand, aborts it. In this mode the knob, sleep and stand-by do not change the temperature.
- **Power:** the station's own limit (130 W) stays in force; ramp mode adds no extra limit.

### Values

| Value | Range | Default | Meaning |
| --- | --- | --- | --- |
| Idle temperature | 100–340 °C | 165 °C | Held until the profile starts, also in the stand (below the melting point of Sn62 solder, 179 °C) |
| Lockout after the profile | 0–60 s | 5 s | No new profile can start in this time |
| Ready up to | idle + 10 to highest step − 10 | 200 °C | A profile can start when the tip is not hotter than this; it then starts from the tip temperature |
| Time-out when idle | 1–60 min | – | After this time without use the station leaves ramp mode |
| Start | 0 or 1 | 1 | 1 = lift from the stand, 0 = knob or app only |
| Start delay after lifting | 0–10 s | 3 s | One decimal |
| Maximum rate | 0.5–100 °C/s | 4 °C/s | No step may ramp faster than this |

### Profile steps

A profile has 1 to 6 steps. Each step has a target temperature (100–350 °C), a rate (°C per second, up to the maximum rate) and a hold time (up to 600 s). Each step ramps from the previous step's temperature to its own at its rate, then holds it; after the last step the heater goes back to idle. The default profile suits tin-lead solder on a Kyocera AVX TCJ capacitor: 190 °C at 2 °C/s, hold 20 s, then 250 °C at 2 °C/s, hold 5 s (about 68 s in all). The highest step must be at least 10 °C above idle and the ready limit + 10.

Press **Save values in the station** to store the values and the steps. If the station refuses them, the message says why (a value out of range, or a profile is running: wait until it is finished).

### Stored profiles

The station keeps up to 10 named profiles (names up to 10 characters). **Load** makes one the active profile (the steps shown above). **Save here** stores the steps shown above under a name, checks them by reading the slot back, and makes them the active profile. **Delete** removes a profile; the active steps do not change. On the station, the menu item R.PROFILE chooses the stored profile.

The values, active profile and stored profiles are part of the settings backup file; a restore writes them back and deletes stored profiles that are not in the file.

## Tools tab

- **Recording**: records set temperature, actual temperature, heater power, heater resistance, filtered temperature and ADC five times a second to a CSV file. Start it here or with Record on the Live tab.
- **Sessions**: each recording with its duration, maximum temperature, average power and heat-up time. **Export** saves a copy through the file picker; **Delete** removes it.
- **Tip health**: shows the heater resistance now, its average and range. Press the baseline button with a good tip heated up; later readings are compared with it per iron ID (more than 10% higher: keep an eye on it; more than 25%: worn tip or poor contact).
- **Alerts:** a notification, vibration and message when the tip stays more than 25 °C above the target for 10 s without cooling. The check stays quiet while the tip cools after the target is lowered. Android asks for notification permission when you switch an alert on. Alerts work only while the app is running and connected, and cannot switch the heater off.

## PID tab

Moving a slider sends the value to the iron at once. **The iron does not store these values: they are lost when it restarts or the iron is changed.**

| Slider | What it does |
| --- | --- |
| Kp | Proportional gain: how hard the heater reacts to the temperature error |
| Ki | Integral gain: removes a lasting error; too high causes overshoot |
| DGain | Derivative gain, 0–32: damps fast changes |
| OVFGain | Overshoot prevention, 0–100 |
| Gain | Sensor amplifier gain. It scales the displayed temperature, so change it only to calibrate |

Below the sliders:

- **Read from iron** reloads the values. **Revert** puts back the values read at connect.
- **Saved PID sets:** save the current values under a name (kept per iron ID on the phone). **Load** sends a set to the iron.
- **Step response:** when the set temperature is raised by 20 °C or more, the app measures rise time (10–90%), overshoot, settling time (±3 °C) and steady error, and lists the last five runs with their PID values. **Run step test** raises the set temperature by 30 °C and puts it back (needs the remote-control firmware and an iron at temperature).
- **Calibration helper:** heat the iron to a steady temperature, measure the tip with a reference thermometer, and enter that value and the room temperature. The app suggests a new Gain: apply it and measure again.
- **Save the current values in the station** keeps the PID and Gain values for that iron; the station loads them whenever the iron is detected. **Restore the standard values** puts back the firmware's built-in values and removes saved or changed ones.

## Firmware tab

1. **Choose HEX file** and pick the file built for the bootloader. The app checks the format and checksums and shows the size.
2. **Update firmware** and confirm. The station jumps to its bootloader and reconnects; Android may ask for USB access again: allow it.
3. The app erases the flash, programs every record, then asks the bootloader to start the new firmware. Keep the cable connected and the app open until it says the update is finished.

If an update fails after the erase, the firmware is not marked valid and the station stays in bootloader mode, which is safe: retry the update, or power-cycle the station and try again. The log at the bottom of the tab shows each step.

## Troubleshooting

| Symptom | Try |
| --- | --- |
| Stays "Not connected" | Check cable and OTG support, allow the USB permission, press Connect, power-cycle the station |
| Control tab does nothing or shows no info | The station has the stock firmware: install the remote-control firmware |
| Remote changes ignored | The station is in its menu: leave the menu first |
| PID sliders greyed out | No values read yet: press Read from iron |
| Heater resistance empty | The station is not reporting it yet: heat the iron up |
| No alert notification | Allow notifications for the app; the app must be running and connected |
| Firmware update fails | See the Firmware tab: the station stays in bootloader mode; retry, or power-cycle and retry |
| Station settings button greyed out | The station firmware is older than remote control v3 (see the Control tab): update the firmware |
| No Name card in the settings | The name needs remote control v4: update the firmware |

## Windows app

The Windows app (the `win-*` release, one exe, no installation) has the same pages as the Android app: Live, Control, PID, Tools, Firmware and More. Station settings and Ramp mode open from the Control page and close with Back.

- **Connection:** USB first; Bluetooth is only used when no cable is connected. On the More page, choose the COM port of the paired UniSolder bridge (Bluetooth ports are listed first) and enter the PIN. If the status says the first query was not answered, the app asks again every 2 seconds over Bluetooth. A firmware update needs the USB cable.
- **Live page:** same chart as the Android app (traces, Ω axis, tip change). Samples are drawn at a steady pace, so the trace scrolls smoothly on any screen refresh rate, also over Bluetooth.
- **Settings backup:** the same file format as the Android app, so a backup from one can be restored with the other (station settings, saved PID values, ramp mode values and profiles).
- **Log:** messages are written to `%LOCALAPPDATA%\UniSolder\UniSolder.log`; double-click the connection text in the status bar to open it.

## Not in the app (yet)

- Control from the phone while the station is in its menu.
- Alerts while the app is closed or the cable is unplugged.
