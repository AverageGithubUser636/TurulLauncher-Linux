# TurulLauncher 4.2.0

## Új
- Smart Repair: Minecraft fájlok, Fabric Loader és alap instance mappák ellenőrzése/javítása.
- Instance Snapshot: gyors ZIP visszaállítási pontok.
- Support Bundle: legutóbbi launcher logok, crash reportok, instance/settings állapot ZIP-be csomagolva.
- Network Health: DNS, turulnetwork.hu és Minecraft szerver elérhetőség ellenőrzése.
- Új gyorsgombok a Játék oldalon.

## Javítva
- A szerver „Csatlakozás” gomb most valóban továbbadja a címet a Minecraftnak (`--server` / `--port`), így működik a Quick Play/autojoin.

## Megjegyzés
- A Modrinth kötelező dependency-k telepítése már korábban rekurzívan működött, ezért azt nem duplikáltuk.
- A forráscsomag buildeléséhez .NET 10 SDK + Windows App SDK szükséges.
