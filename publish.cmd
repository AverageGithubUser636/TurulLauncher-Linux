@echo off
setlocal
cd /d "%~dp0"
set "TURUL_RUN=%RANDOM%-%TIME::=%"
echo [Publish #%TURUL_RUN%] start %DATE% %TIME%
if exist ".publish.lock" (
  echo.
  echo [HIBA] Mar fut egy publish, vagy az elozo osszeomlott. Lock: .publish.lock
  echo Tartalma:
  type ".publish.lock"
  echo.
  echo Ha sehol sem fut publish, torold a .publish.lock fajlt es futtasd ujra.
  echo.
  if not defined TURUL_NO_PAUSE pause
  exit /b 1
)
echo run=%TURUL_RUN% date=%DATE% time=%TIME% user=%USERNAME%@%COMPUTERNAME% > ".publish.lock"
echo ========================================
echo       TurulLauncher 4.6.0 Publish
echo ========================================

rem --- Verzió-tudatos takarítás -------------------------------------------------
rem A korábbi publish-t CSAK akkor töröljük, ha az MÁS verziójú (különben elég a
rem frissítés: a dotnet publish ugyanoda ír, a ZIP/installer pedig felülíródik).
set "TURUL_VER="
for /f "tokens=3 delims=<>" %%v in ('findstr /r /c:"<Version>" "src\TurulMC.Launcher\TurulMC.Launcher.csproj"') do set "TURUL_VER=%%v"
if not defined TURUL_VER (
  echo [FIGYELEM] A verzio nem olvashato a csproj-bol - a meglevo publish-t NEM toroljuk.
)
set "TURUL_OLDVER="
if exist "publish\.turul-version" set /p TURUL_OLDVER=<"publish\.turul-version"
if not defined TURUL_OLDVER (
  rem Régi publish (verzió-jelölő nélkül): az exe fájlverziójából olvassuk ki.
  for /f "delims=" %%v in ('powershell -NoProfile -Command "try{(Get-Item -LiteralPath 'publish\GUI\TurulMC.Launcher.exe').VersionInfo.FileVersion}catch{}"') do set "TURUL_OLDVER=%%v"
)
rem Az exe fájlverziója 4.6.0.0, a csproj verziója 4.6.0 — ezért az első három
rem számmezőt hasonlítjuk össze (a [version] típus kezeli a 3 és 4 tagú alakot is).
rem Ha a RÉGI verzió nem állapítható meg, biztonságos takarítunk (nem keveredjen
rem egy régi publish a mostanival). Ha az ÚJ verzió nem olvasható, nem törlünk.
set "TURUL_VCMP=DIFF"
if not defined TURUL_VER set "TURUL_VCMP=SAME"
if defined TURUL_VER if defined TURUL_OLDVER (
  for /f "delims=" %%r in ('powershell -NoProfile -Command "try{$o=[version]'%TURUL_OLDVER%';$n=[version]'%TURUL_VER%';if($o.Major -eq $n.Major -and $o.Minor -eq $n.Minor -and $o.Build -eq $n.Build){'SAME'}else{'DIFF'}}catch{'DIFF'}"') do set "TURUL_VCMP=%%r"
)
if exist publish (
  if /i "%TURUL_VCMP%"=="SAME" (
    echo [Publish #%TURUL_RUN%] Ugyanaz a verzio ^(%TURUL_VER%^) - a korabbi publish megmarad, csak frissul.
  ) else (
    echo [Publish #%TURUL_RUN%] Verzio valtozas: "%TURUL_OLDVER%" -^> "%TURUL_VER%" - a korabbi publish torlese...
    rmdir /s /q publish
    if exist publish (
      echo [HIBA] A korabbi publish mappa nem torolheto ^(fut a launcher/updater?^).
      goto :fail
    )
  )
)
rem ------------------------------------------------------------------------------

rem Fast build tools: use installed aria2/7-Zip, otherwise download portable copies automatically.
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Ensure-BuildTools.ps1
if exist "tools\bin\build-tools.cmd" call "tools\bin\build-tools.cmd"

dotnet restore TurulMC.sln || goto :fail
dotnet publish src\TurulMC.Updater\TurulMC.Updater.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\Updater || goto :fail
dotnet publish src\TurulMC.Launcher\TurulMC.Launcher.csproj -c Release -r win-x64 --self-contained true -o publish\GUI || goto :fail
if not exist "publish\GUI\TurulMC.Launcher.pri" (
  echo.
  echo [HIBA] Hianyzik a publishbol: publish\GUI\TurulMC.Launcher.pri
  echo Ez XamlParseException 0x802B000A hibahoz vezetne.
  goto :fail
)
copy /y publish\Updater\TurulMC.Updater.exe publish\GUI\TurulMC.Updater.exe >nul || goto :fail

rem Keep debug symbols for the developer, but never ship them to players.
if exist "publish\Symbols" rmdir /s /q "publish\Symbols"
mkdir "publish\Symbols" >nul 2>nul
powershell -NoProfile -ExecutionPolicy Bypass -Command "$files=Get-ChildItem -LiteralPath 'publish\GUI' -Recurse -Filter '*.pdb' -File; foreach($f in $files){ Copy-Item -LiteralPath $f.FullName -Destination (Join-Path 'publish\Symbols' $f.Name) -Force; Remove-Item -LiteralPath $f.FullName -Force }" || goto :fail
dotnet publish src\TurulMC.Cli\TurulMC.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\CLI || goto :fail
if exist "publish\TurulLauncher-4.6.0.zip" del /f /q "publish\TurulLauncher-4.6.0.zip"
if not defined TURUL_ZIP_LEVEL set "TURUL_ZIP_LEVEL=3"
rem "defined" alone is true for a stale/empty path too, so validate the exe exists.
set "TURUL_7ZIP_OK="
if defined TURUL_7ZIP if exist "%TURUL_7ZIP%" set "TURUL_7ZIP_OK=1"
if defined TURUL_7ZIP_OK (
  echo [Publish #%TURUL_RUN%] ag: 7-Zip
  echo.
  echo ZIP keszitese 7-Zip-pel: "%TURUL_7ZIP%" ^(mx=%TURUL_ZIP_LEVEL%, multithread^)
  pushd "publish\GUI"
  rem -bb0: nincs fajlonkenti lista; -bsp1: szazalekos folyamatjelzo.
  "%TURUL_7ZIP%" a -tzip "..\TurulLauncher-4.6.0.zip" "*" -mx=%TURUL_ZIP_LEVEL% -mmt=on -y -bb0 -bsp1
  if errorlevel 1 (
    popd
    goto :fail
  )
  popd
) else (
  echo [Publish #%TURUL_RUN%] ag: beepitett ZIP iro (szazalekos kijelzes)
  echo [FIGYELEM] 7-Zip nem erheto el, TURUL_7ZIP=%TURUL_7ZIP% -- sajat ZIP iro.
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\Zip-Folder.ps1 -Source "publish\GUI" -Destination "publish\TurulLauncher-4.6.0.zip" || goto :fail
)
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Make-UpdateManifest.ps1 -Zip publish\TurulLauncher-4.6.0.zip -Version 4.6.0 -Channel stable || goto :fail
copy /y server\update\stable.json publish\stable.json >nul || goto :fail
rem Verzió-jelölő: a következő publish ebből tudja, kell-e takarítani.
if defined TURUL_VER (
  > "publish\.turul-version" echo %TURUL_VER%
)
echo.
echo KESZ:
echo   publish\GUI\
echo   publish\GUI\TurulMC.Updater.exe
echo   publish\TurulLauncher-4.6.0.zip
echo   publish\stable.json
echo   publish\CLI\TurulLauncher.CLI.exe
echo.
echo Updater preview:
echo   preview-updater.cmd
echo   vagy: publish\Updater\TurulMC.Updater.exe --preview
echo.
echo Installer keszites:
echo   build-installer.cmd
echo   vagy minden egyben: publish-all.cmd
echo.
echo Manifest keszites pelda:
echo powershell -ExecutionPolicy Bypass -File tools\Make-UpdateManifest.ps1 -Zip publish\TurulLauncher-4.6.0.zip -Version 4.6.0 -Channel stable
echo [Publish #%TURUL_RUN%] KESZ %DATE% %TIME%
del /f /q ".publish.lock" >nul 2>nul
if not defined TURUL_NO_PAUSE pause
exit /b 0
:fail
echo.
echo [HIBA] Publish sikertelen (#%TURUL_RUN%).
del /f /q ".publish.lock" >nul 2>nul
if not defined TURUL_NO_PAUSE pause
exit /b 1
