@echo off
setlocal
cd /d "%~dp0"
dotnet run --project "tests\TurulMC.Recovery.SmokeTests" -c Release
exit /b %errorlevel%
