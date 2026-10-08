# TurulLauncher installer build / deploy

## 1. Inno Setup telepítése

Ajánlott: Inno Setup 7 x64.

```bat
winget install --id JRSoftware.InnoSetup.7 -e -s winget -i
```

## 2. Teljes kiadás készítése

```bat
publish-all.cmd
```

Ez létrehozza:

- `publish/GUI/` – a launcher futtatható fájljai
- `publish/TurulLauncher-4.3.4.zip` – az Auto Updater release csomagja
- `publish/Installer/TurulLauncher-Setup-4.3.4.exe` – verziózott installer
- `publish/Installer/TurulLauncher-Setup.exe` – stabil webes letöltési név

## 3. Webes elhelyezés

Ajánlott:

- `https://turulnetwork.hu/launcher/download/TurulLauncher-Setup.exe`
- `https://turulnetwork.hu/launcher/releases/TurulLauncher-4.3.4.zip`
- `https://turulnetwork.hu/launcher/update/stable.json`

A weboldal Letöltés gombja a Setup EXE-re mutasson. A release ZIP az Auto Updaternek való.

## Telepítési viselkedés

A Setup alapértelmezett helye:

`%LOCALAPPDATA%\Programs\TurulLauncher`

Admin jog nem szükséges. Start menü parancsikon automatikusan készül, az asztali ikon opcionális.

A launcher felhasználói adatai az `%APPDATA%\TurulMC` mappában vannak. Az installer/uninstaller ezt a mappát szándékosan nem törli, ezért frissítés vagy újratelepítés nem törli a profilokat, instance-okat vagy beállításokat.
