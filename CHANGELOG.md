# Changelog

Changes in the `display-step` branch of the fork, on top of sparkybg/UniSolder-5.2 `main`.
Newest first. Later entries supersede earlier ones where they overlap (noted below).

## 2026-11-03

- **07eb72d** Presets: turning OFF back on restores the last temperature. Each preset remembers the temperature it had before being set to OFF (four hidden settings, stored in EEPROM). Turning an OFF preset back on, in either direction, restores it instead of 100 °C.
- **b8ae597** Presets: the OFF jump is used once per edit and re-armed on confirm. The first click from a temperature goes to OFF once; after that OFF is reached only by going below 100 °C or above 450 °C. Supersedes 006cfb2 and 54160dc.
- **006cfb2** Presets: the first click of each turn jumps to OFF. Superseded by b8ae597.
- **54160dc** Presets: first turn jumps to OFF, or on from OFF. Superseded by 006cfb2.
- **b16acf9** Button hold: menu after 1 s, standby after 3 s. Named constants `MENU_HOLD_TICKS` (50) and `STANDBY_HOLD_TICKS` (150). Supersedes the 2 s / 5 s timings below. Other hold timings are unchanged.

## 2026-11-02

- **eb3edd4** Presets can be turned OFF in the settings menu. T1–T4 accept 0 (OFF). OFF slots are skipped when cycling. Editing steps from OFF straight to MINTEMP, so 1–99 °C cannot be set.
- **baa0e79** Manual sleep uses the standby mode and its HOT/ZZZ screensaver. The 5 s hold enters standby instead of showing SLP. Wake-up: knob turn, button press, or lifting the iron from its stand (if it was in the stand when sleep started).
- **6ceb1bd** Manual sleep switches the heater off instead of holding the sleep temperature. Supersedes the SLEEP T behaviour from 0415f65.

## 2026-10-30

- **e65ddb1** Display: send only changed rows, faster fill and invert. `OLEDUpdate()` skips rows that have not changed since the last update. `OLEDFill` uses `memset`; `OLEDInvert` uses a pointer loop.
- **5efe9e7** Add a fourth reset temperature preset (T4, default 200 = 400 °C). Stored in a new byte at the end of the settings block. VERSION moves to index 23.
- **abfe2dd** Revert "Add boost: double-click the knob…" (aaf5ee1).
- **21ab884** Revert "Boost: hold the knob and turn it instead of double-clicking" (ed9569f).
- **ed9569f** Boost: hold the knob and turn it. Reverted in 21ab884.
- **aaf5ee1** Add boost: double-click the knob for a preset temperature for a preset time. Reverted in abfe2dd.
- **75a1f28** Switch irons by re-running identification instead of a loop check. Fixes intermittent single-iron recognition after the two-iron change.
- **31a3934** Move two-iron reload check to the end of `IronTasks`. Superseded by 75a1f28.
- **3afd75c** Windows build: do not sign ClickOnce manifests (`/p:SignManifests=false`).
- **a242419** Build Windows app from the solution file. The workflow pointed MSBuild at a publish profile and web deploy target that do not exist in this project. It now builds `UniSolder.sln` in Release.

## 2026-09-30

- **36e4dd2** Two-iron adapter: automatic iron selection from two stands (5.2C). Lifting an iron makes it the active one. Decoding is debounced (0.2 s) and tolerant of 5% resistors. Only active with the adapter on a 5.2C board.
- **36feb2d** Add switchable two-iron adapter support (JBC C245 + C210). Adapter ID `0x1705`. Only the active iron is heated. Double-press the encoder to switch. Each iron keeps its own set temperature. See `TWO_IRON_ADAPTER.md`.
- **0415f65** Add 5-second encoder press to enter sleep mode. Superseded by baa0e79 and 6ceb1bd (sleep behaviour) and b16acf9 (timings).
- **f40e69d** Add DISP.STEP menu setting for displayed temperature resolution. Steps of 2–5 degrees round the measured temperature shown on the main screen, with half a degree of hysteresis. Works in C and F. The setting's value 1 keeps the original display (no rounding), so 2 is the smallest rounding step.
