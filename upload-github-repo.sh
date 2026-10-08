#!/usr/bin/env bash
# TurulLauncher-Linux GitHub-repo + release feltöltés (upload-github-repo.sh)
#
# Amit csinál:
#   1. git init + első commit (ha kell),
#   2. GitHub repo létrehozása "TurulLauncher-Linux" néven (ha kell),
#   3. push (main ágra),
#   4. vVERZIÓ release készítése a dist/*.tar.gz + .sha256 + stable.json
#      fájlokkal.
#
# Hitelesítés (sorrendben az első működőt használja):
#   a) `gh` CLI, ha telepítve ÉS bejelentkezve (gh auth login),
#   b) GITHUB_TOKEN környezeti változó (classic PAT, `repo` scope) curl-lel.
#
# Használat:
#   ./upload-github-repo.sh --version 1.0.0 [--repo TULAJDONOS/TurulLauncher-Linux]
#                           [--private] [--notes RELEASE_NOTES.md] [--token ...]
#
# Előfeltétel: ./publishall.sh --version X.Y.Z már lefutott (dist/ kész).
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_NAME="TurulLauncher-Linux"

VERSION=""
REPO=""
PRIVATE=0
NOTES=""
TOKEN="${GITHUB_TOKEN:-}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) VERSION="$2"; shift 2;;
    --repo) REPO="$2"; shift 2;;
    --private) PRIVATE=1; shift;;
    --notes) NOTES="$2"; shift 2;;
    --token) TOKEN="$2"; shift 2;;
    -h|--help) sed -n '2,20p' "$0"; exit 0;;
    *) echo "Ismeretlen kapcsoló: $1 (lásd: --help)"; exit 2;;
  esac
done

[[ -n "$VERSION" ]] || { echo "HIBA: --version X.Y.Z kötelező"; exit 1; }
[[ -d "$REPO_ROOT/dist" ]] || { echo "HIBA: nincs dist/ — előbb: ./publishall.sh --version $VERSION"; exit 1; }
ls "$REPO_ROOT"/dist/*.tar.gz >/dev/null 2>&1 || { echo "HIBA: nincs tarball a dist/-ben"; exit 1; }

cd "$REPO_ROOT"

# --- 0. eszköz-ellenőrzés ------------------------------------------------------
HAVE_GH=0
if command -v gh >/dev/null 2>&1 && gh auth status >/dev/null 2>&1; then HAVE_GH=1; fi
if [[ "$HAVE_GH" -eq 0 && -z "$TOKEN" ]]; then
  echo "HIBA: se `gh` bejelentkezés, se GITHUB_TOKEN."
  echo "  Választás:"
  echo "    A) Telepítsd a gh-t és lépj be:  sudo pacman -S github-cli && gh auth login"
  echo "    B) Hozz létre classic PAT-et (repo scope): https://github.com/settings/tokens"
  echo "       majd:  GITHUB_TOKEN=ghp_... $0 --version $VERSION ..."
  exit 1
fi

# --- 1. git --------------------------------------------------------------------
if [[ ! -d .git ]]; then
  echo "==> git init"
  git init -b main
fi
if [[ -z "$(git config user.name || true)" ]]; then
  echo "FIGYELEM: nincs git user.name — beállítom helyinek: TurulLauncher"
  git config user.name "TurulLauncher"
  git config user.email "launcher@turulnetwork.hu"
fi
echo "==> commit"
git add -A
if git diff --cached --quiet; then
  echo "    nincs új változás."
else
  git commit -m "TurulLauncher Linux v$VERSION" -m "Linux fork kiadás."
fi
git branch -M main

# --- 2. tulaj + repo ------------------------------------------------------------
if [[ -z "$REPO" ]]; then
  if [[ "$HAVE_GH" -eq 1 ]]; then
    OWNER="$(gh api user --jq .login)"
  else
    read -rp "GitHub felhasználóneved (tulajdonos): " OWNER
  fi
  [[ -n "${OWNER:-}" ]] || { echo "HIBA: nincs tulajdonos"; exit 1; }
  REPO="$OWNER/$REPO_NAME"
else
  OWNER="${REPO%%/*}"
fi
echo "==> Repo: $REPO"

repo_exists() {
  if [[ "$HAVE_GH" -eq 1 ]]; then gh api "repos/$REPO" >/dev/null 2>&1
  else curl -sf -H "Authorization: Bearer $TOKEN" "https://api.github.com/repos/$REPO" >/dev/null 2>&1; fi
}

if repo_exists; then
  echo "    a repo már létezik — létrehozás kihagyva."
else
  echo "==> Repo létrehozása"
  if [[ "$HAVE_GH" -eq 1 ]]; then
    if [[ "$PRIVATE" -eq 1 ]]; then gh repo create "$REPO" --private --source=. --push
    else gh repo create "$REPO" --public --source=. --push; fi
  else
    PRIV_JSON="false"; [[ "$PRIVATE" -eq 1 ]] && PRIV_JSON="true"
    curl -sf -X POST -H "Authorization: Bearer $TOKEN" -H "Accept: application/vnd.github+json" \
      https://api.github.com/user/repos \
      -d "{\"name\":\"$REPO_NAME\",\"private\":$PRIV_JSON,\"description\":\"TurulLauncher for Linux (Avalonia, native)\"}" \
      >/dev/null
    echo "    létrehozva."
  fi
fi

# --- 3. push ----------------------------------------------------------------------
if git remote get-url origin >/dev/null 2>&1; then
  git remote set-url origin "https://github.com/$REPO.git"
else
  git remote add origin "https://github.com/$REPO.git"
fi
echo "==> push (main)"
if [[ "$HAVE_GH" -eq 1 ]]; then
  git push -u origin main
else
  git -c http.extraHeader="Authorization: Bearer $TOKEN" push -u origin main
fi

# --- 4. release ----------------------------------------------------------------------
TAG="v$VERSION"
echo "==> Release: $TAG"

release_notes() {
  if [[ -n "$NOTES" && -f "$NOTES" ]]; then cat "$NOTES"; return; fi
  {
    echo "TurulLauncher for Linux $VERSION"
    echo
    echo "Telepítés:"
    echo '```'
    echo "tar xzf TurulLauncher-Linux-$VERSION-linux-x64.tar.gz"
    echo "cd TurulLauncher-Linux-$VERSION-linux-x64 && sudo ./install.sh"
    echo '```'
    echo
    echo "Fájlok:"
    (cd dist && sha256sum ./*.tar.gz)
  }
}

ASSETS=(dist/*.tar.gz dist/*.sha256)
[[ -f dist/stable.json ]] && ASSETS+=(dist/stable.json)

if [[ "$HAVE_GH" -eq 1 ]]; then
  if gh release view "$TAG" --repo "$REPO" >/dev/null 2>&1; then
    echo "HIBA: a(z) $TAG release már létezik. Töröld a weben, vagy emelj verziót."
    exit 1
  fi
  release_notes > /tmp/turul-release-notes.txt
  # shellcheck disable=SC2086
  gh release create "$TAG" --repo "$REPO" --title "TurulLauncher Linux $VERSION" \
    --notes-file /tmp/turul-release-notes.txt "${ASSETS[@]}"
  rm -f /tmp/turul-release-notes.txt
else
  if curl -sf -H "Authorization: Bearer $TOKEN" \
      "https://api.github.com/repos/$REPO/releases/tags/$TAG" >/dev/null 2>&1; then
    echo "HIBA: a(z) $TAG release már létezik. Töröld a weben, vagy emelj verziót."
    exit 1
  fi
  NOTES_JSON="$(release_notes | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read()))')"
  RELEASE_JSON="$(curl -sf -X POST -H "Authorization: Bearer $TOKEN" -H "Accept: application/vnd.github+json" \
    "https://api.github.com/repos/$REPO/releases" \
    -d "{\"tag_name\":\"$TAG\",\"name\":\"TurulLauncher Linux $VERSION\",\"body\":$NOTES_JSON,\"draft\":false,\"prerelease\":false}")"
  UPLOAD_URL="$(echo "$RELEASE_JSON" | python3 -c 'import json,sys; print(json.load(sys.stdin)["upload_url"].split("{")[0])')"
  for asset in "${ASSETS[@]}"; do
    name="$(basename "$asset")"
    echo "    feltöltés: $name"
    curl -sf -X POST -H "Authorization: Bearer $TOKEN" \
      -H "Content-Type: application/octet-stream" \
      --data-binary "@$asset" "$UPLOAD_URL?name=$name" >/dev/null
  done
fi

echo
echo "==> KÉSZ: https://github.com/$REPO/releases/tag/$TAG"
echo "Weboldalra (launcher/linux/update/stable.json): a dist/stable.json tartalmát"
echo "töltsd fel — az updater onnan fogja látni a $VERSION-t."
