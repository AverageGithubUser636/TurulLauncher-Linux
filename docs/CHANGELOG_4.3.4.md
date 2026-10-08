# TurulLauncher 4.3.4

## Added
- Modrinth texture/resource pack search and installation for the active Instance.
- Installed resource pack list and removal.
- Modrinth modpack search and installation into a dedicated new Instance.
- Local `.mrpack` import with native Windows file picker.
- Download progress for pack installation.

## Changed
- Imported Modrinth packs keep their requested Fabric Loader version when available.
- Custom Turul modpack sync removes only files previously managed by its manifest; user-added mods/config/resourcepacks are preserved.
- Update manifest generation uses the 4.3.4 changelog when the version changes.

## Current limitations
- Modpack runtime support is Fabric or vanilla. Forge, NeoForge and Quilt packs are rejected instead of being installed incorrectly.
