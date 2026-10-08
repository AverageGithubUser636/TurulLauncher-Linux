# Setup Guide

## Prerequisites

- **.NET 10 SDK** - [Download](https://dotnet.microsoft.com/download/dotnet/10.0)
- **Java 17+** - Required for Minecraft 1.20.1+ (JDK 21 recommended)
- **Windows 10/11 x64** (Linux support planned)

## Building

```bash
cd TurulLauncher
dotnet restore
dotnet build
```

### Running the Launcher

```bash
dotnet run --project src/TurulMC.Launcher
```

### Running Tests

```bash
dotnet test
```

## First-Time Setup

1. **Launch the application** - The dark-themed TurulMC window will appear.
2. **Create a Developer Profile** - Click "Create Profile" and enter your username (3-16 characters).
3. **Configure Settings** - Click "Settings" to configure:
   - Minecraft version (default: `26.1.2`)
   - Loader type and version (default: Fabric `0.19.3`)
   - Test server host/port (default: `localhost:25565`)
   - Modpack manifest URL
   - RAM allocation (default: 2048 MB)
   - Java path override (leave empty for auto-detect)

## Java Runtime

The launcher auto-detects Java runtimes from:
- `JAVA_HOME` environment variable
- Common installation paths (`Program Files\Java`, `Eclipse Adoptium`, etc.)
- System `PATH`

To override, set a custom path in Settings pointing to `javaw.exe`.

## Local Modpack Manifest

### Creating a Manifest

Create a `manifest.json` following this schema:

```json
{
  "id": "turulmc-dev",
  "version": "1.0.0-dev",
  "minecraftVersion": "26.1.2",
  "loader": "fabric",
  "loaderVersion": "0.19.3",
  "files": [
    {
      "path": "mods/example.jar",
      "url": "http://localhost:8080/mods/example.jar",
      "sha256": "optional-sha256-hash",
      "size": 1024
    }
  ]
}
```

### Serving Locally

Use any local web server. For Python:

```bash
cd /path/to/your/modpack/files
python -m http.server 8080
```

Then set the manifest URL in Settings to: `http://localhost:8080/manifest.json`
(Release-ben csak `https://` manifest engedélyezett; localhost/file csak DEBUG-ban.)

### Manifest CLI (4.4.6)

```bash
TurulLauncher.CLI.exe manifest-hash manifest.json
TurulLauncher.CLI.exe modpack.export <instanceDir> <out.mrpack>
TurulLauncher.CLI.exe modpack.import <in.mrpack> <instanceDir>
TurulLauncher.CLI.exe servers.list
TurulLauncher.CLI.exe servers.add "Teszt" play.example.hu 25565
TurulLauncher.CLI.exe server.connect "Teszt"
```
Export/import kötelező SHA-val, zip-slip guarddal. `serve`-re használj statikus szervert (`python -m http.server`).

Or use a file path directly: `C:\path\to\manifest.json`

### Instance Directory

All modpack files are synced to:
```
%APPDATA%\TurulMC\instances\dev-instance\
```

Contents:
- `versions/` - Minecraft client JARs and Fabric version JSONs
- `libraries/` - Minecraft and Fabric library JARs
- `assets/` - Minecraft assets (sounds, textures)
- `mods/` - Mod JARs from the manifest
- `config/` - Mod configuration files

## Local Test Server

### Connecting to Localhost

1. Start your test server on `localhost:25565`
2. The launcher's "Local Server Status" panel shows online/offline status
3. The PLAY button launches Minecraft with `--server localhost --port 25565`

### Modifying Test Server Settings

In Settings:
- **Test Server Host**: Default `localhost`
- **Test Server Port**: Default `25565`

## Configuration Files

| File | Location | Purpose |
|------|----------|---------|
| `profiles.json` | `%APPDATA%\TurulMC\` | Developer profiles with offline UUIDs |
| `settings.json` | `%APPDATA%\TurulMC\` | Launcher settings (version, Java path, RAM) |
| `launcher-YYYY-MM-DD.log` | `%APPDATA%\TurulMC\logs\` | Daily log files |

## Troubleshooting

### "No Java runtime found"
- Ensure Java 17+ is installed
- Set `JAVA_HOME` or configure the path in Settings
- Verify `javaw.exe` exists at the configured path

### "Modpack sync failed"
- Verify the manifest URL is accessible
- Check that the local web server is running
- Ensure SHA-256 hashes in the manifest match the files

### "Launch failed"
- Check `%APPDATA%\TurulMC\logs\` for detailed error output
- Verify Minecraft files are downloaded (PLAY button triggers download)
- Ensure sufficient RAM is allocated (minimum 2048 MB for modded)
