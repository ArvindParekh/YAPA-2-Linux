#!/usr/bin/env bash
# install.sh — build YAPA 2 from source and install it on this machine.
# Run from the repo root: ./install.sh
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$REPO/YAPA.Avalonia/YAPA.Avalonia.csproj"
PUBLISH_OUT="$REPO/out/linux-x64"

INSTALL_LIB="/usr/local/lib/yapa2"   # binary + Assets/ live here
INSTALL_BIN="/usr/local/bin/yapa2"   # thin wrapper script
DESKTOP="$HOME/.local/share/applications/yapa2.desktop"
ICON_BASE="$HOME/.local/share/icons/hicolor"

# ── Find dotnet ───────────────────────────────────────────────────────────────
DOTNET="dotnet"
if ! command -v dotnet &>/dev/null; then
    if [[ -x "$HOME/.dotnet/dotnet" ]]; then
        DOTNET="$HOME/.dotnet/dotnet"
    else
        echo "ERROR: dotnet not found. Install .NET 8 SDK first." >&2
        exit 1
    fi
fi

# ── Build ─────────────────────────────────────────────────────────────────────
echo "==> Building YAPA 2 (Release · linux-x64 · self-contained · single-file)..."
"$DOTNET" publish "$PROJECT" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:DebugType=none \
    -p:DebugSymbols=false \
    -o "$PUBLISH_OUT"

# ── Install binary + assets ───────────────────────────────────────────────────
echo "==> Installing to $INSTALL_LIB ..."
sudo mkdir -p "$INSTALL_LIB/Assets"
sudo install -m 755 "$PUBLISH_OUT/YAPA.Avalonia"      "$INSTALL_LIB/YAPA.Avalonia"
sudo install -m 644 "$PUBLISH_OUT/Assets/tick.wav"    "$INSTALL_LIB/Assets/tick.wav"
sudo install -m 644 "$PUBLISH_OUT/Assets/ding.wav"    "$INSTALL_LIB/Assets/ding.wav"

# ── Launcher wrapper ──────────────────────────────────────────────────────────
echo "==> Writing launcher at $INSTALL_BIN ..."
sudo tee "$INSTALL_BIN" > /dev/null <<'WRAPPER'
#!/usr/bin/env bash
exec /usr/local/lib/yapa2/YAPA.Avalonia "$@"
WRAPPER
sudo chmod +x "$INSTALL_BIN"

# ── Icon ──────────────────────────────────────────────────────────────────────
ICO="$REPO/YAPA.Avalonia/Assets/pomoTray.ico"
if command -v convert &>/dev/null && [[ -f "$ICO" ]]; then
    echo "==> Installing icons..."
    for size in 16 32 48 128 256; do
        dir="$ICON_BASE/${size}x${size}/apps"
        mkdir -p "$dir"
        # ImageMagick picks the best-fitting frame from the ICO for the target size
        convert "$ICO" -thumbnail "${size}x${size}" -alpha on \
            "$dir/yapa2.png" 2>/dev/null && true
    done
    gtk-update-icon-cache --force --quiet "$ICON_BASE" 2>/dev/null || true
fi

# ── Desktop entry ─────────────────────────────────────────────────────────────
echo "==> Writing desktop entry..."
mkdir -p "$(dirname "$DESKTOP")"
cat > "$DESKTOP" <<'DESKTOP_ENTRY'
[Desktop Entry]
Name=YAPA 2
GenericName=Pomodoro Timer
Comment=Yet Another Pomodoro App
Exec=/usr/local/bin/yapa2
Icon=yapa2
Type=Application
Categories=Utility;Office;
Keywords=pomodoro;timer;focus;productivity;
StartupNotify=false
DESKTOP_ENTRY

update-desktop-database "$(dirname "$DESKTOP")" 2>/dev/null || true

echo ""
echo "Done. Launch with:  yapa2"
