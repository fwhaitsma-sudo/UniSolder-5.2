# UniSolder fork: working notes

Work in progress is tracked in `software/Android/ROADMAP.md` (plan, decisions, what is done, what is next).
Read it first and continue from the first unchecked item: do not redo checked items.

- Fork: fwhaitsma-sudo/UniSolder-5.2, default branch `display-step` (firmware changes are listed in `CHANGELOG.md`).
- Firmware: `software/front/US_Firmware.X` (PIC32MX564F128H, XC32). Android app: `software/Android` (Kotlin, no dependencies). Windows app: `software/PC`.
- Builds run on GitHub Actions only (no local Android SDK or MPLAB): firmware workflow builds every pushed branch except `display-step`; the Android workflow builds `android-app` and `remote-control`. Builds of `display-step` are started by hand and publish a release.
- Compile errors show up as annotations on the run (REST `check-runs/<job>/annotations`).
- The user tests on real hardware and asks for the merge to `display-step`; do not merge unasked. Changelog lists implemented features only.
