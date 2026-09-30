# Two-iron adapter (JBC C245 + JBC C210)

Custom firmware feature: two instruments connected at the same time through an
adapter, with only the active one heated (at full power). Each iron keeps its
own set temperature. Single-iron builds are not affected: the feature only
switches on when the adapter's ID code is detected.

## Adapter wiring

Station connector side:

| Station pin | Connect to |
|---|---|
| Vout1+ | C245 blue (heater) |
| Vout1- | C245 red |
| SENSEA | C245 green (shell) |
| Vout2+ | C210 red **and** SENSEB |
| Vout2- | C210 blue |
| EARTH | C245 green and C210 green (shells) |
| ID | 150 ohm to Vout1-, 3.0k to Vout2- |

- The ID resistors (150 ohm ID-Vout1-, 3.0k ID-Vout2-) give ID code `0x1705`,
  which selects the two-iron profile.
- The C210 is wired like the second tip of the JBC tweezers (a proven pattern
  in this firmware).
- The C245 differs from the normal C245 wiring: its shell goes to **SENSEA**
  instead of SENSEB, and its red wire no longer goes to SENSEA. This frees
  SENSEB for the C210.

## Use

- **Double-press** the encoder button (two presses within 0.4 s) on the main
  screen to switch between iron 1 (C245) and iron 2 (C210). A short beep confirms;
  the header shows `[1] JBC C245` or `[2] JBC C210`.
- Turning the knob sets the temperature of the active iron. Each iron remembers
  its own temperature, also after power-off.
- With the adapter connected, a single short press opens the presets after a
  0.4 s pause (the station waits to see whether a second press follows).
- Sleep, stand-by and the presets apply to the active iron.
- Switching starts the other iron from cold; the "temperature reached" beep
  sounds again when it gets there.

## Before normal use: check the C245 reading

The C245 is read on a single input in this wiring. The firmware uses the same
gain and conversion as the standard C245 profile, but this is **untested**, so
the header shows `(UNCAL)`.

1. Select iron 1 and set a low temperature, e.g. 200 C.
2. Measure the tip with a tip thermometer (e.g. Hakko FG-100).
3. If the actual temperature keeps rising past the set value, or the reading
   is clearly wrong, switch off at once.
4. Compare at a few set points (e.g. 250, 300, 350 C). A consistent error can
   be corrected in the profile (`IronPairs` in `iron.c`, gain or `c1`).

Also verify the C210 once the same way.

The Windows UniSolder application can adjust gain and PID of the active iron
live over USB for testing; those changes are lost when switching iron or
power-cycling, so copy the final values into `iron.c`.

## Automatic switching by lifting an iron (5.2C boards)

On a UniSolder 5.2C (VERSION screen shows board `1`) the station can see which
iron you lift, using two stands on the single HOLDER input:

| Stand | Switch closed while the iron rests | Wire between |
|---|---|---|
| C245 stand (iron 1) | series resistor **3.3k** | SLEEP and GND of the HOLDER connector |
| C210 stand (iron 2) | series resistor **5.6k** | SLEEP and GND of the HOLDER connector |

Both stand circuits go in parallel on the same two HOLDER pins. With the
board's 3k pull-up the station reads about 418 (both resting), 536 (only C245
resting), 666 (only C210 resting) or 1023 (both lifted); 5% resistors are fine.
The raw value is shown on the INST.INFO screen, handy for checking the wiring.

**Use a separate switch in each stand** (microswitch, or reed switch with a
magnet), not the tip touching a metal stand. The C245 shell is now a sensor
input, so an electrical contact through the tip would upset its temperature
reading.

Behaviour:
- Lift one iron: it becomes the active one and heats. Put it back: it goes to
  HOLDER T / sleep as usual.
- Both lifted: the active iron stays active. Double-press still switches
  manually; it stays in effect until the next time an iron is lifted.
- Setting HOLDER to OFF disables automatic switching.
- With the two-iron adapter on a 5.2C board, tip-changer detection is not
  used (its signal levels are taken by the second stand).
- On a 5.2 board (board `0`) the holder input is on/off only, so switching is
  manual (double-press) and the holder works as before.
