<p align="center"><img src="YAPA.Avalonia/Assets/yapa2_128.png" width="96" alt="YAPA 2"></p>

<h1 align="center">YAPA 2 for Linux</h1>

<p align="center">A minimal, always-on-top Pomodoro timer that stays out of your way.</p>

YAPA 2 puts a small transparent timer on your desktop: big digits, a few buttons
that appear when you need them, and a tray icon. It's a Linux port of the
Windows classic [YAPA-2](https://github.com/YetAnotherPomodoroApp/YAPA-2),
rebuilt on Avalonia and tuned for GNOME.

## Features

- **The classic look**: large digits, colour and size of your choice, a shadow for
  busy wallpapers, optional status and progress line. A *Motivational* theme adds a quote card.
- **Proper Pomodoro flow**: work, short breaks, a long break every N pomodoros,
  optional auto-start, pause and resume (breaks too), skip, stop.
- **Sound for every event**: start, pause, resume, stop, pomodoro complete and break over each get
  their own soft cue, or your own file. Optional background music per phase.
- **Rain while you work**: soft, steady or pouring rain and brown noise, generated live
  so they never audibly loop, plus an optional ticking clock. It fades in when a pomodoro
  starts and out when it ends. You can also loop your own recording. Switch it from the ⋯ menu,
  the tray, or `yapa2 --ambience`.
- **Notifications with buttons**: *Start break* / *Skip break* / *Start pomodoro*
  right from the notification, even while the timer is hidden.
- **Statistics**: today, this week, streaks, a daily goal, a year heatmap and a two-week chart.
- **An assistant that runs the app**: double-click the timer (or press <kbd>/</kbd>) and ask
  or tell it anything: "I walked for an hour, log it and start the next one", "give me 5 more
  minutes", "if I stop at 6, how many will I have done?", "when do I focus best?", "make the
  text gold". It reads and changes everything, live: the timer, history, statistics, settings
  and profiles. It can act later ("stop at 6", "remind me to stretch in 20 minutes"),
  remembers what you tell it to across conversations, and knows your breaks and abandoned
  pomodoros as well as the completed ones. Every request can be undone in one click. It runs on your own Claude sign-in
  through [Claude Code](https://claude.com/code), with no API key, and can reach nothing on
  your computer but YAPA.
- **Plays well with GNOME**:
  - Stays clear of the dock and top bar, where GNOME on X11 would swallow its clicks.
  - Shows on every workspace, stays on top, and stays put through Show Desktop (Super+D).
  - Hides from Alt+Tab unless you want it there.
  - Lives in the tray and can launch at login.
- **Honest time**: time asleep never counts as work, and an interrupted pomodoro is
  restored (paused) the next time you start YAPA.
- **Scriptable**:
  - `yapa2 --toggle` for a global shortcut.
  - `yapa2 --status --json` for status bars.

## Install

You need the [.NET 8 SDK](https://dot.net) to build.

```bash
git clone https://github.com/ArvindParekh/YAPA-2-Linux.git
cd YAPA-2-Linux
./install.sh            # per-user, into ~/.local (no sudo)
```

`./install.sh --system` installs for all users under `/opt/yapa2`. Running it
again upgrades in place. `./uninstall.sh` removes the app; add `--purge` to also
delete settings and history.

The tray icon on GNOME needs the AppIndicator extension. Ubuntu ships it enabled.

## Using it

| | |
|---|---|
| Drag anywhere | move the timer |
| Double-click, or <kbd>/</kbd> | ask YAPA |
| Right-click | settings |
| ⋯ button (or <kbd>Menu</kbd>) | every action, Statistics, Quit |
| <kbd>Space</kbd> | start / pause / resume |
| <kbd>Esc</kbd> | stop: abandon the pomodoro, or end the break |
| <kbd>S</kbd> | skip the break |
| <kbd>L</kbd> | take the long break now |
| <kbd>Ctrl</kbd>+<kbd>,</kbd> / <kbd>Ctrl</kbd>+<kbd>I</kbd> | settings / statistics |
| <kbd>Ctrl</kbd>+<kbd>H</kbd> / <kbd>Ctrl</kbd>+<kbd>Q</kbd> | hide to tray / quit |

**Long break whenever you need it.** Done two pomodoros and need the long one
now? Press <kbd>L</kbd>, use the ⋯ menu or tray, or the notification's *Long break*
button. It works from idle, after work, and during or after a short break (time
already rested counts). When it ends, the cycle starts over at pomodoro 1.

**Stop vs. finish.** Stopping a pomodoro abandons it: it isn't recorded, and the
same pomodoro starts over. Stopping during a break ends the break.

**Ask YAPA.** The assistant opens beside the timer and closes when you click away
or press <kbd>Esc</kbd>; reopening within a few minutes carries on the same
conversation. Under each reply it lists what it changed, with **Undo**. It needs
Claude Code installed and signed in; Settings → General picks the model (Sonnet by
default). It starts Claude Code only while you use it, with none of your Claude Code
tools, plugins or MCP servers: only YAPA's own.

### Global shortcut

GNOME Settings → Keyboard → View and Customise Shortcuts → Custom Shortcuts → add
`yapa2 --toggle`. One key starts and pauses the timer from anywhere.

### Command line

```text
yapa2 --start | --pause | --toggle | --stop | --skip | --long-break | --reset
yapa2 --ambience            # rain / ticking on or off
yapa2 --show | --hide | --toggle-window | --settings | --stats | --ask
yapa2 --status [--json]     # e.g. "Work 12:34"
yapa2 --quit
```

Commands go to the running timer. `--status` never launches the app when it
isn't running, so it's safe to poll from a status bar.

## Files

| What | Where |
|---|---|
| Settings | `~/.config/YAPA2/` |
| History (SQLite) | `~/.local/share/YAPA2/Yapa.db` |
| Log | `~/.cache/YAPA2/yapa2.log` |

## Development

```bash
dotnet build YAPA.sln
dotnet test YAPA.sln
YAPA_HOME=/tmp/yapa-dev dotnet run --project YAPA.Avalonia   # isolated profile
```

`YAPA_HOME` gives a fully separate instance: its own settings, history and
single-instance lock. It runs next to your real timer. See
[CLAUDE.md](CLAUDE.md) for architecture notes and [ROADMAP.md](ROADMAP.md)
for what's next.

## Credits

The original YAPA and YAPA-2 are by floatas and contributors, and the YAPA-2
website is [yapa2.app](https://yapa2.app). Pomodoro Technique® is a trademark
of Francesco Cirillo. MIT licensed: see [LICENSE.TXT](LICENSE.TXT).
