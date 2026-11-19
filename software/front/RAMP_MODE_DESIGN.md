# Ramp mode: design note (firmware, step 1 and 2)

Status: step 1 written (2026-11-19), waiting for CI and hardware test. Branch `remote-control` only.

Decisions by the user (2026-11-19): one ramp + one dwell in version 1; idle temperature applies in the stand; peak maximum 350 C is fine; **no power cap below the existing 130 W limit** (the power cap parameter and the PID.c change were dropped).
Goal: a separate **ramp mode** in the station. Outside this mode the station behaves exactly as it does today.
Inside it the station runs a heating ramp by itself (idle, ramp, dwell, cool down, lockout) and later starts it by itself when the tip touches a pad.

## 1. What the code already gives us (checked 2026-11-19)

| Fact | Where | Consequence |
|---|---|---|
| The temperature the PID regulates to is `CTTemp` (whole degrees C). It is set in one place, the end of `MenuTasks` (`CTTemp = i`), after sleep, holder, stand-by and manual sleep have been applied. | `menu.c` ~1302-1326 | One hook decides everything: in ramp mode `i` is replaced by the ramp target just before `CTTemp = i`. No change to the PID input path. |
| `MenuTasks` does its work every 2nd ISR tick, 50 times per second (20 ms). | `menu.c` 1169, 1274 | The ramp state machine can run on this 20 ms tick. A ramp of 45 C/s moves 0.9 C per tick, which is fine in whole degrees. |
| `PID()` runs from the ISR once per ADC cycle (about once per mains period per iron, so about 20 ms; half as often per iron with a two-iron adapter). The PID works in **0.5 C units** (`CTemp[0]` = degrees x 2). | `isr.c` 314, `PID.c` 254 | Earlier I wrote that the resolution is 2 C: that was the live frame in the app. The control loop itself has 0.5 C, so a 1.5 C window and a 2.5 C dip are measurable, but a dip is only 5 counts: it needs the filtered values. |
| The station runs on **AC or DC** (detected at power-up: `mainFlags.ACPower`). The heater is switched per half wave from an accumulator: `PWM += PIDDuty`, heater on when it overflows. | `main.c` 87-105, `isr.c` 329-340 | "Zero cross" is correct. Duty is an average over several half waves by design, so the demand value (`PIDDutyFull`), not the instantaneous heater pin, is the signal to look at. |
| PID demand: `PIDDutyP + PIDDutyI` (24 bit, `0x00FFFFFF` = 100%), then scaled for rated power and the 6 A limit, then `PIDDuty = pdt << 8`. Anti-windup exists (integrator limited so that P+I <= 100%). | `PID.c` 334-409 | A power cap goes in right after `pdt = P + I`, and the integrator is clamped to `cap - P` (the same pattern as the existing clamp). Default = no cap, so the classic path is unchanged. |
| When the target changes, `DestinationReached` is cleared, which switches off the waveshaping / overshoot cut-off until the target is reached again. | `PID.c` 80, 309-331 | During a ramp the target moves every tick, so this cut-off is inactive until the ramp ends. Overshoot at the peak must be measured on hardware. |
| Stand detection: `FNAP == 0` means the active iron is in its stand (only when the HOLDER sensor setting is ON or AUTO-with-holder). Flag 32 in the state reply is just this. | `menu.c` 1158, 1233-1263 | Ramp mode uses `FNAP` directly. With HOLDER = OFF there is no stand signal, so ramp mode needs HOLDER ON/AUTO and says so on the display if not. |
| EEPROM: 0-63 settings, 64-127 temperature log, 256-383 saved PID (8 slots), 512+ tip presets (paused). 384-511 is unused. | `iron.c` 1016, `pars.h` | Ramp parameters go to 384-415 with their own marker byte. Not part of the settings backup block (0-63); the app backs them up separately (like the PID slots). |
| Source files are listed in `nbproject/configurations.xml` and the three `Makefile-*.mk` files. | `nbproject/` | A new `ramp.c` needs entries in all of them (copy the `iron.c` lines). CI shows at once if one is missed. |

## 2. Design

### 2.1 Mode
- A runtime flag `RampMode`. **Never saved**: after power-up the station is always classic.
- Entered from the station menu (new action item RAMP MODE, like INST.INFO) or from the app. Left by the same item, by a long press of the knob (two seconds, as for the menu) or by the app.
- While in ramp mode the display header shows `RAMP` and the state.
- Classic behaviour inside ramp mode: sleep time, stand-by time, HOLDER T and the sleep temperature are **switched off**, because the ramp logic owns the target. To keep a forgotten iron safe, there is an idle time-out (parameter, default 10 minutes): when nothing happened that long, ramp mode ends and the classic sleep logic takes over again.
- Turning the knob in ramp mode does nothing to the target (the ramp parameters are the target). A short knob press = manual trigger (step 1) or abort (while running).

### 2.2 State machine (runs on the 20 ms tick, new file `ramp.c`)

```
            enter mode
                |
                v
   +-------> IDLE  (target = idle temperature, any time; iron in stand or not)
   |          |  trigger: knob press / app command / (step 2) contact detected
   |          |  only if iron is OUT of the stand
   |          v
   |        RAMP   target = idle + rate x t, up to peak   (rate in C/s)
   |          |  peak reached (actual >= peak - 3) or ramp time + 1.5 s passed
   |          v
   |        PEAK   target = peak, for the dwell time
   |          |
   |          v
   |        COOL   target = idle; ends when actual <= idle + 5, or after 10 s
   |          |
   |          v
   +------ LOCKOUT  target = idle; no trigger accepted for the lockout time
```

Abort (any of RAMP, PEAK): knob press, menu opened, iron put in its stand, tip change holder, new trigger from the app with "stop" -> go to COOL at once (no more heating).
Fault (heater open, short circuit, no sensor, tip change in progress): target 0 and leave ramp mode with a beep and a message.
Every state has a time limit, so the machine can never wait for ever (this is the weakness of the app's "wait for temperature" step):
RAMP <= (peak - idle) / rate + 1.5 s, PEAK = dwell, COOL <= 10 s, LOCKOUT = lockout time.

Ramp target in integer math (C89, no FPU): `target = idle + (n * rate) / 50`, with `n` the tick count since the trigger and `rate` in whole C/s. 32-bit `int`, no overflow (n <= 600, rate <= 100).

### 2.3 Parameters (stored in EEPROM 384.., marker `0xD3`, defaults in brackets)

| Name | Unit | Range | Default |
|---|---|---|---|
| idle temperature | C | 150..350 | 155 |
| peak temperature | C | idle+10..350 (hard maximum 350, also below MAXTEMP) | 275 |
| ramp rate | C/s | 5..100 | 45 |
| dwell | 0.1 s | 0..100 | 11 |
| lockout | s | 0..60 | 12 |
| idle time-out | min | 1..60 | 10 |
| contact: duty jump | % | 3..40 | 12 (step 2) |
| contact: temperature dip | 0.5 C | 2..20 | 5 (step 2) |

The user decides the real values; the defaults above are the numbers from the review prompt. The peak limits for the polymer capacitor in the example are the user's responsibility (datasheet), not mine. Version 1 has one ramp and one dwell. A step table (preheat, soak, reflow, like the app's profiles) can follow later, the state machine is written so it can loop over steps.

### 2.4 Contact detection (step 2)
Runs on the same 20 ms tick, reads `PIDVars[0]` (iron 0 only in v1; with a two-iron adapter the active iron).
1. **Settled**: out of the stand and |actual - idle| <= 1.5 C for 1.5 s.
2. **Baseline**: exponential average of `PIDDutyFull` (shift 4, integer), only while settled.
3. **Trigger** when settled and either: `PIDDutyFull - baseline` >= duty jump for 3 ticks in a row, or `idle - TAvgP` >= dip for 2 ticks in a row. Latency about 40-60 ms, set by the PID cycle and not by Bluetooth.
4. Feeding solder wire also draws heat and will trigger. That is accepted for this mode; the lockout and the knob abort are the safeguards.
5. **The numbers are not guessed**: step 2a is a recording of duty, filtered temperature and error at 20 ms during 10 real contacts (pad and ground plane), made with the existing recorder if its rate is high enough, otherwise with a small capture buffer in the firmware that the app reads back. The thresholds are chosen from that data.

### 2.5 No phone needed, no heartbeat
The review prompt had a 5 s disconnect fail-safe because the phone ran the ramp. Here the station owns the state machine, so the phone is only an editor and viewer. A lost connection changes nothing; the station-side limits (time limits, abort, idle time-out) are the safety net.

## 3. Protocol 7 (backward compatible: protocol byte 6 -> 7, old commands unchanged)

| Cmd | Request | Reply |
|---|---|---|
| 0x50 get ramp parameters | - | [status, 8 values as u16 in the order of 2.3] |
| 0x51 set ramp parameters | [key 0x5A 0xA5, 8 x u16] | [status] 0 ok, 1 bad key, 2 out of range, 3 station is running a ramp. Saved to EEPROM 384.. |
| 0x52 ramp control | [0 leave mode, 1 enter mode, 2 trigger, 3 abort] | [mode, state] |
| 0x53 ramp status | - | [state, ready, u16 ms left, u16 target, u16 highest temperature since the trigger, u16 actual] |

The live frame and the state reply (0x10) stay as they are. The app polls 0x53 about 5 times per second, only while its Profiles/Ramp page is open.

## 4. Files and functions

| File | Change |
|---|---|
| `ramp_core.h` (new) | State machine, parameters (load, check, save), `RampTick()` called from `MenuTasks`, enter/leave/trigger/abort, status for the protocol, contact detection (step 2). It is **included by menu.c** instead of being a separate .c file, because a new .c file needs entries in `nbproject/configurations.xml` and the generated `Makefile-*.mk` files. |
| `menu.c` | One hook before `CTTemp = i` (`if(RampMode) i = RampTick(i)`), skip sleep and stand-by logic in ramp mode, menu item RAMP MODE (action), header text, knob press and long press handling, abort when the menu opens. Remote helpers `RemoteRamp*`. |
| `menu.h` | Declarations; `RAMP` display mode for the app's `inMenu` handling if a screen mode is needed. |
| `PID.c` / `PID.h` | **No change** (the power cap was dropped). |
| `io.c` | Commands 0x50-0x53, protocol byte 7. |
| `pars.h` | `RAMP_EEP_ADDR 384`, marker. |
| Android | Later: a Ramp page next to the profiles (parameters, status, enter/leave/trigger/abort, backup of the 8 values in the settings file). |

## 5. Steps

1. **Step 1 (first build)**: mode, menu item, state machine with manual trigger (knob press or app), parameters in EEPROM with defaults, power cap, protocol 7 commands, minimal app page to set the values and show the state. Test on hardware: ramp speed reached, overshoot, cap.
2. **Step 2a**: recording of real contacts, thresholds chosen from the data.
3. **Step 2b**: contact detection with those thresholds.
4. Afterwards, if wanted: a step table (preheat/soak/reflow), per tip parameters (after TIP PRESET, which stays paused).

## 6. Decided
All four open points were answered on 2026-11-19 (see the top). The capacitor's own limits are not checked by the firmware: the TCJ datasheet (Kyocera AVX) gives no hand-soldering limits, only 3 reflow cycles according to J-STD-020.
