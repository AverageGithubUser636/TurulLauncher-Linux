#!/usr/bin/env bash
# TurulLauncher rendszer-szintű telepítése a kibontott mappából.
# Használat (a kibontott TurulLauncher-Linux-*/ mappában):
#   sudo ./install.sh            # /opt + /usr/local/bin + menübejegyzés
#   ./install.sh --user          # ~/.local alá, sudo nélkül
set -euo pipefail

MODE="system"
if [[ "${1:-}" == "--user" ]]; then MODE="user"; fi

SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP="TurulLauncher"

if [[ "$MODE" == "system" ]]; then
  PREFIX="/opt/turullauncher"
  BINLINK="/usr/local/bin/$APP"
  DESKDIR="/usr/share/applications"
  ICONDIR="/usr/share/pixmaps"
  if [[ $EUID -ne 0 ]]; then echo "sudo kell (vagy: ./install.sh --user)"; exit 1; fi
else
  PREFIX="$HOME/.local/share/turullauncher"
  BINLINK="$HOME/.local/bin/$APP"
  DESKDIR="$HOME/.local/share/applications"
  ICONDIR="$HOME/.local/share/pixmaps"
fi

mkdir -p "$PREFIX" "$DESKDIR" "$ICONDIR" "$(dirname "$BINLINK")"
cp -r "$SRC/." "$PREFIX/"
chmod +x "$PREFIX/$APP"
ln -sf "$PREFIX/$APP" "$BINLINK"
cp "$PREFIX/turul-logo.png" "$ICONDIR/turul-logo.png"
sed -e "s|^Exec=.*|Exec=$BINLINK|" -e "s|^Icon=.*|Icon=$ICONDIR/turul-logo.png|" \
  "$PREFIX/turullauncher.desktop" > "$DESKDIR/turullauncher.desktop"

echo "Kész: $APP ($MODE mód)"
echo "  indítás: $APP  (vagy a menüből: TurulLauncher)"
echo "  adatok:  ~/.local/share/TurulMC"
