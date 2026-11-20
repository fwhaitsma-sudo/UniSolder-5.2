# Ramp mode PC simulation

Stubs around `../US_Firmware.X/ramp_core.h` to test the ramp profile logic on a PC (gcc, not part of the firmware build).

    gcc -std=gnu89 -o ramp_profsim ramp_profsim.c -lm
    ./ramp_profsim <scenario>    # step profile with the lift start: 1 lift at 5 s, 2 back in the stand at 30 s (abort) and lift again, 3 hot tip at the lift (WAIT)

The thermal model is invented. (The contact detection simulations were removed with the contact start on 2026-11-20.)

    gcc -std=gnu89 -o ramp_slotsim ramp_slotsim.c -lm
    ./ramp_slotsim               # stored profiles: write, read, load (active slot), change, delete

    gcc -std=gnu89 -o ramp_selsim ramp_selsim.c -lm
    ./ramp_selsim                # the R.PROFILE menu screen: list, lines, load
