# TurulLauncher Linux — architektúra

A Windows-verzió (`src/TurulMC.Launcher`, WinUI3 + WebView2) **érintetlen**.
A Linux-fork új projektként él mellette, a közös Core-ra épülve.

```
TurulMC.sln (lásd: csak a fork-projekt van bekötve + Core/Infra/tesztek)
├── src/TurulMC.Core/              # MEGOSZTOTT üzleti logika (58+ fájl)
│   ├── Mods/                      # ModManager, ModrinthClient/Installer, ModIconCache
│   ├── Modpacks/                  # ModpackService, ModpackInstaller, ModpackPackager
│   ├── Java/                      # JavaRuntimeService/Provisioner, AdoptiumReleaseClient
│   ├── Minecraft/                 # Installation/Launcher/Fabric + GameLoadingStatus
│   ├── Update/                    # UpdateService (Linux-manifestekkel)
│   ├── Text/                      # DisplayText (felületi szövegtisztítás)
│   ├── Platform/                  # LauncherPlatform (windows/linux/osx)
│   └── Storage/                   # LauncherPaths (XDG), InstanceStore, AtomicFile
├── src/TurulMC.Launcher.Avalonia/ # NATÍV Linux GUI (Avalonia 11.3.16, net10.0)
│   ├── Views/                     # 9 lap + 2 dialógus/ablak (VM-mel egy fájlban)
│   └── Services/                  # LauncherServices, ThemeService, GameWindowDetector
└── tests/                         # xunit (62) + Features smoke (89) + Recovery (21)
```

## Képernyők és felelősségek

| Lap | VM | Core-kapcsolat |
|---|---|---|
| Játék (`HomeView`) | `HomeViewModel` | `LaunchInstanceAsync`, `Instances` (+ duplikálás/backup) |
| Modok (`ModsView`) | `ModsViewModel` + `SearchRow` | `ModManager`, `Modrinth*`, `Icons` (+ frissítés/szűrő/bulk) |
| Textúrák (`ResourcePacksView`) | u.a. packekre | `ModManager`, `Modrinth*` |
| Shaderek (`ShadersView`) | `ShadersViewModel` | `ModManager` (shader-műveletek), `Modrinth*` |
| Modpackok (`ModpacksView`) | `ModpacksViewModel` | `PackInstaller`, `ModpackPackager` |
| Szerverek (`ServersView`) | `ServersViewModel` | `Servers`, `ServerStatus` |
| Java (`JavaView`) | `JavaViewModel` | `Java`, `JavaProvisioner` |
| Beállítások (`SettingsView`) | `SettingsViewModel` | `SettingsStorage`, `Auth`, `ThemeService` |
| Frissítés (`UpdateView`) | `UpdateViewModel` | `Updates` |
| Doctor (`DoctorView`) | `DoctorViewModel` | `LauncherDoctor` |
| Naplók (`LogsView`) | `LogsViewModel` | `LauncherPaths.LogsRoot`, instance `logs/` + `crash-reports/` |
| Splash / Betöltőfigyelő / Instance-szerkesztő | kód-behind + mini-VM | `GameLoadingStatus`, settings |

MVVM-lite: `PropertyChangedBase` (INotifyPropertyChanged), nincs DI-konténer —
`LauncherServices.Current` a kompozíciós gyökér. Nézetek közti üzenetek:
`InstancesChanged`, `NavigateRequested`, `UpdateCheckCompleted` események.

## Indítási folyamat (`LaunchInstanceAsync`)

```
profil (auto offline) → settings-szinkron (MC/loader/RAM/loaderVer!)
  → EnsureVersionDownloaded → [fabric: resolve→install] → BuildLaunchConfig
  → JavaPath felülírás/JVM-args/ablak → EnsureRuntime → LaunchAsync
  → GameLoading-ablak (ProcessOutput + MinecraftExited + ablak-figyelés)
```

Kritikus invariáns: **a feloldott `LoaderVersion` mindig visszakerül a
settings-be** (`ApplyInstanceToSettings`, regressziós teszttel őrizve) —
különben a Fabric JSON-keresés üres kulccsal fut és „nincs telepítve" hibát ad.

## Adatútvonalak (XDG Linuxon)

```
$XDG_DATA_HOME/TurulMC/  (~/.local/share/TurulMC)
├── instances.json / instances/<id>/   (világ, mods/, resourcepacks/, versions/…)
├── settings.json / profiles.json / servers.json
├── java/  (provisioned Temurin)       cache/icons/  logs/
└── .turul-meta/  (Modrinth projekt-követés, mappánként)
```

Windowson minden marad `%APPDATA%\TurulMC`-ban (`LauncherPaths` dönt).

## Dizájnrendszer

- Paletta: `App.axaml` (`TurulGoldBrush` stb. `DynamicResource`-ként) —
  5 téma (`ThemeService`) élőban cseréli az akcentust + logót
- Minden lista-sor: hover/selected arany stílus (Fluent-kék felülírva),
  pill-badge-ek, 9–14px sugarak, átmenetek
- Ikonok: emoji-készlet (🎮🧩🗺📦🌐☕⚙⚕🔄), Inter + rendszer-fallbackkal;
  Modrinth-ikonok `Image` + `IconPathToBitmapConverter` (500-as memóriacache)
- Headless screenshot-harness (`/tmp/opencode/uishot`, nem verziózott):
  minden lap PNG-ben ellenőrizve — lásd a commit-előtti gyakorlatot

## Tesztstratégia

- xunit: tiszta Core-logika (62)
- Features smoke: produkciós belépési pontok stub HTTP-vel, hálózat nélkül
  (89 — Modrinth/MRPack/updater/traversal/Linux-port/MC-szabályok)
- Recovery smoke: diagnosztika (21)
- Szabály: új hálózati/fájl-útvonal csak `RoutingHandler`-es teszttel mehet be;
  élő API-próbák (`mr-live`) kézzel, temp-könyvtárba, takarítással
