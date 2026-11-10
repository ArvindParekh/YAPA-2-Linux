#!/usr/bin/env bash
# Build YAPA 2 from source and install it.
#
#   ./install.sh            install for the current user (no sudo): ~/.local
#   ./install.sh --system   install for everyone under /opt/yapa2 (uses sudo)
#
# Re-running upgrades in place. Settings and history are never touched.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$REPO/YAPA.Avalonia/YAPA.Avalonia.csproj"
OUT="$REPO/out/linux-x64"

MODE=user
for arg in "$@"; do
    case "$arg" in
        --system) MODE=system ;;
        -h|--help) sed -n '2,8p' "$0"; exit 0 ;;
        *) echo "unknown option: $arg" >&2; exit 2 ;;
    esac
done

if [[ $MODE == system ]]; then
    PREFIX_LIB=/opt/yapa2
    PREFIX_BIN=/usr/local/bin
    PREFIX_SHARE=/usr/local/share
    SUDO=sudo
else
    PREFIX_LIB="${XDG_DATA_HOME:-$HOME/.local/share}/../lib/yapa2"
    PREFIX_LIB="$(realpath -m "$PREFIX_LIB")"
    PREFIX_BIN="$HOME/.local/bin"
    PREFIX_SHARE="${XDG_DATA_HOME:-$HOME/.local/share}"
    SUDO=
fi

# ── dotnet ───────────────────────────────────────────────────────────────────
DOTNET=dotnet
if ! command -v dotnet >/dev/null 2>&1; then
    if [[ -x "$HOME/.dotnet/dotnet" ]]; then DOTNET="$HOME/.dotnet/dotnet"
    else echo "error: the .NET 8 SDK is required (https://dot.net)" >&2; exit 1; fi
fi

# ── A running instance must quit first: its files are about to be replaced ──
# Ask it over IPC first (works however it was launched); fall back to the
# process list for builds that predate the IPC channel.
# Exact process names only: matching command lines would also hit any shell
# whose command happens to mention yapa2 (including the one running this).
running() {
    pgrep -u "$USER" -x 'yapa2|YAPA\.Avalonia' >/dev/null 2>&1
}
WAS_RUNNING=0
RESUME=""
if [[ -x "$PREFIX_BIN/yapa2" ]] && STATUS="$("$PREFIX_BIN/yapa2" --status --json 2>/dev/null)"; then
    WAS_RUNNING=1
    # A pomodoro or break in progress comes back running, not paused.
    case "$STATUS" in *'"phase":"Work"'*|*'"phase":"Break"'*) RESUME="--start" ;; esac
    echo "==> Quitting the running YAPA 2 (its timer is saved and restored after the upgrade)"
    "$PREFIX_BIN/yapa2" --quit >/dev/null 2>&1 || true
    for _ in $(seq 1 25); do "$PREFIX_BIN/yapa2" --status >/dev/null 2>&1 || break; sleep 0.2; done
    for _ in $(seq 1 25); do running || break; sleep 0.2; done
fi
if running; then
    echo "YAPA 2 is running. Quit it (tray icon → Quit) and run this again." >&2
    echo "Your timer state is saved on quit and restored on the next launch." >&2
    exit 1
fi

# ── Build ────────────────────────────────────────────────────────────────────
echo "==> Building (Release, linux-x64, self-contained)…"
rm -rf "$OUT"
"$DOTNET" publish "$PROJECT" -c Release -r linux-x64 --self-contained true \
    -p:DebugType=none -p:DebugSymbols=false -nologo -v:quiet -o "$OUT"

# ── Install ──────────────────────────────────────────────────────────────────
echo "==> Installing to $PREFIX_LIB"
$SUDO rm -rf "$PREFIX_LIB.new"
$SUDO mkdir -p "$PREFIX_LIB.new"
$SUDO cp -r "$OUT"/. "$PREFIX_LIB.new/"
$SUDO rm -rf "$PREFIX_LIB"
$SUDO mv "$PREFIX_LIB.new" "$PREFIX_LIB"

$SUDO mkdir -p "$PREFIX_BIN"
$SUDO ln -sf "$PREFIX_LIB/YAPA.Avalonia" "$PREFIX_BIN/yapa2"

for size in 16 32 48 128 256 512; do
    $SUDO install -Dm644 "$REPO/YAPA.Avalonia/Assets/yapa2_${size}.png" \
        "$PREFIX_SHARE/icons/hicolor/${size}x${size}/apps/yapa2.png"
done
$SUDO install -Dm644 "$REPO/YAPA.Avalonia/Assets/yapa2.svg" "$PREFIX_SHARE/icons/hicolor/scalable/apps/yapa2.svg"

sed "s|@EXEC@|$PREFIX_BIN/yapa2|g" "$REPO/packaging/yapa2.desktop.in" > "$OUT/yapa2.desktop"
$SUDO install -Dm644 "$OUT/yapa2.desktop" "$PREFIX_SHARE/applications/yapa2.desktop"

gtk-update-icon-cache -q -t -f "$PREFIX_SHARE/icons/hicolor" 2>/dev/null || true
update-desktop-database -q "$PREFIX_SHARE/applications" 2>/dev/null || true

# ── Earlier builds installed elsewhere; point at them so they get removed ────
if [[ $MODE == user && -e /usr/local/lib/yapa2 ]]; then
    echo
    echo "note: an older YAPA 2 build is still in /usr/local (root-owned). Remove it with:"
    echo "      sudo rm -rf /usr/local/lib/yapa2 /usr/local/bin/yapa2"
fi
case ":$PATH:" in *":$PREFIX_BIN:"*) ;; *) echo "note: $PREFIX_BIN is not on your PATH; the launcher entry works regardless." ;; esac

if [[ $WAS_RUNNING == 1 ]]; then
    echo "==> Restarting YAPA 2${RESUME:+ and resuming your timer}"
    setsid "$PREFIX_BIN/yapa2" $RESUME >/dev/null 2>&1 < /dev/null &
fi

echo
echo "Installed YAPA 2. Start it from the app grid, or run: yapa2   (yapa2 --help for commands)"
