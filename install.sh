#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ASSETS="$SCRIPT_DIR/YAPA.Avalonia/Assets"
OUT_DIR="$SCRIPT_DIR/out/linux-x64"
BINARY="$OUT_DIR/YAPA.Avalonia"
INSTALL_BIN="/usr/local/bin/yapa2"
ICON_BASE="$HOME/.local/share/icons/hicolor"
DESKTOP_DIR="$HOME/.local/share/applications"

# ── Find dotnet ───────────────────────────────────────────────────────────────
if command -v dotnet &>/dev/null; then
    DOTNET=dotnet
elif [[ -x "$HOME/.dotnet/dotnet" ]]; then
    DOTNET="$HOME/.dotnet/dotnet"
else
    echo "error: dotnet not found. Install it from https://dot.net and re-run." >&2
    exit 1
fi

# ── Build ─────────────────────────────────────────────────────────────────────
echo "Building YAPA 2 (self-contained single file)…"
"$DOTNET" publish "$SCRIPT_DIR/YAPA.Avalonia/YAPA.Avalonia.csproj" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -o "$OUT_DIR" \
    --nologo -v quiet

# ── Install binary ────────────────────────────────────────────────────────────
echo "Installing binary to $INSTALL_BIN…"
sudo install -m 755 "$BINARY" "$INSTALL_BIN"

# ── Install icons ─────────────────────────────────────────────────────────────
echo "Installing icons…"
for size in 16 32 48 128 256 512; do
    dir="$ICON_BASE/${size}x${size}/apps"
    mkdir -p "$dir"
    cp "$ASSETS/yapa2_${size}.png" "$dir/yapa2.png"
done
# SVG for scalable (vector-aware launchers)
mkdir -p "$ICON_BASE/scalable/apps"
cp "$ASSETS/yapa2.svg" "$ICON_BASE/scalable/apps/yapa2.svg"

gtk-update-icon-cache -f -t "$ICON_BASE" 2>/dev/null || true

# ── Install .desktop entry ────────────────────────────────────────────────────
echo "Installing .desktop entry…"
mkdir -p "$DESKTOP_DIR"
cp "$SCRIPT_DIR/yapa2.desktop" "$DESKTOP_DIR/yapa2.desktop"
update-desktop-database "$DESKTOP_DIR" 2>/dev/null || true

echo ""
echo "Done. Launch with:  yapa2"
echo "Or find 'YAPA 2' in your app launcher."
