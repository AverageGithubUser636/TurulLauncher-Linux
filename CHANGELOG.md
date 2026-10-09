# Changelog — TurulLauncher for Linux

A Linux-fork változásnaplója. Formátum: [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
verziózás: szemantikus, `1.0.0`-tól induló **önálló** számozás (nem követi a Windowst).

## [Unreleased]

### Added

- ✨ **Shaderek-lap**: shaderpack lista átnevezéses ki/bekapcsolással
  (`.zip.disabled`, Iris-kompatibilis), fájl-hozzáadás, törlés,
  Modrinth shader-böngészés ikonokkal + egykattintásos telepítéssel
- 🔄 **Mod-frissítések**: frissítés-ellenőrzés minden Modrinth-metás modhoz
  (downgrade-védelemmel), soronkénti „↑ verzió" jelzés, kijelölt/összes
  frissítése; a tiltott állapot az új fájlra költözik
- 🔍 **Mod-kereső és szűrők**: szöveges keresés (név/fájlnév) + állapot
  (Mind/Bekapcsolt/Kikapcsolt/Hibás) a telepített listán
- ☑️ **Tömeges mod-műveletek**: Mind be / Mind ki, többszörös kijelölés
  törlése; a kapcsolás már az átnevezett fájlnevet követi
- 📋 **Naplók-lap**: játéknapló / crash report / launcher-napló tallózása
  (256 KB + 400 soros farok-korlát, autoscroll, mappa-megnyitás)
- ⧉ **Instance-duplikálás**: teljes másolat új azonosítóval (`logs` nélkül),
  a többi nézet értesítést kap (`InstancesChanged`)
- 💾 **Instance-backup**: ZIP-export (`logs`/`crash-reports` nélkül)
  fájlmentés-párbeszéddel

### Changed

- `InstanceStore` teszt-izolált gyökérrel (`HydrateModpackMetadata` is);
  `CopyInstanceAsync` + `ExportBackupAsync` új Core-műveletek
- Nav bővítve: Shaderek (Tartalom), Naplók (Rendszer végén)

## [1.0.0] — 2026-10-08 — első Linux-kiadás 🎉

Ez **major** kiadás: új termék, nem a Windows-verzió átirata. Natív
Avalonia-felület WebView2 nélkül, XDG-adatkönyvtárral, AppImage + tarball
formában, GitHub-release-csel.

### Added — új felület (9 lap)

- 🎮 **Játék**: hero-kezdőlap (aktív Instance + chipek + óriás JÁTÉK-gomb),
  instance lista, létrehozó/szerkesztő dialógus (név, RAM, verzió, loader,
  loader-verzió, Java-út, JVM-args, ablakméret, megjegyzés)
- 🧩 **Modok**: ki/bekapcsolás (`.jar.disabled`), fájl-hozzáadás, törlés,
  **Modrinth-böngészés** kereséssel + ikonokkal + egykattintásos telepítéssel
- 🗺 **Textúrák**: resource-pack lista `options.txt`-alapú kapcsolással,
  Modrinth-böngészéssel és ikonokkal
- 📦 **Modpackok**: Modrinth-böngészés, `.mrpack` import/export új Instance-ba
- 🌐 **Szerverek**: mentett szerverek, élő ping (MOTD/játékos/ms),
  egykattintásos csatlakozás `--server/--port`-tal
- ☕ **Java**: észlelt runtime-ok, egykattintásos Temurin-telepítés,
  törlés, ajánlás az aktív Instance-hoz
- 🎨 **Témák**: 5 színséma (arany/zöld/piros/lila/égszín) igazi Turul-logókkal,
  azonnali váltással, mentve
- ⚕ **Doctor**: 17 ellenőrzés + support-csomag (a Windows-logikával)
- 🔄 **Frissítés**: önálló oldal (hero, changelog-idővonal, telepítés),
  induláskori néma ellenőrzés + arany jelvény/banner
- Splash screen fázisokkal + játékbetöltő-figyelő élő naplóval
  (`GameLoadingStatus`), ablak-érzékeléssel (wmctrl/xdotool), crash-naplóval
- Menü-integráció: `install.sh` (tarball) + `install-appimage.sh`
  (Super-gomb → TurulLauncher, GNOME/KDE/XFCE)

### Added — Core-szolgáltatások (tesztelve)

- `ModrinthClient`: keresés, verziók, tömeges projekt-lekérés,
  szerver-oldali loader/MC-szűrés, release-preferencia
- `ModrinthInstaller`: rekurzív `required`-függőségfeloldás (kör-védelem,
  25-ös mélység), CDN-allowlist, 512 MB cap, SHA-512 ellenőrzés,
  `.turul-meta` követés
- `ModpackInstaller`: `.mrpack` telepítés (kliens-szűrés, hash, zip-slip
  védelem, `.turul-modpack.json`), export-manifest építés
- `ModIconCache`: projekt-ikonok `cache/icons`-ban (PNG/JPEG/WebP/GIF-fejléc
  ellenőrzéssel, 2 MB cap)
- `ModManager`: mod/pack fájlműveletek traversal-védelemmel
- `UpdateService`: stable/beta manifest, szemantikus összehasonlítás,
  hordozható önfrissítés SHA-256-tal (csak `TurulMC.portable` jelölőre)
- `DisplayText`: HTML-entitás/markdown/chat-komponens/`§`-kód mentesítés
- `LauncherPlatform`: egységes `windows`/`linux`/`osx` szabályillesztés
- `ThemeService`, `GameWindowDetector`, `InstanceStore` (közös Core-ban)

### Changed — Linux-helyesség (a Windows-kód átvételekor javítva)

- Adoptium-lekérés `os=linux`-szal (Windows JRE helyett), `.tar.gz`
  kicsomagolás + végrehajtható-bit állítás
- Könyvtár/natives-szabályok a futó platformhoz (`linux`/`osx`/`windows`)
- Java-felderítés Linux-útvonalakon (`/usr/lib/jvm`, SDKMAN, …),
  `java`-bináris elfogadása (`javaw.exe` helyett/mellett)
- `servers.json` + `java-cache.json` XDG-helyre (`~/.local/share/TurulMC`)
- `InstanceStore` opcionális gyökérrel (teszt-izoláció)
- Önálló `1.0.0` verzió + saját Linux-frissítési manifest
  (`launcher/linux/update/stable.json`)

### Fixed

- Fabric-indítás „nincs telepítve" hibája: a feloldott `LoaderVersion`
  nem került át a settings-be (regressziós teszttel őrizve)
- Ikon-takarító törölte a `.download` átmeneti fájlt a `Move` előtt
- Traversal-bejegyzés az egész modpack/updater-telepítést dobja (nem nevezi át)
- Fluent-kék maradványok: lista-kijelölés, checkbox, ComboBox-elemek → arany
- `x:Name` mezők NULL-ja (`InitializeComponent` a kézi `Load` helyett)
- Kliens-oldali verzió-ellenőrzés a szerver-szűrés mellé (Modrinth + modpack)

### Removed

- WebView2 + HTML-UI (natív Avalonia van helyette)
- Külső `Updater.exe` (önfrissítés van helyette)
- Tálca-ikon (Linuxon `minimize` a megfelelője)
- `%APPDATA%`-függés (XDG van helyette)
- 7 hívatlan metódus + üres stubok (halottkód-irtás)

### Security

- Repó-átvilágítás: nincs kulcs/token/jelszó/webhook/privát IP a kódban
- Letöltések: HTTPS-kényszer, hoszt-allowlistek, SHA-ellenőrzés, méretcap-ek
- Fájlműveletek: traversal-védelem mindenhol (`PathSecurity`)
