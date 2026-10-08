# Architecture

## Overview

TurulMC Launcher is a .NET 10 / WinUI 3 desktop application (WindowsAppSDK, unpackaged) designed for local modpack testing, development, and offline server debugging.

## Project Structure

```
TurulMC.sln
├── src/
│   ├── TurulMC.Core/           # Domain models, interfaces, business logic
│   │   ├── Authentication/     # IAuthenticationService
│   │   ├── Minecraft/          # IMinecraftInstallationService, IMinecraftLauncherService, IFabricLoaderService
│   │   ├── Modpacks/           # IModpackService
│   │   ├── Java/               # IJavaRuntimeService
│   │   ├── Networking/         # IServerStatusService
│   │   ├── Storage/            # ISettingsStorage
│   │   ├── Http/               # LauncherHttpClient
│   │   ├── Security/           # Sha256Service
│   │   ├── Logging/            # LauncherLogger
│   │   └── Models/             # All data models
│   ├── TurulMC.Infrastructure/ # Infrastructure implementations
│   │   ├── Authentication/     # LocalAuthenticationService (profile persistence)
│   │   ├── FileSystem/         # SettingsStorage (JSON persistence)
│   │   ├── Http/               # HttpClient wrapper
│   │   ├── Security/           # SHA-256 verification
│   │   └── Logging/            # File-based logging
│   └── TurulMC.Launcher/       # WinUI 3 application (WindowsAppSDK, WebView2 UI in ui/)
│       ├── Views/              # AXAML views
│       ├── ViewModels/         # MVVM ViewModels (CommunityToolkit.Mvvm)
│       ├── Controls/           # Custom controls
│       └── Resources/          # Styles and themes
├── tests/
│   └── TurulMC.Core.Tests/     # Unit tests (xUnit)
├── server/
│   └── manifest/               # Sample modpack manifest
└── docs/
```

## Layer Architecture

### Core Layer (TurulMC.Core)
- Contains all business logic and domain models
- Defines service interfaces (no implementation dependencies)
- Utility services (HTTP, SHA-256, Logging) live here to avoid circular references
- Zero external framework dependencies beyond base .NET

### Infrastructure Layer (TurulMC.Infrastructure)
- Implements persistence services (profiles, settings)
- Authentication service that manages local profiles
- References Core for interfaces and models

### Presentation Layer (TurulMC.Launcher)
- WinUI 3 (WindowsAppSDK, unpackaged) + WebView2 (`ui/index.html`) bridge (`MainWindow.*.cs`)
- Single-instance (`SingleInstance.cs`: mutex + wake-event, 2. példány előtérbe hoz és kilép; CLI külön exe, nem blokkolt)
- References both Core and Infrastructure

## Key Design Decisions

1. **No DI Container**: services are instantiated directly for launcher-scale simplicity.

2. **Security (4.4.6)**: kötelező SHA (hiány/hiba = törlés + kivétel), `PathSecurity.ResolveInsideRoot` traversal-védelem, zip-slip guard, symlink-védelem törléseknél, token-maszkolás logban, DevTools csak DEBUG-ban, `LauncherSettingsValidator` (javaPath-RCE zárás, jvmArgs denylist), Modrinth `cdn.modrinth.com` pin + 512 MB cap + mélység 25, `GetManifestAsync` https-only (DEBUG-ban localhost/file ok).

2. **Offline UUID Generation**: Uses MD5 hash of `"OfflinePlayer:<username>"` with version (3) and variant (8) bits set, matching Minecraft's offline mode convention.

3. **Modpack Sync**: Manifest-based approach with SHA-256 verification. Supports both HTTP and local file URIs. Instance directory: `%APPDATA%/TurulMC/instances/dev-instance/`.

4. **Fabric Integration**: Fetches Fabric loader JARs directly from Maven (`maven.fabricmc.net`) and creates version JSON with `inheritsFrom` pointing to the base Minecraft version.

5. **File Downloads**: All downloads use atomic writes (`.tmp` + rename) to prevent corruption on interruption.

## Data Flow

```
User clicks PLAY
    → Build LaunchConfig from profile + settings
    → Ensure Minecraft version downloaded (client JAR, libraries, assets)
    → Install Fabric loader (if configured)
    → Sync modpack files (download/update/remove)
    → Build JVM arguments + classpath
    → Launch Minecraft process with stdout/stderr capture
```
