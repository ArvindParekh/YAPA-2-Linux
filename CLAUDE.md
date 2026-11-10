# YAPA 2 for Linux — engineering notes

YAPA 2 is a minimal, always-on-top Pomodoro timer. This repo is the Avalonia
port of the original Windows/WPF app (upstream: YetAnotherPomodoroApp/YAPA-2),
developed Linux-first (GNOME on X11 is the reference desktop).

## Layout

| Path | What |
|---|---|
| `YAPA.Shared/` | Platform-free core: pomodoro engine, settings store, contracts, statistics. No Avalonia references. |
| `YAPA.Avalonia/` | The app: windows, view-models, settings UI, platform integration. |
| `YAPA.Avalonia/Platform/` | OS integration. `Platform/Linux/` holds X11, PulseAudio, D-Bus code. Everything there must degrade to a no-op off Linux or when the service is missing. |
| `YAPA.Avalonia/Audio/` | Sound cues + background music. |
| `YAPA.Shared.Tests/`, `YAPA.Avalonia.Tests/` | MSTest + NSubstitute. |
| `packaging/` | Desktop entry, install/uninstall scripts. |

## Build, test, run

The SDK lives in `~/.dotnet` (not on PATH by default):

```bash
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet build YAPA.sln
dotnet test YAPA.sln
```

Run an **isolated** dev instance. It must never touch the user's real config,
history, or running timer:

```bash
YAPA_HOME=/tmp/yapa-dev dotnet run --project YAPA.Avalonia
```

`YAPA_HOME` relocates config, data, cache and the single-instance lock
(see `Platform/AppPaths.cs`). Without it, a second launch just forwards its
arguments to the user's running instance.

Seed the dev instance silent before it plays anything; external players (ffplay, pw-play)
do not honour `YAPA_SILENT_AUDIO`. Keys inside a component are unprefixed, and a wrong
key is silently ignored (that made a radio audition audible once). Check the Sounds page
reads 0% before touching anything:

```bash
mkdir -p /tmp/yapa-dev/config && echo '{"PomodoroEngine":{"Volume":0.0},"Ambience":{"Volume":0.0,"TickVolume":0.0}}' > /tmp/yapa-dev/config/settings.json
```

Per-machine settings (music paths, window position) live in `localSettings.json`.

## Rules of the road

- Never kill, restart or send commands to the user's installed instance
  (`/usr/local/lib/yapa2` or `~/.local/lib/yapa2`). Never write to `~/.config/YAPA2`
  or `~/.local/share/YAPA2`.
- Don't play audible test sounds; verify audio paths with silent buffers. The test suite
  sets `YAPA_SILENT_AUDIO=1`, which turns every player into a silent one.
- Test the real app, not just units. A feature is done only after it has been used
  end to end in a running instance (isolated `YAPA_HOME`, nested GNOME Shell for UI
  clicks). A binding loop in the ambience picker shipped because only the pieces were tested.
- The machine has 15 GB and runs GNOME, Claude, Slack and browsers. A nested gnome-shell
  plus build servers got the real GNOME Shell OOM-killed once. Check `free -h` first,
  and run `dotnet build-server shutdown` after building.
- Keep `YAPA.Shared` free of UI/platform dependencies so the engine stays unit-testable.
- Time in the engine is measured on a monotonic clock, so time asleep never counts as work.
  Wall-clock time is only used for timestamps of history records.
- History timestamps are stored as UTC. Convert to local time only when grouping by day.
- Settings changes apply immediately (no Save/Cancel). Persistence is debounced and atomic.
- Comments explain *why*, not *what*. File-scoped namespaces, nullable enabled in new code.

## Desktop facts that drive design (GNOME / X11)

- On X11, GNOME Shell captures every click inside its stage input region, which
  includes the dock's whole container (e.g. 90% of the screen width), not just
  the visible icons. A window placed there loses clicks. `Platform/Linux/X11` watches
  that region and keeps the timer out of it.
- Mutter 46 never draws a compositor shadow on ARGB (transparent) windows, whatever
  their type; measured, and it matches mutter's `has_shadow()`. The timer is a plain
  NORMAL window. `_GTK_FRAME_EXTENTS` only matters for opaque visuals. UTILITY and DOCK
  window types were tried in earlier builds and broke focus, minimize, drag and right-click.
- Mutter deletes `_NET_WM_STATE` and `_NET_WM_DESKTOP` when a window is hidden.
  `WindowShellIntegration.Apply` re-sets them before every show (it is a property edit
  while the window is withdrawn) and after map (EWMH client messages).
- Subclassing an Avalonia control (other than Window) needs
  `StyleKeyOverride => typeof(BaseControl)`. Otherwise no control theme matches,
  and the control renders nothing. The actions menu shipped broken this way.
- Mutter ends Show Desktop when any new window maps, popups and tooltips included,
  except for DOCK windows and their transients. So the timer has no hover tooltips.
- Avalonia installs its own Xlib error handler. Anything chaining to the previous
  handler must hold it as a plain `Delegate`, because marshaling hands back
  Avalonia's delegate type.
