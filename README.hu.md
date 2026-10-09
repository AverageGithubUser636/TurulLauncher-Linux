# TurulLauncher for Linux 🐦

Natív Linux Minecraft-launcher (C# + Avalonia) — a Windowsos TurulLauncher testvére, nem portja: saját `1.0.0`-tól induló verziószámozással.

*In English: [README.md](README.md)*

## Telepítés (2 perc)

**A) AppImage — ajánlott** (egyetlen fájl):
```bash
chmod +x TurulLauncher-Linux-*.AppImage
./TurulLauncher-Linux-*.AppImage
# Menübe (Super-gomb → TurulLauncher):
./install-appimage.sh TurulLauncher-Linux-*.AppImage
```

**B) Tarball — klasszikus**:
```bash
tar xzf TurulLauncher-Linux-*.tar.gz
cd TurulLauncher-Linux-*/ && sudo ./install.sh   # vagy: ./install.sh --user
```

Követelmény: 64 bites Linux — sem .NET, sem Java nem kell előre
(self-contained csomag; a Java-t a launcher intézi).
Menübejegyzés minden disztrón működik (GNOME, KDE, XFCE, …).

## Funkciók

| Terület | Mit tud |
|---|---|
| 🎮 Játék | Instance-ok, Fabric/vanilla indítás, világ/RAM/Java instance-onként |
| 🧩 Modok | ki/bekapcsolás, fájl-hozzáadás, **Modrinth-böngészés** (függőségekkel!) |
| 🗺 Textúrák | resource pack lista, `options.txt`-alapú kapcsolás, Modrinth |
| 📦 Modpackok | Modrinth-böngészés, `.mrpack` import/export új Instance-ba |
| 🌐 Szerverek | mentett szerverek, élő ping, egykattintásos csatlakozás |
| ☕ Java | észlelt runtime-ok, Temurin-telepítés, ajánlás az Instance-hoz |
| 🎨 Témák | 5 színséma (arany/zöld/piros/lila/égszín) + Turul-logók |
| ⚕ Doctor | 17 ellenőrzés, support-csomag |
| 🔄 Frissítés | automatikus ellenőrzés + egykattintásos telepítés (hordozható módban) |

## Adatok (XDG)

```
~/.local/share/TurulMC/   instance-ok, modok, beállítások, logok, java/
~/.config/...             NEM használjuk (a világok nem configok)
```

## Frissítés

A launcher induláskor ellenőrzi a
`turulnetwork.hu/launcher/linux/update/stable.json`-t.
Hordozható módban (tarball-telepítés, AppImage) egy kattintással telepít
(SHA-256 ellenőrzéssel); csomagos telepítésnél a letöltési oldalt nyitja meg.

## Forrásból

```bash
dotnet run --project src/TurulMC.Launcher.Avalonia   # futtatás
./publishall.sh --version 1.1.0 --github <neved>     # kiadás-gyártás
./upload-github-repo.sh --version 1.1.0              # GitHub release
```

Tesztek: `dotnet test tests/TurulMC.Core.Tests` plusz a két smoke-projekt
(összesen 165+ ellenőrzés, hálózat nélkül).

## Dokumentáció

- [CHANGELOG.md](CHANGELOG.md) — verziótörténet (Keep a Changelog)
- [docs/linux-architecture.md](docs/linux-architecture.md) — fork-felépítés, indítási folyamat, XDG-elrendezés, dizájnrendszer, tesztstratégia
- [docs/linux-build-release.md](docs/linux-build-release.md) — build, `publishall.sh`, GitHub-release-ek, weboldal-manifestek, updater-mátrix

## Hibaelhárítás

- **Nem indul a játék?** Doctor lap → Futtatás (Java, mappa, jogok, hálózat).
- **Fekete/kék valami?** Téma + `~/.local/share/TurulMC/logs/startup-*.log`.
- **Összeomlott a játék?** A betöltőfigyelő nyitva marad a napló-farokkal.
- **Tiszta lap?** Töröld az `instances/`-t (a mentéseid instance-onként vannak!).

## Különbségek a Windows-verzióhoz képest

- Nincs WebView2: natív Avalonia-felület (gyorsabb, stabilabb).
- Nincs külső `Updater.exe`: a launcher önmagát frissíti (vagy jelez).
- Nincs tálca-ikon (Linuxon `minimize` a megfelelője).
- Adatkönyvtár XDG-helyen, nem `%APPDATA%`-ban.

## Licenc

Minden jog fenntartva © Turul Network. A kód olvasható, de felhasználási
engedélyt külön megállapodás ad (ha nyílt licencet szeretnél, nyiss egy issue-t).
