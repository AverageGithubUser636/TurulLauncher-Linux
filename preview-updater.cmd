@echo off
setlocal
cd /d "%~dp0"
if exist "publish\Updater\TurulMC.Updater.exe" (
  start "" "publish\Updater\TurulMC.Updater.exe" --preview
) else (
  start "" "tools\updater-preview.html"
)
