@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo ========================================
echo      TurulLauncher Installer Build
echo ========================================

if not exist "publish\GUI\TurulMC.Launcher.exe" (
  echo.
  echo [HIBA] A publish\GUI\TurulMC.Launcher.exe nem letezik.
  echo Eloszor futtasd a publish.cmd-t.
  echo.
  if not defined TURUL_NO_PAUSE pause
  exit /b 1
)

set "ISCC="
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 7\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 7\ISCC.exe"
if not defined ISCC if defined ProgramFiles(x86) if exist "%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe"
if not defined ISCC if exist "%LocalAppData%\Programs\Inno Setup 7\ISCC.exe" set "ISCC=%LocalAppData%\Programs\Inno Setup 7\ISCC.exe"

if not defined ISCC (
  echo.
  echo [HIBA] Inno Setup nem talalhato.
  echo Telepites ajanlott paranccsal:
  echo   winget install --id JRSoftware.InnoSetup.7 -e -s winget -i
  echo.
  echo Ezutan futtasd ujra ezt a fajlt.
  if not defined TURUL_NO_PAUSE pause
  exit /b 1
)

echo Inno Setup: "%ISCC%"
echo.
rem Ensure download/archive helpers are available here too when build-installer.cmd is run directly.
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Ensure-BuildTools.ps1
if exist "tools\bin\build-tools.cmd" call "tools\bin\build-tools.cmd"
echo.
echo VC++ runtime elokeszitese...
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Get-VCRedist.ps1
if errorlevel 1 goto :fail

if exist "publish\Installer" rmdir /s /q "publish\Installer"
mkdir "publish\Installer" >nul 2>nul

"%ISCC%" "installer\TurulLauncher.iss"
if errorlevel 1 goto :fail

if not exist "publish\Installer\TurulLauncher-Setup-4.6.0.exe" goto :fail
copy /y "publish\Installer\TurulLauncher-Setup-4.6.0.exe" "publish\Installer\TurulLauncher-Setup.exe" >nul

echo.
echo KESZ:
echo   publish\Installer\TurulLauncher-Setup-4.6.0.exe
echo   publish\Installer\TurulLauncher-Setup.exe
echo.
echo Weboldalra a TurulLauncher-Setup.exe mehet.
if not defined TURUL_NO_PAUSE pause
exit /b 0

:fail
echo.
echo [HIBA] Installer build sikertelen.
if not defined TURUL_NO_PAUSE pause
exit /b 1
