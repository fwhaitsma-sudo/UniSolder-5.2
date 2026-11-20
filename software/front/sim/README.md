# Ramp mode PC simulation

Stubs around `../US_Firmware.X/ramp_core.h` to test the contact detection on a PC (gcc, not part of the firmware build). The contact scenarios set start = 2 (contact) and a ready limit of 230 C.

    gcc -std=gnu89 -o ramp_sim ramp_sim.c -lm
    ./ramp_sim <scenario> [wipe ripple %] [random seed]
    # scenarios: 1 pad contact, 2 brass wool strokes (3 Hz), 3 nothing, 4 wipe then pad, 5 lift then pad within the blank time

    gcc -std=gnu89 -o ramp_logsim ramp_logsim.c -lm
    ./ramp_logsim ../test_logs/ramp_test_3_contact.csv     # replays a 5 Hz app log at 50 Hz (lightly smoothed) through the detector

The thermal model and the noise are invented; the numbers that count come from the station's capture buffer.

    gcc -std=gnu89 -o ramp_profsim ramp_profsim.c -lm
    ./ramp_profsim <scenario>    # step profile with the lift start: 1 lift at 5 s, 2 back in the stand at 30 s (abort) and lift again, 3 hot tip at the lift (WAIT)
