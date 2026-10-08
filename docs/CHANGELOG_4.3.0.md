# TurulLauncher 4.3.0

## Player-facing
- Automatikus frissítésellenőrzés induláskor.
- Stable és Beta frissítési csatorna.
- Beállításokból kézi „Frissítések keresése”.
- Új verziónál changelog + „Frissítés és újraindítás”.
- SHA-256 csomagellenőrzés a telepítés előtt.

## Technikai
- Külön `TurulMC.Updater.exe`: megvárja a launcher bezárását, majd cseréli a fájlokat.
- Sikertelen fájlcserénél rollback az előző fájlokra.
- Update ZIP path traversal védelem.
- Csak HTTPS `turulnetwork.hu` / `*.turulnetwork.hu` release URL engedélyezett.
- Manifest endpointok: `/update/stable.json`, `/update/beta.json`.
