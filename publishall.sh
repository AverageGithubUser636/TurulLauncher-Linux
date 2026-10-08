#!/usr/bin/env bash
# TurulLauncher-Linux kiadás-készítés (publishall.sh)
#
# Amit csinál:
#   1. (opcionálisan) tesztek futtatása,
#   2. self-contained publikálás linux-x64-re (+ linux-arm64-re),
#   3. hordozható csomag összeállítása (bináris + TurulMC.portable jelölő +
#      .desktop + ikon + install.sh + README),
#   4. tar.gz + SHA-256 + frissítési manifest-minta generálása a dist/ mappába.
#
# Használat:
#   ./publishall.sh [--version X.Y.Z] [--rid linux-x64|linux-arm64|all]
#                   [--format tarball|appimage|all]
#                   [--skip-tests] [--github TULAJDONOS] [--no-publish-tests]
#
# A --version a csproj 4 verziómezőjét is átírja (1.0.0-tól indul a fork).
# Az AppImage csak x86_64-re készül (az ARM futtatókörnyezet keresztbe
# nem tölthető le stabilan); ARM-re a tarball a támogatott forma.
# Példa:  ./publishall.sh --version 1.1.0 --github Pisti2303
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CSPROJ="$REPO_ROOT/src/TurulMC.Launcher.Avalonia/TurulMC.Launcher.Avalonia.csproj"
PROJECT="$REPO_ROOT/src/TurulMC.Launcher.Avalonia/TurulMC.Launcher.Avalonia.csproj"
DIST="$REPO_ROOT/dist"

VERSION=""
RIDS="linux-x64 linux-arm64"
FORMATS="tarball appimage"
SKIP_TESTS=0
GITHUB_OWNER="TULAJDONOS"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) VERSION="$2"; shift 2;;
    --rid)
      if [[ "$2" == "all" ]]; then RIDS="linux-x64 linux-arm64"; else RIDS="$2"; fi
      shift 2;;
    --format)
      if [[ "$2" == "all" ]]; then FORMATS="tarball appimage"; else FORMATS="$2"; fi
      shift 2;;
    --skip-tests) SKIP_TESTS=1; shift;;
    --github) GITHUB_OWNER="$2"; shift 2;;
    -h|--help) sed -n '2,16p' "$0"; exit 0;;
    *) echo "Ismeretlen kapcsoló: $1 (lásd: --help)"; exit 2;;
  esac
done

# appimagetool helye: rendszerben, vagy saját gyorsítótárban (egyszer töltődik).
appimagetool_path() {
  if command -v appimagetool >/dev/null 2>&1; then command -v appimagetool; return; fi
  local cached="$HOME/.cache/turul-build/appimagetool"
  if [[ ! -x "$cached" ]]; then
    echo "==> appimagetool letöltése (egyszeri)" >&2
    mkdir -p "$(dirname "$cached")"
    curl -sSL -o "$cached" \
      "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage"
    chmod +x "$cached"
  fi
  echo "$cached"
}

# --- 1. verzió ---------------------------------------------------------------
if [[ -z "$VERSION" ]]; then
  VERSION="$(grep -m1 '<Version>' "$CSPROJ" | sed -E 's/.*<Version>([^<]+)<\/Version>.*/\1/')"
fi
if ! [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "HIBA: érvénytelen verzió: '$VERSION' (X.Y.Z kell)"; exit 1
fi
echo "==> Verzió: $VERSION"

if grep -q "<Version>$VERSION</Version>" "$CSPROJ"; then
  echo "    csproj egyezik."
else
  echo "==> csproj verziómezők átírása $VERSION-re"
  sed -i -E \
    -e "s|<Version>[^<]+</Version>|<Version>$VERSION</Version>|" \
    -e "s|<AssemblyVersion>[^<]+</AssemblyVersion>|<AssemblyVersion>$VERSION.0</AssemblyVersion>|" \
    -e "s|<FileVersion>[^<]+</FileVersion>|<FileVersion>$VERSION.0</FileVersion>|" \
    -e "s|<InformationalVersion>[^<]+</InformationalVersion>|<InformationalVersion>$VERSION-linux</InformationalVersion>|" \
    "$CSPROJ"
  echo "    (ezt commitold: a verzió a forrásban él)"
fi

# --- 2. tesztek ---------------------------------------------------------------
if [[ "$SKIP_TESTS" -eq 0 ]]; then
  echo "==> Tesztek"
  dotnet test "$REPO_ROOT/tests/TurulMC.Core.Tests/TurulMC.Core.Tests.csproj" --nologo -v q
  dotnet run --project "$REPO_ROOT/tests/TurulMC.Features.SmokeTests" --no-launch-profile
  dotnet run --project "$REPO_ROOT/tests/TurulMC.Recovery.SmokeTests" --no-launch-profile
else
  echo "==> Tesztek kihagyva (--skip-tests)"
fi

# --- 3. publikálás ------------------------------------------------------------
rm -rf "$DIST"
mkdir -p "$DIST"

for RID in $RIDS; do
  echo "==> Publish: $RID (self-contained, single-file, trimming NÉLKÜL)"
  OUT="$DIST/publish-$RID"
  # A trimminget szándékosan nem kapcsoljuk be: az Avalonia/XAML
  # reflectiont használ, a vágott build összeomlana.
  dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true \
    /p:PublishSingleFile=true \
    /p:IncludeNativeLibrariesForSelfExtract=true \
    /p:DebugType=none \
    /p:DebugSymbols=false \
    -o "$OUT"

  # Futtatható-e a bináris neve (single-file: kiterjesztés nélküli)?
  BIN="$OUT/TurulLauncher"
  if [[ ! -x "$BIN" ]]; then
    echo "HIBA: nem jött létre a bináris: $BIN"; exit 1
  fi

  # --- 4. hordozható csomag ---
  STAGE="$DIST/stage-$RID/TurulLauncher-Linux-$VERSION-$RID"
  mkdir -p "$STAGE"
  cp -r "$OUT/." "$STAGE/"
  # Debug-szimbólumok nem kellenek a kiadásba.
  rm -f "$STAGE"/*.pdb
  # Hordozható jelölő: CSAK ettől mer az updater önmagát frissíteni.
  touch "$STAGE/TurulMC.portable"
  cp "$REPO_ROOT/src/TurulMC.Launcher/Assets/Logos/turul-logo-yellow.png" "$STAGE/turul-logo.png"
  cp "$REPO_ROOT/scripts/turullauncher.desktop" "$STAGE/" 2>/dev/null || true
  cp "$REPO_ROOT/scripts/install.sh" "$STAGE/" 2>/dev/null || true
  cp "$REPO_ROOT/scripts/README-Linux.txt" "$STAGE/" 2>/dev/null || true

  TARBALL="$DIST/TurulLauncher-Linux-$VERSION-$RID.tar.gz"
  if [[ "$FORMATS" == *"tarball"* ]]; then
    tar -czf "$TARBALL" -C "$DIST/stage-$RID" "TurulLauncher-Linux-$VERSION-$RID"
    (cd "$DIST" && sha256sum "$(basename "$TARBALL")" > "$(basename "$TARBALL").sha256")
    echo "    kész: $TARBALL ($(du -h "$TARBALL" | cut -f1))"
  fi

  # --- 4b. AppImage (csak x86_64; a dotnet-publish kimenetből) ---
  if [[ "$FORMATS" == *"appimage"* && "$RID" == "linux-x64" ]]; then
    echo "==> AppImage összeállítás"
    APPDIR="$DIST/TurulLauncher.AppDir"
    rm -rf "$APPDIR"
    mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/share/icons/hicolor/256x256/apps" "$APPDIR/usr/share/metainfo"
    cp "$OUT/TurulLauncher" "$APPDIR/usr/bin/"
    chmod +x "$APPDIR/usr/bin/TurulLauncher"
    # FIGYELEM: TurulMC.portable NEM kerül bele — az AppImage squashfs
    # írásvédett, az önfrissítés szétrombolná. AppImage-ből futva az updater
    # a kézi letöltést kínálja fel (lásd UpdateService).
    cp "$REPO_ROOT/src/TurulMC.Launcher/Assets/Logos/turul-logo-yellow.png" "$APPDIR/turul-logo.png"
    cp "$APPDIR/turul-logo.png" "$APPDIR/.DirIcon"
    cp "$APPDIR/turul-logo.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/turul-logo.png"
    cp "$REPO_ROOT/scripts/turullauncher.desktop" "$APPDIR/TurulLauncher.desktop"
    cat > "$APPDIR/AppRun" <<'APPRUN'
#!/bin/sh
# A single-file .NET kicsomagolását írható helyre irányítjuk (a squashfs
# írásvédett, a /tmp pedig lehet kicsi/tele) — ugyanaz az elv, mint a
# Windows-updater DOTNET_BUNDLE_EXTRACT_BASE_DIR trükkje.
HERE="$(dirname "$(readlink -f "$0")")"
export DOTNET_BUNDLE_EXTRACT_BASE_DIR="${XDG_CACHE_HOME:-$HOME/.cache}/TurulMC/bundle"
exec "$HERE/usr/bin/TurulLauncher" "$@"
APPRUN
    chmod +x "$APPDIR/AppRun"
    cat > "$APPDIR/usr/share/metainfo/turullauncher.appdata.xml" <<APPDATA
<?xml version="1.0" encoding="UTF-8"?>
<component type="desktop-application">
  <id>turullauncher</id>
  <name>TurulLauncher</name>
  <summary>Minecraft launcher Linuxra</summary>
  <description><p>TurulLauncher for Linux — natív Minecraft launcher (magyarul).</p></description>
  <launchable type="desktop-id">TurulLauncher.desktop</launchable>
  <provides><binary>TurulLauncher</binary></provides>
</component>
APPDATA

    APPIMAGE="$DIST/TurulLauncher-Linux-$VERSION-x86_64.AppImage"
    export VERSION
    "$(appimagetool_path)" "$APPDIR" "$APPIMAGE"
    chmod +x "$APPIMAGE"
    (cd "$DIST" && sha256sum "$(basename "$APPIMAGE")" > "$(basename "$APPIMAGE").sha256")
    echo "    kész: $APPIMAGE ($(du -h "$APPIMAGE" | cut -f1))"
  fi

  # --- 5. frissítési manifest-minta a weboldalra ---
  # Elsődleges az AppImage (egyetlen fájlos telepítés); ha nem készült,
  # a tarball az alapértelmezett.
  MANIFEST_FILE=""
  MANIFEST_SHA=""
  if [[ "$FORMATS" == *"appimage"* && "$RID" == "linux-x64" && -f "$DIST/TurulLauncher-Linux-$VERSION-x86_64.AppImage.sha256" ]]; then
    MANIFEST_FILE="TurulLauncher-Linux-$VERSION-x86_64.AppImage"
    MANIFEST_SHA="$(cut -d' ' -f1 "$DIST/$MANIFEST_FILE.sha256")"
  elif [[ "$FORMATS" == *"tarball"* ]]; then
    MANIFEST_FILE="TurulLauncher-Linux-$VERSION-$RID.tar.gz"
    MANIFEST_SHA="$(cut -d' ' -f1 "$TARBALL.sha256")"
  fi
  if [[ -n "$MANIFEST_FILE" ]]; then
  cat > "$DIST/stable-$RID.json" <<EOF
{
  "version": "$VERSION",
  "required": false,
  "url": "https://github.com/$GITHUB_OWNER/TurulLauncher-Linux/releases/download/v$VERSION/$MANIFEST_FILE",
  "sha256": "$MANIFEST_SHA",
  "publishedAt": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "changelog": [
    "TurulLauncher Linux $VERSION"
  ]
}
EOF
  fi
done

echo
echo "==> KÉSZ. dist/ tartalma:"
# Kanonikus manifest: AppImage előnyben (az updater egyetlen URL-t tölt).
if [[ -f "$DIST/stable-linux-x64.json" ]]; then
  cp "$DIST/stable-linux-x64.json" "$DIST/stable.json"
  echo "    (dist/stable.json — ezt tedd ki a weboldalra:"
  echo "     launcher/linux/update/stable.json)"
fi
ls -la "$DIST" | grep -v "^total\|^d"
echo
if [[ "$GITHUB_OWNER" == "TULAJDONOS" ]]; then
  echo "FIGYELEM: a manifest-URL-ekben TULAJDONOS áll — add meg: --github <neved>"
fi
echo "Következő lépés: ./upload-github-repo.sh --version $VERSION"
