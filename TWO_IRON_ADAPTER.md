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

## Not done yet: automatic switching by lifting an iron

Planned, depends on the board:

- **UniSolder 5.2** (VERSION screen shows board `0`): the holder input is
  on/off only, so it cannot tell which of two stands was used.
- **UniSolder 5.2C** (board `1`): the holder input is analogue. Two stand
  contacts with different resistors can be told apart; the resistor values
  still need to be worked out from the schematic.
