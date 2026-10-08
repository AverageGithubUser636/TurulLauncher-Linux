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
#                   [--skip-tests] [--github TULAJDONOS] [--no-publish-tests]
#
# A --version a csproj 4 verziómezőjét is átírja (1.0.0-tól indul a fork).
# Példa:  ./publishall.sh --version 1.1.0 --github Pisti2303
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CSPROJ="$REPO_ROOT/src/TurulMC.Launcher.Avalonia/TurulMC.Launcher.Avalonia.csproj"
PROJECT="$REPO_ROOT/src/TurulMC.Launcher.Avalonia/TurulMC.Launcher.Avalonia.csproj"
DIST="$REPO_ROOT/dist"

VERSION=""
RIDS="linux-x64 linux-arm64"
SKIP_TESTS=0
GITHUB_OWNER="TULAJDONOS"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) VERSION="$2"; shift 2;;
    --rid)
      if [[ "$2" == "all" ]]; then RIDS="linux-x64 linux-arm64"; else RIDS="$2"; fi
      shift 2;;
    --skip-tests) SKIP_TESTS=1; shift;;
    --github) GITHUB_OWNER="$2"; shift 2;;
    -h|--help) sed -n '2,16p' "$0"; exit 0;;
    *) echo "Ismeretlen kapcsoló: $1 (lásd: --help)"; exit 2;;
  esac
done

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
  tar -czf "$TARBALL" -C "$DIST/stage-$RID" "TurulLauncher-Linux-$VERSION-$RID"
  (cd "$DIST" && sha256sum "$(basename "$TARBALL")" > "$(basename "$TARBALL").sha256")
  echo "    kész: $TARBALL ($(du -h "$TARBALL" | cut -f1))"

  # --- 5. frissítési manifest-minta a weboldalra ---
  SHA="$(cut -d' ' -f1 "$TARBALL.sha256")"
  cat > "$DIST/stable-$RID.json" <<EOF
{
  "version": "$VERSION",
  "required": false,
  "url": "https://github.com/$GITHUB_OWNER/TurulLauncher-Linux/releases/download/v$VERSION/TurulLauncher-Linux-$VERSION-$RID.tar.gz",
  "sha256": "$SHA",
  "publishedAt": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "changelog": [
    "TurulLauncher Linux $VERSION"
  ]
}
EOF
done

echo
echo "==> KÉSZ. dist/ tartalma:"
# Kanonikus manifest = x64 (az updater egyetlen URL-t tölt; ARM kézi letöltés).
if [[ -f "$DIST/stable-linux-x64.json" ]]; then
  cp "$DIST/stable-linux-x64.json" "$DIST/stable.json"
  echo "    (dist/stable.json = x64 — ezt tedd ki a weboldalra:"
  echo "     launcher/linux/update/stable.json)"
fi
ls -la "$DIST" | grep -v "^total\|^d"
echo
if [[ "$GITHUB_OWNER" == "TULAJDONOS" ]]; then
  echo "FIGYELEM: a manifest-URL-ekben TULAJDONOS áll — add meg: --github <neved>"
fi
echo "Következő lépés: ./upload-github-repo.sh --version $VERSION"
