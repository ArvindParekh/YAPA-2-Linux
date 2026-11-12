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
- **Ask YAPA**, an assistant that runs the whole app. Double-click the timer, press <kbd>/</kbd>, or use
  the menu, tray or `--ask`, and ask or tell it anything. It sees and changes the timer, history,
  statistics, every setting and the profiles, live. It plans the day on the timer's own rules, answers
  history questions with SQL over a local-time copy of the history, and undoes any request in one click.
  It runs on the user's Claude sign-in through Claude Code, limited to YAPA's own tools.
- The assistant can also schedule actions and reminders for later (kept across restarts), move the timer,
  choose music and sound files, export history to CSV, reset a settings section, read the app log,
  remember notes across conversations and look back through past ones.
- Every word on the timer can be changed, a note line of your own can show live values ("{left} to go"),
  and the Motivational theme can show your own quote; from Settings or by asking YAPA.
- Looks: six built-in styles (Classic, Glass, Minimal, Bold, Neon, Paper) and your own, saved by name or
  designed by YAPA on request; new style options for the digits (font, weight), the progress line colours,
  and a backdrop plate.
- Reminders and actions the assistant scheduled announce themselves in a bubble on the timer, with
  a chime (a new "Reminder or notice" cue, the original YAPA ding); the desktop's own notification
  while the timer is hidden, or always if chosen in Settings → General.
- A timer event log (starts, pauses, stops, breaks) in `Yapa.db`, so breaks and abandoned pomodoros
  can be asked about, not only completed ones.
- A redesigned Settings window: dark grouped cards, a sidebar with icons, steppers, a live preview of the
  timer, a to-scale strip of one cycle, and an Assistant page showing what it remembers and has scheduled.
- Geist (bundled, OFL) as the interface font; the timer keeps Inter.
- Timer operations behind it: lengthen or shorten the interval under way, backdate one started late,
  and jump to any place in the cycle without recording anything.
- Ambience while you work: soft, steady or pouring rain and brown noise (synthesized live,
  never looping), a ticking clock, or your own recording on loop. It fades with the
  session, optionally continues through breaks, and has a quick switch in the menu, tray and CLI.
- Long break on demand: <kbd>L</kbd>, the ⋯ menu, the tray, `--long-break`, or the notification button.
  It starts the cycle over afterwards, and a running short break turns into the long one without losing the time already rested.
- The timer stays visible through Show Desktop; the tray restores a minimized timer in one click.
- Right-click opens settings again, as in the original; every other action is on a ⋯ button.
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
