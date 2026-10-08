# TurulLauncher 4.1 state architecture

TurulLauncher now uses the same *kind* of separation common in mature Minecraft launchers, without copying implementation code.

## Global launcher state
Stored in `%APPDATA%/TurulMC/settings.json`:
- selected player profile ID
- theme
- performance mode
- first-run completion
- tutorial completion

Writes are atomic (`.tmp`) with a `.bak` recovery file.

## Player profiles
Stored in `%APPDATA%/TurulMC/profiles.json`.
- independent from Instances
- atomic writes
- backup recovery
- selected profile lives in global launcher settings

## Instances
Stored in `%APPDATA%/TurulMC/instances.json`, with game files under:
`%APPDATA%/TurulMC/instances/<instance-id>/`

Each Instance owns its Minecraft version, loader, loader version and RAM. Instance config writes are atomic and recoverable.

## Web UI
The WebView is a renderer/controller only. It does **not** own durable state. No `localStorage` is used for launcher-critical data.

## Source of truth
Backend files -> bridge -> one canonical UI state controller.
