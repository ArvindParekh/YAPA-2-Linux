#!/usr/bin/env bash
# Remove YAPA 2. Settings (~/.config/YAPA2) and history (~/.local/share/YAPA2)
# are kept unless --purge is given.
set -euo pipefail

PURGE=0
SYSTEM=0
for arg in "$@"; do
    case "$arg" in
        --purge) PURGE=1 ;;
        --system) SYSTEM=1 ;;
        *) echo "usage: $0 [--system] [--purge]" >&2; exit 2 ;;
    esac
done

command -v yapa2 >/dev/null 2>&1 && yapa2 --quit >/dev/null 2>&1 || true

if [[ $SYSTEM == 1 ]]; then
    sudo rm -rf /opt/yapa2 /usr/local/bin/yapa2 /usr/local/share/applications/yapa2.desktop
    sudo find /usr/local/share/icons/hicolor -name 'yapa2.*' -delete
else
    SHARE="${XDG_DATA_HOME:-$HOME/.local/share}"
    rm -rf "$(realpath -m "$SHARE/../lib/yapa2")" "$HOME/.local/bin/yapa2" "$SHARE/applications/yapa2.desktop"
    find "$SHARE/icons/hicolor" -name 'yapa2.*' -delete 2>/dev/null || true
fi
rm -f "${XDG_CONFIG_HOME:-$HOME/.config}/autostart/yapa2.desktop"

if [[ $PURGE == 1 ]]; then
    rm -rf "${XDG_CONFIG_HOME:-$HOME/.config}/YAPA2" "${XDG_DATA_HOME:-$HOME/.local/share}/YAPA2" "${XDG_CACHE_HOME:-$HOME/.cache}/YAPA2"
    echo "Removed YAPA 2 with its settings and history."
else
    echo "Removed YAPA 2. Settings and history were kept."
fi
