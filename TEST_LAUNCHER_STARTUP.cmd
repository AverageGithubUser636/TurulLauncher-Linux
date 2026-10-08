@echo off
setlocal
set "EXE=%LOCALAPPDATA%\Programs\TurulLauncher\TurulMC.Launcher.exe"
echo ========================================
echo   TurulLauncher startup diagnostic
 echo ========================================
if not exist "%EXE%" (
  echo [HIBA] Nem talalhato: %EXE%
  pause
  exit /b 1
)
powershell -NoProfile -Command "$p=Start-Process -FilePath '%EXE%' -WorkingDirectory (Split-Path '%EXE%') -PassThru; Start-Sleep -Seconds 5; if($p.HasExited){Write-Host ('Launcher kilepett. Exit code: '+$p.ExitCode)} else {Write-Host 'Launcher process fut.'}"
echo.
echo Startup logok:
dir /b /o-d "%APPDATA%\TurulMC\logs\startup*" 2>nul
echo.
echo Ha nem indult el, nyisd meg ezt a mappat:
echo %APPDATA%\TurulMC\logs
pause
