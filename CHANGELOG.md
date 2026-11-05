# Changelog

Features and changes implemented in the `display-step` branch of the fork, on top of sparkybg/UniSolder-5.2 `main`.
Only what is in the firmware now is listed. Reverted and superseded changes are left out.

## Display and settings

- **DISP.STEP** (menu, 2–5 degrees): rounds the measured temperature shown on the main screen, with half a degree of hysteresis. Works in C and F. The value 1 keeps the original display. (f40e69d)
- **Standby screen:** the text "ready when you are" replaces HOT and ZZZ. It stays still and jumps to a random position every 30 seconds. There is no longer a HOT warning on this screen.
- **Welcome screen with your name:** at power-up the boot logo is replaced by "Hi <name>!" (for example "Hi Foppe!"). The name is set in the new NAME menu item, up to 10 characters: turn the knob to change the character, press for the next one, hold 1 s or press on the last one to save. A changed name shows at the next boot. With no name set, the logo is shown as before. The name is stored in the EEPROM when saved.
- **Menu pincode:** a PINCODE menu item sets or clears a 4-digit pincode (four dashes clear it). With a pincode set, opening the menu asks for it first. Without one the menu opens as before. Holding the button for the whole boot screen clears a forgotten pincode.
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

## Windows application build

- **Build from the solution file** (`UniSolder.sln`, Release). (a242419)
- **No ClickOnce manifest signing**, so the build succeeds on the CI machine. (3afd75c)
- **.NET 10 build:** the Windows application is migrated to .NET 10 (SDK-style projects, HidSharp instead of the old USB layer), with USB communication fixes. A separate workflow, "Windows Unisolder application (.NET 10)", publishes a single-file exe that runs without installing .NET. (68e7b9f)
