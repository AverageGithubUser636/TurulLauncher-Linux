# TurulLauncher for Linux 🐦

Native Linux Minecraft launcher (C# + Avalonia) — sibling of the Windows
TurulLauncher, not a port: independent versioning starting at `1.0.0`.

*Magyarul: [README.hu.md](README.hu.md)*

## Install (2 minutes)

**A) AppImage — recommended** (single file):
```bash
chmod +x TurulLauncher-Linux-*.AppImage
./TurulLauncher-Linux-*.AppImage
# Add to menu (Super key → TurulLauncher):
./install-appimage.sh TurulLauncher-Linux-*.AppImage
```

**B) Tarball — classic**:
```bash
tar xzf TurulLauncher-Linux-*.tar.gz
cd TurulLauncher-Linux-*/ && sudo ./install.sh   # or: ./install.sh --user
```

Requirements: 64-bit Linux — no .NET, no Java needed up front
(self-contained package; the launcher handles Java).
Menu entry works on every distro (GNOME, KDE, XFCE, …).

## Features

| Area | What it does |
|---|---|
| 🎮 Play | Instances, Fabric/vanilla launch, per-instance world/RAM/Java |
| 🧩 Mods | enable/disable, add from file, **Modrinth browser** (with dependencies!) |
| 🗺 Textures | resource pack list, `options.txt`-based toggling, Modrinth |
| 📦 Modpacks | Modrinth browser, `.mrpack` import/export into new instances |
| 🌐 Servers | saved servers, live ping, one-click join |
| ☕ Java | detected runtimes, Temurin install, per-instance recommendation |
| 🎨 Themes | 5 color schemes (gold/green/red/purple/sky) + Turul logos |
| ⚕ Doctor | 17 checks, support bundle |
| 🔄 Update | automatic check + one-click install (in portable mode) |

## Data (XDG)

```
~/.local/share/TurulMC/   instances, mods, settings, logs, java/
~/.config/...             NOT used (worlds are not configs)
```

## Updates

On startup the launcher checks
`turulnetwork.hu/launcher/linux/update/stable.json`.
In portable mode (tarball install, AppImage) it installs in one click
(with SHA-256 verification); with packaged installs it opens the download page.

## From source

```bash
dotnet run --project src/TurulMC.Launcher.Avalonia   # run
./publishall.sh --version 1.1.0 --github <name>      # build a release
./upload-github-repo.sh --version 1.1.0              # GitHub release
```

Tests: `dotnet test tests/TurulMC.Core.Tests` plus the two smoke projects
(165+ checks total, no network needed).

## Troubleshooting

- **Game won't start?** Doctor tab → Run (Java, folders, permissions, network).
- **Something looks off?** Themes + `~/.local/share/TurulMC/logs/startup-*.log`.
- **Game crashed?** The loading monitor stays open with the log tail.
- **Clean slate?** Delete `instances/` (your saves live per-instance!).

## Differences from the Windows version

- No WebView2: native Avalonia UI (faster, more stable).
- No external `Updater.exe`: the launcher updates itself (or notifies).
- No tray icon (on Linux `minimize` is the equivalent).
- XDG data directory instead of `%APPDATA%`.

## License

All rights reserved © Turul Network. The code is readable, but usage
requires a separate agreement (open an issue if you want an open license).
