#!/usr/bin/env bash
# Try the redesign next to your installed YAPA, without touching it.
#
#   ./preview.sh            build (Release) and open the preview
#   ./preview.sh --reseed   start over from a fresh copy of your real settings and history
#   ./preview.sh --stop     quit the preview
#   ./preview.sh -- ARGS    pass arguments to it (--day, --stats, --settings, --ask, --toggle)
#
# The preview is its own YAPA: its own timer, settings and history, under .preview/ in this
# folder (YAPA_HOME). The first run copies your settings and history in, so Your day and
# Insights show your real days; after that the two go their own ways. Pomodoros you run in the
# preview stay in the preview. Your installed app, its files and its timer are only ever read.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$HERE/YAPA.Avalonia/YAPA.Avalonia.csproj"
PREVIEW="$HERE/.preview"
APP="$PREVIEW/app"
HOME_DIR="$PREVIEW/home"

REAL_CONFIG="${XDG_CONFIG_HOME:-$HOME/.config}/YAPA2"
REAL_DATA="${XDG_DATA_HOME:-$HOME/.local/share}/YAPA2"

RESEED=0
STOP=0
PASS=()
while [[ $# -gt 0 ]]; do
    case "$1" in
        --reseed) RESEED=1 ;;
        --stop) STOP=1 ;;
        --) shift; PASS=("$@"); break ;;
        -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
        *) echo "unknown option: $1 (see --help)" >&2; exit 2 ;;
    esac
    shift
done

# The preview's own processes: the binary under .preview/app, started with this YAPA_HOME.
preview_pids() {
    local pid
    for pid in $(pgrep -f "^$APP/YAPA.Avalonia" 2>/dev/null || true); do
        if tr '\0' '\n' < "/proc/$pid/environ" 2>/dev/null | grep -qx "YAPA_HOME=$HOME_DIR"; then echo "$pid"; fi
    done
}

stop_preview() {
    local pids
    pids=$(preview_pids)
    [[ -z $pids ]] && return 0
    # SIGTERM lets it save its timer state, as quitting from its menu does.
    kill $pids 2>/dev/null || true
    for _ in $(seq 1 25); do [[ -z $(preview_pids) ]] && return 0; sleep 0.2; done
    echo "The preview didn't quit; it's still running as: $pids" >&2
    return 1
}

if [[ $STOP == 1 ]]; then stop_preview; echo "Preview stopped."; exit 0; fi

# ── dotnet ───────────────────────────────────────────────────────────────────
DOTNET=dotnet
if ! command -v dotnet >/dev/null 2>&1; then
    if [[ -x "$HOME/.dotnet/dotnet" ]]; then DOTNET="$HOME/.dotnet/dotnet"
    else echo "Needs the .NET 8 SDK (https://dot.net)." >&2; exit 1; fi
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# ── Build ────────────────────────────────────────────────────────────────────
echo "==> Building the redesign (Release)…"
stop_preview
rm -rf "$APP"
"$DOTNET" publish "$PROJECT" -c Release -r linux-x64 --self-contained true \
    -p:DebugType=none -p:DebugSymbols=false -p:UseSharedCompilation=false -nologo -v:quiet -o "$APP"
"$DOTNET" build-server shutdown >/dev/null 2>&1 || true

# ── Seed ─────────────────────────────────────────────────────────────────────
# Copies, never moves or links: nothing the preview does can reach the real files.
if [[ $RESEED == 1 ]]; then rm -rf "$HOME_DIR"; fi
if [[ ! -d $HOME_DIR ]]; then
    echo "==> Copying your settings and history into the preview"
    mkdir -p "$HOME_DIR/config" "$HOME_DIR/data/assistant"
    for f in settings.json localSettings.json; do
        [[ -f $REAL_CONFIG/$f ]] && cp "$REAL_CONFIG/$f" "$HOME_DIR/config/"
    done
    if [[ -f $REAL_DATA/Yapa.db ]]; then
        # The database is in WAL mode: recent commits may still be in Yapa.db-wal. Copy both
        # and fold them together in the copy. Never open the real one, even read-only: SQLite
        # creates -shm/-wal files beside a WAL database for any reader.
        staging=$(mktemp -d)
        cp "$REAL_DATA/Yapa.db" "$staging/"
        [[ -f $REAL_DATA/Yapa.db-wal ]] && cp "$REAL_DATA/Yapa.db-wal" "$staging/"
        if command -v sqlite3 >/dev/null 2>&1; then
            sqlite3 "$staging/Yapa.db" ".backup '$HOME_DIR/data/Yapa.db'"
        else
            cp "$staging"/Yapa.db* "$HOME_DIR/data/"
        fi
        rm -rf "$staging"
    fi
    # Ask YAPA's notes and past conversations come along; its sign-in doesn't (the preview
    # uses Claude Code's own, or asks). Scheduled actions stay behind, or both apps would run
    # them; so does the running timer, so the preview starts idle rather than mid-pomodoro.
    for f in memory.json conversations.jsonl; do
        [[ -f $REAL_DATA/assistant/$f ]] && cp "$REAL_DATA/assistant/$f" "$HOME_DIR/data/assistant/"
    done
    [[ -d $REAL_DATA/Sounds ]] && cp -r "$REAL_DATA/Sounds" "$HOME_DIR/data/"
fi

# ── Run ──────────────────────────────────────────────────────────────────────
if [[ -n $(preview_pids) ]]; then
    # Already open: hand it the arguments, as a second launch of YAPA does.
    YAPA_HOME="$HOME_DIR" "$APP/YAPA.Avalonia" "${PASS[@]}" >/dev/null 2>&1 || true
else
    echo "==> Opening the preview"
    YAPA_HOME="$HOME_DIR" setsid "$APP/YAPA.Avalonia" "${PASS[@]}" > "$PREVIEW/app.log" 2>&1 < /dev/null &
fi
echo "The preview is running next to your installed YAPA. Its timer is the new one;"
echo "double-click it or press / for the command bar, and Ctrl K inside the hub."
echo "Quit it from its ⋯ menu, or: ./preview.sh --stop"
