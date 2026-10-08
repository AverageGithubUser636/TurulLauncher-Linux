#!/usr/bin/env bash
# TurulLauncher rendszer-szintű telepítése a kibontott mappából.
# Használat (a kibontott TurulLauncher-Linux-*/ mappában):
#   sudo ./install.sh            # /opt + /usr/local/bin + menübejegyzés
#   ./install.sh --user          # ~/.local alá, sudo nélkül
#   ./install.sh --uninstall      # rendszer-szintű eltávolítás
#   ./install.sh --user --uninstall  # felhasználói eltávolítás
set -euo pipefail

MODE="system"
ACTION="install"
for arg in "$@"; do
  case "$arg" in
    --user) MODE="user";;
    --uninstall) ACTION="uninstall";;
    -h|--help) sed -n '2,8p' "$0"; exit 0;;
    *) echo "Ismeretlen kapcsoló: $arg"; exit 2;;
  esac
done

SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP="TurulLauncher"

if [[ "$MODE" == "system" ]]; then
  PREFIX="/opt/turullauncher"
  BINLINK="/usr/local/bin/$APP"
  DESKDIR="/usr/share/applications"
  ICONDIR="/usr/share/icons/hicolor/256x256/apps"
  if [[ $EUID -ne 0 ]]; then echo "sudo kell (vagy: ./install.sh --user)"; exit 1; fi
else
  PREFIX="$HOME/.local/share/turullauncher"
  BINLINK="$HOME/.local/bin/$APP"
  DESKDIR="$HOME/.local/share/applications"
  ICONDIR="$HOME/.local/share/icons/hicolor/256x256/apps"
fi

if [[ "$ACTION" == "uninstall" ]]; then
  echo "Eltávolítás ($MODE mód)..."
  rm -rf "$PREFIX" "$BINLINK" \
    "$DESKDIR/turullauncher.desktop" \
    "$ICONDIR/turul-logo.png"
  command -v update-desktop-database >/dev/null && update-desktop-database "$DESKDIR" 2>/dev/null || true
  command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -f "$ICONDIR/../.." 2>/dev/null || true
  echo "Kész. (A ~/.local/share/TurulMC adatok megmaradtak.)"
  exit 0
fi

mkdir -p "$PREFIX" "$DESKDIR" "$ICONDIR" "$(dirname "$BINLINK")"
cp -r "$SRC/." "$PREFIX/"
chmod +x "$PREFIX/$APP"
ln -sf "$PREFIX/$APP" "$BINLINK"
cp "$PREFIX/turul-logo.png" "$ICONDIR/turul-logo.png"
sed -e "s|^Exec=.*|Exec=$BINLINK|" -e "s|^Icon=.*|Icon=turul-logo|" \
  "$PREFIX/turullauncher.desktop" > "$DESKDIR/turullauncher.desktop"
command -v update-desktop-database >/dev/null && update-desktop-database "$DESKDIR" 2>/dev/null || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -f "$ICONDIR/../.." 2>/dev/null || true

echo "Kész: $APP ($MODE mód)"
echo "  indítás: Super-gomb → TurulLauncher  (vagy: $APP)"
echo "  adatok:  ~/.local/share/TurulMC"
