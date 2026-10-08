@echo off
setlocal EnableExtensions
cd /d "%~dp0"
echo ========================================
echo   TurulLauncher GitHub Release upload
echo ========================================
echo.
echo Repo: AverageGithubUser636/TurulLauncher-Releases
echo Verzioszam: automatikus a Launcher projektbol
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Publish-GitHubRelease.ps1
if errorlevel 1 goto :fail
echo.
echo GitHub release feltoltve.
pause
exit /b 0

:fail
echo.
echo [HIBA] GitHub release feltoltes sikertelen.
pause
exit /b 1
