# Roadmap

How YAPA 2 for Linux keeps getting better: what's next, what's being
considered, and the rules for maintaining it.

## Next

- **Task labels.** An optional "what are you working on?" for each pomodoro,
  stored with the record (a nullable column, backward compatible) and broken
  down in Statistics.
- **Wayland-native polish.** The app runs through XWayland today. Track
  Avalonia's Wayland backend, and when it lands, revisit always-on-top
  (GNOME needs an extension or `xdg-activation`) and the panel-avoidance logic
  (no stage input region on Wayland).
- **Packaged releases.** A Flatpak (Flathub) and an AppImage built in CI, so
  installing no longer needs the .NET SDK. The CI workflow already publishes
  the binaries; packaging manifests are the missing piece.
- **History editing.** Delete a mistaken record from Statistics, e.g. to clean
  up rows recorded by an early build, where Stop was counted as a completed pomodoro.

## Considering

- Overtime display, e.g. "+02:13" since work ended, while waiting to start the break.
- A GNOME Shell top-bar indicator built on `yapa2 --status --json`.
- Profile switching from the tray. The engine already supports multiple profiles.
- Idle detection: offer to discard a pomodoro when there was no input for most of it.
- Export history to CSV.
- Translations (all strings are already in one place per view).

## Maintenance rules

1. **Every bug gets a test that reproduces it before the fix.**
   - Engine and statistics bugs: unit tests in `YAPA.Shared.Tests` with the fake clock.
   - Window and binding bugs: headless tests in `YAPA.Avalonia.Tests/Ui`.
   - Desktop integration: private-bus D-Bus tests, or opt-in live tests.
2. **Never break user data.** Settings keys and the `Pomodoros` table are
   compatible with every earlier version. Add, never rename. New columns are nullable.
3. **Degrade, don't crash.** Missing PulseAudio, D-Bus, X11 or tray support
   disables that feature, logs one line, and the timer keeps working.
4. **Manual check before a release.** Run an isolated instance
   (`YAPA_HOME=/tmp/yapa-dev`) on GNOME/X11 and check:
   - drag
   - right-click
   - the dock zones
   - sounds
   - a notification button
   - hide/show from the tray
   - quit and restore
5. **CI must stay green** (`.github/workflows/ci.yml`): build, all tests, publish.
