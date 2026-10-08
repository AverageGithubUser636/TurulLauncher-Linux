@echo off
setlocal EnableExtensions
cd /d "%~dp0"
set "TURUL_NO_PAUSE=1"
rem A publish.cmd verzió-tudatos: a korábbi publish-t csak akkor törli, ha az MÁS
rem verziójú (a publish\.turul-version jelölő, illetve az exe fájlverziója alapján).
rem Ha azonos verzió: a meglévő publish megmarad és csak frissül.
call publish.cmd
if errorlevel 1 goto :fail
call build-installer.cmd
if errorlevel 1 goto :fail

powershell -NoProfile -ExecutionPolicy Bypass -File tools\Validate-Release.ps1 -Version 4.6.0
if errorlevel 1 goto :fail

if exist "publish\GitHubRelease" rmdir /s /q "publish\GitHubRelease"
mkdir "publish\GitHubRelease" >nul 2>nul
copy /y "publish\TurulLauncher-4.6.0.zip" "publish\GitHubRelease\TurulLauncher-4.6.0.zip" >nul || goto :fail
copy /y "publish\Installer\TurulLauncher-Setup.exe" "publish\GitHubRelease\TurulLauncher-Setup.exe" >nul || goto :fail

echo.
echo ========================================
echo        MINDEN KESZ - 4.6.0
echo ========================================
echo Auto Update ZIP: publish\TurulLauncher-4.6.0.zip
echo Installer:       publish\Installer\TurulLauncher-Setup.exe
echo Update JSON:     publish\stable.json
echo GitHub assets:   publish\GitHubRelease\
echo.
echo Kovetkezo: prepare-free-hosting.cmd
echo.
pause
exit /b 0

:fail
echo.
echo [HIBA] A teljes publish folyamat megszakadt.
pause
exit /b 1
