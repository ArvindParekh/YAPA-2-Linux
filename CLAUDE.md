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

## Rules of the road

- Never kill, restart or send commands to the user's installed instance
  (`/usr/local/lib/yapa2` or `~/.local/lib/yapa2`). Never write to `~/.config/YAPA2`
  or `~/.local/share/YAPA2`.
- Don't play audible test sounds; verify audio paths with silent buffers.
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
- Mutter skips its compositor shadow for windows that set `_GTK_FRAME_EXTENTS`;
  that is how the transparent timer avoids a halo while staying a NORMAL window.
  UTILITY and DOCK window types were tried earlier and broke focus, minimize, drag and right-click.
