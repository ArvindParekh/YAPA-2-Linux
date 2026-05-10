#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT_DIR="$SCRIPT_DIR/out/linux-x64"

echo "Building YAPA 2 for Linux (self-contained, single file)…"

dotnet publish "$SCRIPT_DIR/YAPA.Avalonia/YAPA.Avalonia.csproj" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:IncludeAllContentForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -o "$OUT_DIR"

echo ""
echo "Published to: $OUT_DIR/YAPA.Avalonia"
echo ""
echo "To install system-wide:"
echo "  sudo cp '$OUT_DIR/YAPA.Avalonia' /usr/local/bin/yapa2"
echo "  sudo chmod +x /usr/local/bin/yapa2"
echo "  cp '$SCRIPT_DIR/yapa2.desktop' ~/.local/share/applications/"
echo "  update-desktop-database ~/.local/share/applications/"
