# TurulLauncher Linux — build, kiadás, frissítés

## Fejlesztés

```bash
dotnet run --project src/TurulMC.Launcher.Avalonia          # futtatás
dotnet build TurulMC.sln                                     # fordítás
dotnet test tests/TurulMC.Core.Tests                        # xunit (62)
dotnet run --project tests/TurulMC.Features.SmokeTests      # smoke (89)
dotnet run --project tests/TurulMC.Recovery.SmokeTests      # recovery (21)
```

Követelmény: .NET 10 SDK. NuGet-csomagok rögzítettek
(`Avalonia 11.3.16` — nem `12.x`, a stabil API-felület miatt).

## Verzióemelés

A verzió **egy helyen** él: `TurulMC.Launcher.Avalonia.csproj`
(`<Version>` + Assembly/File/Informational). Az `AppInfo.Version` onnan olvas.

```bash
# 1. Csproj átírása X.Y.Z-re (4 mező), commit „vX.Y.Z" üzenettel
# 2. CHANGELOG.md [Unreleased] → [X.Y.Z] + DISCORD_CHANGELOG_X.Y.Z.txt
# 3. Kiadás:
./publishall.sh --version 1.1.0 --github AverageGithubUser636
```

## `publishall.sh`

```
./publishall.sh [--version X.Y.Z] [--rid linux-x64|linux-arm64|all]
                [--format tarball|appimage|all]
                [--skip-tests] [--github TULAJDONOS]
```

1. Tesztek (kihagyható: `--skip-tests`)
2. `dotnet publish` — self-contained, single-file, **trimming NÉLKÜL**
   (az Avalonia reflectiont használ, a vágott build összeomlana)
3. `dist/` tartalma: `TurulLauncher-Linux-<ver>-<rid>.tar.gz` (+`.sha256`),
   `TurulLauncher-Linux-<ver>-x86_64.AppImage` (+`.sha256`, csak x64),
   `stable-<rid>.json` + kanonikus `stable.json` (= AppImage, ha készült)
4. Hiányzó `DISCORD_CHANGELOG_<ver>.txt` esetén vázat generál

Csomag-tartalom: bináris + `TurulMC.portable` jelölő **(tarballban!)** +
`.desktop` + ikon + `install.sh` + README. Az AppImage-be **nincs**
portable-jelölő (squashfs írásvédett — az önfrissítés szétrombolná).

## `upload-github-repo.sh`

```bash
./upload-github-repo.sh --version 1.1.0 [--repo TULAJ/Neve] [--private]
                        [--notes FÁJL] [--token ...]
```

Repo-létrehozás (ha kell) → push `main`-re → `vX.Y.Z` release a
`dist/` assetekkel (tarballok + AppImage + sha256 + `stable.json` +
`install-appimage.sh`). Auth: `gh` (bejelentkezve) vagy `GITHUB_TOKEN`
(classic PAT, `repo` scope). Létező tagre nem ír rá (biztonsági stop).

## Weboldal-telepítés (kézzel, 2 fájl)

Az updater **csak** `turulnetwork.hu`-ról telepít (hoszt-allowlist!), ezért:

```
turulnetwork.hu/launcher/linux/releases/TurulLauncher-Linux-<ver>-x86_64.AppImage
turulnetwork.hu/launcher/linux/update/stable.json   ← dist/stable.json tartalma,
                                                      VALÓDI SHA-val!
```

Ellenőrzés kiadás után:
```bash
curl -s .../stable.json                          # version/url/sha olvasása
curl -sSL -o x.AppImage <url> && sha256sum x.AppImage   # egyeznie kell!
```

## Updater-viselkedés mátrix

| Környezet | `TurulMC.portable` | Mit kínál |
|---|---|---|
| Tarball-telepítés (`install.sh`) | ✅ van | Egykattintásos telepítés + újraindítás (SHA-256-tal) |
| AppImage | ❌ nincs | Letöltési oldal megnyitása (böngészőben) |
| `dotnet run` / csomagos | ❌ nincs | Letöltési oldal megnyitása |

Csatornák: `stable` (`stable.json`), `beta` (`beta.json`) — a beállítás
`UpdateChannel`-je választ. A beta-csatorna szerver-oldalon még 404-et ad.

## Discord-changelog

Minden verzióhoz `DISCORD_CHANGELOG_X.Y.Z.txt`: ```-be zárva, `[-]` kivettük /
`[*]` javítottuk-változott / `[+]` hozzáadtuk tagolással, végén
`turulnetwork.hu`. (A `publishall.sh` a vázat legenerálja.)
