# Changelog

## 2.1.0 (unreleased)

A ground-up revival of the Linux port.

### Fixed
- **No sound on start, pause, resume or stop.**
  - The cue and the background music shared one player, and music stopped it on every phase change.
  - Sound tools were looked up through a `PATH` that apps launched from GNOME don't have.
  - `ffplay` was given malformed arguments.
  - Pause and stop had no sound at all.
  - The stock tick peaked at −27 dBFS, so it was barely audible.
- **Clicks not registering near the Ubuntu dock.** On X11, GNOME Shell takes every click
  inside its input region, and that includes the dock's whole invisible container
  (90% of the screen width), not just the icons. The timer now keeps out of
  that region and moves itself if the dock appears under it.
- **"Today" and the dashboard counted the wrong days.** Local midnight was compared
  with UTC timestamps, and the week view was cumulative.
- Resuming a paused break turned it into a work interval.
- Changing "pomodoros before long break" required a restart.
- Sleep or suspend counted as work time.
- Settings *Cancel* didn't revert anything; settings now apply instantly, with per-page reset.
- A crash while saving settings could reset all of them; writes are now atomic.
- Digits jittered and clipped. They now use tabular figures, and the digit-spacing workaround is gone.

### Added
- A distinct, soft sound for every event, with a per-event choice of built-in sounds or your own files.
- Desktop notifications with *Start break* / *Skip break* / *Start pomodoro* buttons.
- A statistics window: streaks, daily goal, year heatmap, two-week chart.
- A command line with replies: `--toggle`, `--status --json`, `--skip`, `--quit`, …
- Options for show on all workspaces, hide from Alt+Tab, launch at login,
  start hidden, and pause while the screen is locked.
- Live theme switching, a context menu, keyboard shortcuts, and a tray menu that follows the timer state.
- An interrupted pomodoro is restored paused on the next launch, with no dialog.
- A per-user install with no sudo, an uninstaller, and a desktop entry with quick actions.
- A log file at `~/.cache/YAPA2/yapa2.log`.

### Changed
- Stopping a pomodoro abandons it and never records it as completed.
- The history database is accessed with plain SQLite instead of Entity Framework, which makes the install about 5 MB smaller.
- The Windows-only WPF code was removed from this branch; it lives on upstream.
