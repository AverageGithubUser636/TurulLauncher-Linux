@echo off
setlocal EnableExtensions
cd /d "%~dp0"
echo ========================================
echo   TurulLauncher ONE-WEB hosting prepare
echo ========================================
echo.
set "TURUL_GITHUB_REPO=AverageGithubUser636/TurulLauncher-Releases"
echo Repo: %TURUL_GITHUB_REPO%
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Prepare-FreeHosting.ps1 -GitHubRepo "%TURUL_GITHUB_REPO%" -Version 4.6.0
if errorlevel 1 goto :fail
echo.
echo Kesz.
echo GitHub assetek: publish\GitHubRelease\
echo Weboldal patch: publish\WebPatch\
echo.
pause
exit /b 0
:fail
echo.
echo [HIBA] Hosting csomag elokeszitese sikertelen.
pause
exit /b 1
