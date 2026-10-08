@echo off
setlocal
cd /d "%~dp0"

title TurulLauncher 4.6.0 Developer Build
echo.
echo ========================================
echo        TurulLauncher 4.6.0 Developer Build
echo ========================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [HIBA] A .NET SDK nem talalhato a PATH-ban.
    echo Telepitsd a projekt altal igenyelt .NET SDK-t, majd futtasd ujra.
    echo.
    pause
    exit /b 1
)

echo [1/5] Regi build fajlok torlese...
for %%D in (
    "src\TurulMC.Launcher\bin"
    "src\TurulMC.Launcher\obj"
    "src\TurulMC.Core\bin"
    "src\TurulMC.Core\obj"
    "src\TurulMC.Infrastructure\bin"
    "src\TurulMC.Infrastructure\obj"
    "tests\TurulMC.Core.Tests\bin"
    "tests\TurulMC.Core.Tests\obj"
) do (
    if exist "%%~D" rmdir /s /q "%%~D"
)

echo [2/5] NuGet csomagok visszaallitasa...
dotnet restore "TurulMC.sln"
if errorlevel 1 goto :fail

echo [3/5] Release build...
dotnet build "TurulMC.sln" -c Release --no-restore
if errorlevel 1 goto :fail

echo [4/5] Tesztek futtatasa...
dotnet test "tests\TurulMC.Core.Tests\TurulMC.Core.Tests.csproj" -c Release --no-build
if errorlevel 1 goto :fail

echo [Recovery] Automatikus modmentes es hibaelemzes tesztjei...
dotnet run --project "tests\TurulMC.Recovery.SmokeTests" -c Release
if errorlevel 1 goto :fail

echo [5/5] TurulLauncher 4.6.0 inditasa...
echo.
dotnet run --project "src\TurulMC.Launcher" -c Release --no-build
exit /b %errorlevel%

:fail
echo.
echo [HIBA] A TurulLauncher 4.6.0 build vagy teszteles megszakadt.
echo Nezd meg a fenti hibauzenetet.
echo.
pause
exit /b 1
