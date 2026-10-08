#!/usr/bin/env bash
# TurulLauncher AppImage menü-integráció (install-appimage.sh).
#
# Az AppImage önmagában hordoz mindent (.desktop + ikon is benne van),
# ezért ez a script magából az AppImage-ből dolgozik:
#   1. bemásolja ~/Applications/ alá (ez az AppImage-ajánlott hely),
#   2. kiszedi belőle a .desktop fájlt + ikont,
#   3. bejegyzi a menübe (Super-gomb → TurulLauncher).
#
# Használat:
#   ./install-appimage.sh TurulLauncher-Linux-1.0.0-x86_64.AppImage
#   ./install-appimage.sh --uninstall
#
# Minden disztrón működik (GNOME, KDE, XFCE, …) — csak a freedesktop
# alapszabványt használja, semmi disztró-specifikusat.
set -euo pipefail

APPS="$HOME/Applications"
DESKDIR="$HOME/.local/share/applications"
ICONDIR="$HOME/.local/share/icons/hicolor/256x256/apps"
DESKFILE="turullauncher.desktop"

if [[ "${1:-}" == "--uninstall" ]]; then
  echo "AppImage-integráció eltávolítása..."
  rm -f "$DESKDIR/$DESKFILE" "$ICONDIR/turul-logo.png" "$APPS"/TurulLauncher-Linux-*.AppImage
  command -v update-desktop-database >/dev/null && update-desktop-database "$DESKDIR" 2>/dev/null || true
  echo "Kész. (A ~/.local/share/TurulMC adatok megmaradtak.)"
  exit 0
fi

[[ $# -ge 1 ]] || { echo "Használat: $0 <TurulLauncher-*.AppImage> [--uninstall]"; exit 2; }
SRC="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
[[ -f "$SRC" ]] || { echo "HIBA: nincs ilyen fájl: $1"; exit 1; }

BASE="$(basename "$SRC")"
mkdir -p "$APPS" "$DESKDIR" "$ICONDIR"

echo "==> Másolás: $APPS/$BASE"
cp "$SRC" "$APPS/$BASE"
chmod +x "$APPS/$BASE"

echo "==> .desktop + ikon kiemelése az AppImage-ből"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
(cd "$WORK" && "$APPS/$BASE" --appimage-extract >/dev/null 2>&1)
[[ -f "$WORK/squashfs-root/TurulLauncher.desktop" ]] || { echo "HIBA: nincs .desktop az AppImage-ben"; exit 1; }

sed -e "s|^Exec=.*|Exec=\"$APPS/$BASE\" %U|" -e "s|^Icon=.*|Icon=turul-logo|" \
  "$WORK/squashfs-root/TurulLauncher.desktop" > "$DESKDIR/$DESKFILE"
cp "$WORK/squashfs-root/turul-logo.png" "$ICONDIR/turul-logo.png"

command -v desktop-file-validate >/dev/null && desktop-file-validate "$DESKDIR/$DESKFILE" && echo "    .desktop érvényes."
command -v update-desktop-database >/dev/null && update-desktop-database "$DESKDIR" 2>/dev/null || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -f "$ICONDIR/../.." 2>/dev/null || true

echo
echo "Kész! Nyomd meg a Super-gombot és írd be: TurulLauncher"
echo "  AppImage: $APPS/$BASE"
echo "  adatok:   ~/.local/share/TurulMC"
