# TurulLauncher Auto Update deploy

A launcher ezeket olvassa:

- `https://turulnetwork.hu/launcher/update/stable.json`
- `https://turulnetwork.hu/launcher/update/beta.json`

## Release lépések

1. Futtasd: `publish.cmd`
2. Töltsd fel a `publish/TurulLauncher-X.Y.Z.zip` fájlt ide:
   `https://turulnetwork.hu/launcher/releases/TurulLauncher-X.Y.Z.zip`
3. Generáld a manifestet:
   `powershell -ExecutionPolicy Bypass -File tools/Make-UpdateManifest.ps1 -Zip publish/TurulLauncher-X.Y.Z.zip -Version X.Y.Z -Channel stable`
4. Írd át a `changelog` tömböt a játékost érintő változásokra.
5. Töltsd fel `server/update/stable.json`-t `/update/stable.json` néven.
6. Beta release-nél ugyanez `-Channel beta` kapcsolóval.

`-Required` kapcsolóval kötelező frissítést adhatsz ki.

## Biztonság

- A launcher csak HTTPS `turulnetwork.hu` vagy `*.turulnetwork.hu` csomagot fogad el.
- A ZIP SHA-256 hashének pontosan egyeznie kell a manifesttel.
- A külön updater csak a launcher bezárása után cserél fájlokat.
- Hibánál a már lecserélt fájlokat visszaállítja.
- ZIP path traversal (`../`) blokkolva van.

## Updater UI preview

- Build nélkül: `preview-updater.cmd` megnyitja a `tools/updater-preview.html` fájlt.
- Publish után: `publish\Updater\TurulMC.Updater.exe --preview` a valódi WinForms updater UI-t nyitja meg fájlmódosítás nélkül.
- A preview ablakot érdemes 100%, 125% és 150% Windows DPI-n is ellenőrizni.
