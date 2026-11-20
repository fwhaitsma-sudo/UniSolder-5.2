# Ramp mode: design note (firmware, step 1 and 2)

Status: step 1 tested on hardware (2026-11-19). Step 2b (contact start, 2.6) tested 2026-11-20: not reliable, kept as an experimental option. 2026-11-20: step profiles with the JBC lift start (2.7, protocol 9) written and simulated, waiting for the hardware test. Branch `remote-control` only.

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
| contact: cooling constant | 0.001/s | 5..50 | 16 (step 2, proposed, see 2.4 A) |
| contact: blank after lift | s | 0..10 | 3 (step 2, see 2.4 brass wool) |

The user decides the real values; the defaults above are the numbers from the review prompt. The peak limits for the polymer capacitor in the example are the user's responsibility (datasheet), not mine. Version 1 has one ramp and one dwell. A step table (preheat, soak, reflow, like the app's profiles) can follow later, the state machine is written so it can loop over steps.

### 2.4 Contact detection (step 2) - REMOVED 2026-11-20 (not reliable in hardware tests; see 2.7). Kept here as history.
Runs on the same 20 ms tick, reads `PIDVars[0]` (iron 0 only in v1; with a two-iron adapter the active iron).
Two detectors, because the tip is in one of two situations when the user touches a pad:

**A. Cooling detector (heater off, tip above its target).** This is the normal case between ramps: after a ramp the tip stays far above idle for 30-40 s (test 2 and 3), so the heater demand is 0 and there is no duty signal at all. The signal is how fast the tip cools.

The raw cooling rate depends on how hot the tip is, so it is normalised by the temperature above the room (cold junction):
`k = cooling rate / (T - T_room)` (unit 1/s). Free cooling gives a nearly constant k; contact with a pad roughly doubles it.

Data from test 3 (`test_logs/ramp_test_3_contact.csv`, C245, 2026-11-19, two contacts confirmed by the user just before the manual trigger; T_room = actual - adc = 16.5 C):

| Segment | Tip temperature | Cooling rate | k (1/s) |
|---|---|---|---|
| free, 0-6 s | ~205 C | 1.6 C/s | 0.008-0.009 |
| free, 15-20.5 s | ~248 C | 2.4 C/s | 0.010-0.011 |
| free, 33-37 s | ~253 C | 2.3 C/s | 0.009-0.011 |
| **contact, 6-8.4 s** | ~197 C | 3.75 C/s | **0.021** |
| **contact, 20.5-27 s** | ~225 C | 5.0 C/s | **0.022-0.025** |
| heater just switched off, 12.5-15 s / 31-33 s | ~265 C | 5.6-6.6 C/s | ~0.023-0.027 (false!) |

Proposed rule (thresholds to be confirmed in step 2a):
1. **Armed** when: ramp mode, iron out of the stand **for at least the lift blank time** (default 3 s, see below), state IDLE or LOCKOUT, and `PIDDutyFull == 0` for at least **3 s**. The 3 s blanking hides the fast drop right after the heater switches off (heat spreading from the heater into the tip), which looks exactly like contact (last row of the table).
2. **Rate**: ring buffer of 50 filtered temperatures (`TAvgP`, 0.5 C units, one per tick = 1 s). Rate = (mean of the oldest 25 - mean of the newest 25) per 0.5 s. Integer only, 200 bytes RAM. (A least-squares slope is better but not needed for a 2x difference.)
3. **Room temperature**: `CJTemp` (0.5 C units). If there is no cold junction sensor (`CJTemp == -273*2`) use 25 C.
4. **Contact** when `rate_per_s * 1000 >= k_threshold * (T - T_room)` for **25 ticks in a row (0.5 s)**. Default `k_threshold` = 16 (= 0.016/s, halfway between 0.011 and 0.021). Expected latency 1-1.5 s, about the same as the user's own reaction in test 3.
5. **Ready limit**: the contact condition is evaluated all the time, but a ramp only starts when the tip is also at or below the ready limit (option B). Because k stays high as long as the tip touches the pad, a contact at 240 C (above a 230 C ready limit) starts the ramp as soon as the tip has cooled to 230 C, about 2 s later at 5 C/s. No latch needed.

**B. Duty detector (heater regulating, tip at idle).** The original plan, for the case where the tip has cooled down to idle and the PID holds it there:
1. **Settled**: out of the stand and |actual - idle| <= 1.5 C for 1.5 s.
2. **Baseline**: exponential average of `PIDDutyFull` (shift 4, integer), only while settled.
3. **Trigger** when settled and either: `PIDDutyFull - baseline` >= duty jump for 3 ticks in a row, or `idle - TAvgP` >= dip for 2 ticks in a row. Latency about 40-60 ms.

Both detectors start the same RAMP state. Common points:
- Feeding solder wire also draws heat and will trigger. That is accepted for this mode; the lockout and the knob abort are the safeguards.
- **Things that may also raise k without real pad contact**, to be recorded in step 2a before the threshold is final: putting the iron in the stand (metal stand touching the tip; stand detection should block this anyway), wiping on a wet sponge or brass wool, waving the iron through the air, large copper area vs small pad. If wiping crosses the threshold, an extra arming condition is needed (for example: only after the iron was lifted from the stand, or within a window after a knob press).
- **Brass wool (DECIDED 2026-11-20, user: option 1, "for now").** The 310 C logs (`test_logs/2a_310C_*.csv`) show brass wool drawing about 5x more heat than a small pad, and the user expects the same with the heater off. So no threshold can separate a wipe from a pad. Fallback: **blank after lift**. Contact detection is ignored for a set time after the iron leaves the stand (`FNAP` goes from 0 to 1), to cover the wipe that usually follows. The timer restarts on every lift. Known limitation, accepted: a wipe later on, without putting the iron back in the stand first, still starts a ramp (harmless: the tip heats to the peak in the wool, then the lockout runs). Rejected for now: knob press to arm, and "first contact after a lift is the wipe".
- **The numbers are not guessed**: step 2a records duty, filtered temperature, CJ temperature and computed k at 20 ms during about 10 real contacts per case (pad, ground plane, and the false cases above), with a small capture buffer in the firmware that the app reads back if the 5 Hz recorder is too slow. The thresholds are chosen from that data.
- New parameter (10th): **contact: cooling constant**, unit 0.001/s, range 5..50, default 16. Changes the EEPROM format again (new marker; older formats still read), decided at step 2b.

### 2.6 Step 2b as built (2026-11-20, protocol 8) - REMOVED 2026-11-20 together with the capture buffer (0x54/0x55); history only
Written in `ramp_core.h` (`RampDetect`), called every 20 ms tick in IDLE and LOCKOUT. Differences from the plan above, found with a PC simulation (stubs, a thermal model with sensor noise, and test 3 replayed at 50 Hz):
- **Heater-off blanking is 5 s, not 3 s.** In test 3 the post-heater drop still gave k = 0.020 at 3 s and 0.015 at 3.2 s: with 3 s it gave a false contact.
- **Hysteresis**: once a contact has begun it holds while k >= threshold - 0.003 (in test 3 the first contact rose slowly and flickered around 0.016).
- **Contact during the lift blank** sets the wipe hold: that contact must end (0.5 s without contact) before a new one counts. Without this a wipe that lasts past the blank time started a ramp.
- **Wipe check (ripple)**: the rate in 0.1 s steps (sums of 5 raw samples) over the last 0.9 s; ripple = standard deviation above the noise floor / mean rate. The noise floor is the variance learned while the tip cools freely (noise and strokes add as variances). The check uses the mean ripple over the second half second of contact, so with the check on a start needs 1 s of contact (latency about 1.5 s). Simulation: pad contacts 20 of 20 started; simulated 3 Hz strokes were caught in 6-7 of 10 (the rest started a ramp in the wool). Default 30 %, 0 = off. **This is a first guess**; the real value comes from captures.
- Detector B (duty, tip regulated at idle) is built but only active with automatic start = 2. The 310 C logs show it cannot tell a small pad from noise.
- New parameters 10-13: cooling constant (default 16 = 0.016/s), blank after lift (3 s), wipe ripple (30 %), automatic start (0 off, **1 cooling detector = default**, 2 also duty). Duty jump default 12 -> 5 %, dip 5 -> 3 (x 0.5 C). EEPROM mark 0xD5 (13 values; 0xD4 and 0xD3 still read; old contact defaults are replaced by the new ones).
- Display: READY AUTO when automatic start is on, WIPE IGNORED while the wipe hold is active.
- **Capture buffer** (for step 2a at 20 ms): 500 records x 8 bytes (10 s, 4 KB RAM): raw temperature (0.5 C), filtered (1/16 C), duty /255, k, ripple, state + detector flags. Recording in ramp mode; it freezes 2 s after a start (manual or automatic) or an ignored wipe; the app reads it and saves a CSV (time 0 = the event) next to the recordings.

Protocol 8: 0x50/0x51 carry 13 values (a protocol 7 app reads 9; a 9 value write leaves values 10-13 as they are), 0x53 adds k, ripple, flags, last event, counters and the room temperature (bytes 10-21), 0x54 capture control/status [0 status, 1 record again, 2 freeze], 0x55 read records [u16 first] -> [u16 first, n, n x 8 bytes].

### 2.7 Step profiles and the lift start (2026-11-20, protocol 9)
Hardware tests of 2.6: the contact start worked sometimes, not always. Research (JBC Dynamic Soldering Profiles, JBC Soldering Assistant, Hakko patent US 10,974,335): JBC starts a profile when the tool is lifted from the stand (after a set "start delay") or by a pedal; power-based joint detection exists (JBC Soldering Assistant, Hakko) but only to grade joints at working temperature, not to start heating. DECIDED (user): JBC method, start delay configurable, default 3 s; profiles adapted to the capacitor.

**Capacitor limits (Kyocera AVX tantalum technical summary and application guide):** reflow lead-free: preheat 150 +-15 C for 60-120 s, peak 245 +-5 C, max 2.5 C/s, max 40 s above 230 C; SnPb: preheat 150 +-15 C for 60-90 s, peak 220 +-5 C, max 2 C/s, max 60 s above the solder melting point; hand soldering: tip max 370 C, max 3 s, but TCJ is "not dedicated to hand soldering" (no approved hand method). JBC publishes no profile values; its profiles are slow (screen shows about 1.5 min) and have a maximum rate setting (4.0 C/s shown).

**Default profile (tip temperatures, tin-lead Sn62, TCJ D-case on a signal pad, tip on the pad from the start):**

| Step | Tip | Rate | Hold |
|---|---|---|---|
| start delay | idle 165 C | - | 3 s after the lift (wipe, place the tip with flux) |
| 1 preheat/soak | 190 C | 2 C/s | 20 s |
| 2 reflow/peak | 240 C | 2 C/s | 5 s |
| end | heater back to idle, lockout 5 s | | |

About 63 s in all; the tip is above 179 C for about 50 s (the joint less): inside the SnPb 60 s limit; ramp at the SnPb maximum of 2 C/s; the joint should reach about 215-225 C at a 240 C tip (tip to joint difference not measured yet). Ground plane: start with soak 30 s, peak 255 C. The old single ramp (45 C/s to 275 C, 1.1 s) was about 20 x the reflow ramp limit.

**Firmware (ramp_core.h):**
- Parameter 12 is now the start: 0 knob/app only, 1 lift + delay (default), 2 contact (cooling), 3 contact + power (both experimental, as in 2.6). The knob and the app can always start a profile.
- New parameters 13 start delay (0.1 s, 0-100, default 30), 14 maximum rate (0.1 C/s, 5-1000, default 40), 15 number of steps (1-6, default 2). Lockout default 12 -> 5 s, ready limit default 230 -> 200 C. Parameters 1-3 (single ramp peak, rate, dwell) are no longer used. EEPROM: parameters mark 0xD6 (16 values; 0xD5/0xD4/0xD3 still read; when an older format is read the start becomes 1 and the ready limit at most 200), steps at 420 (mark 0xE1, 6 x (C, 0.1 C/s, 0.1 s)).
- Lift start: when the iron leaves its stand in IDLE or LOCKOUT, the delay counts (display START IN 3s); after it, the profile starts as soon as the station is ready (WAIT while the tip is above the ready limit or the lockout runs). Putting the iron back cancels it. Uses the stand signal FNAP whatever the HOLDER setting is (the same signal as HLD on the display and HOLDER T); before 2026-11-20 13:50 it also needed HOLDER on or auto, so with HOLDER off the stand was ignored while HLD was shown.
- Each step ramps the target from the previous step's temperature (step 1: the tip temperature, between idle and step 1's temperature) at its rate, then holds; the hold starts when the target is there and the tip is within +-5 C, or 1.5 s after the ramp should have ended. After the last step: lockout, target idle. Abort: knob press, app, iron in the stand, menu opened.
- Display: READY LIFT, START IN 3s, WAIT, S1/2 50s RAMP|HOLD (seconds left in the whole profile), LOCKOUT 5s.
- Validation: steps 100-350 C, rate 0.1 C/s from 1 to the maximum rate, hold up to 600 s; highest step at least idle + 10; ready limit between idle + 10 and highest step - 10.

**Protocol 9:** 0x50/0x51 carry 16 values (a 13 value write from a protocol 8 app keeps values 13-16; a 9 value write keeps 10-16); 0x53 adds step (byte 22), lift start state (byte 23: 0 none, 1 delay, 2 waiting until ready) and u16 delay ms left (24); while running, the u16 ms left is the whole profile. 0x56 read steps -> [0, count, 6 x (u16 C, u16 0.1 C/s, u16 0.1 s)], 0x57 write steps [key, count, 6 x 3 u16] -> [status 0/1/2/3].

**Peak raised to 250 C (2026-11-20, user):** profile logs (test_logs/profile_*) showed the solder starting to melt at a tip target of about 210 C (signal pad) / 222 C (ground plane) and flowing at 220 / 234 C: tip to joint about 30 / 45 C. With a 240 C peak a ground plane was fully molten for only about 8 s. Default step 2 is now 250 C (2 C/s, hold 5 s), about 68 s in all (simulated: 70 s from the start). Stations that already saved steps keep theirs (change step 2 on the Ramp page). No joint completion detection: no melt signature in the heater power (ROADMAP item 23).

**Stored profiles (2026-11-20, user: "store profiles in the station via the app"; protocol 10):** the app-run "Soldering profiles" page was removed (replaced by ramp mode), but named profiles can be kept in the station. 10 slots in the external EEPROM (24LC64, 8 KB; tip presets end at 1920) at 2048 + slot x 64: mark 0xE5, name 10 characters (printable ASCII, 0 padded), number of steps, 6 x (u16 C, u16 0.1 C/s, u16 0.1 s); the mark is written last. Byte 457 = slot the active steps were loaded from (0xFF = none; cleared when the steps are changed to something else). Writing a slot checks only fixed limits (1-6 steps, 100-350 C, 0.1-100 C/s, hold up to 600 s); loading checks against the station's values (maximum rate, idle, ready limit) like 0x57. Commands: 0x58 read [slot] -> [slot, status 0 ok/1 empty/2 bad, name 10, count, 18 x u16]; 0x59 write [key, slot, name 10, count, 18 x u16] -> [status]; 0x5A delete [key, slot]; 0x5B load [slot] -> [status 0/1/2/3]; 0x56 reply byte 38 = active slot. App: Ramp page list "Profiles stored in the station" with Load / Save here (name dialog, saves the steps shown in the editor) / Delete.

**Station menu R.PROFILE and backup (2026-11-20, user):** menu item R.PROFILE (ParDef 34, right after RAMP MODE) opens a screen with the stored profiles (only the filled slots, starting at the active one): header "PROFILE 3  (1/2) ACT", the name large, the steps ("180 30s>250 8s>260 3s"); turn = next/previous, short press = load (beep, back to the normal screen), hold 1 s = back to the menu. No stored profiles: error beep. A profile that does not fit the station's values shows NOT VALID: CHECK APP; while a profile runs: PROFILE RUNNING. Works in and out of ramp mode. Settings backup file (app): new lines ramp= (16 values), steps=count;18 values;active slot, profiles=10 and profileN=name in hex;count;18 values. Restore writes them after the settings and PID values (values/steps in either order, stored profiles not in the file are deleted, the active slot is loaded) and only when the station has protocol 9/10; older files without these lines leave ramp mode as it is.

**Contact start removed (2026-11-20, user):** after the lift start worked, the experimental pad contact start (2.4, 2.6) was removed from the firmware and the app: detectors, wipe check, capture buffer (4 KB RAM) and commands 0x54/0x55. Start is 0 or 1 (an older 2 or 3 in the EEPROM becomes 1). Parameters 6, 7, 9-11 stay in the 16 value layout but are not used; 0x53 bytes 10-21 are zero. The PC simulations of the detector were removed; `software/front/sim/ramp_profsim.c` stays.

**App:** Ramp page: start, start delay, maximum rate, step table (6 rows, temperature / rate / hold), status with step and time left, "Starting in x s" and "Waiting" for the lift start. The single ramp fields are hidden with protocol 9.

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
