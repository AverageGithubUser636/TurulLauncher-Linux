param(
  [string]$GitHubRepo='AverageGithubUser636/TurulLauncher-Releases',
  [string]$Version='4.6.0'
)
$ErrorActionPreference='Stop'

if ($GitHubRepo -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
  throw "Hibas GitHubRepo. Pelda: AverageGithubUser636/TurulLauncher-Releases"
}

$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$zip=Join-Path $root "publish\TurulLauncher-$Version.zip"
$setup=Join-Path $root 'publish\Installer\TurulLauncher-Setup.exe'
$manifest=Join-Path $root 'publish\stable.json'

foreach ($f in @($zip,$setup,$manifest)) {
  if (-not (Test-Path -LiteralPath $f)) { throw "Hianyzo release fajl: $f. Elobb futtasd a publish-all.cmd-t." }
}

$webPatch=Join-Path $root 'publish\WebPatch'
$github=Join-Path $root 'publish\GitHubRelease'
Remove-Item -LiteralPath $webPatch -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $github -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Join-Path $webPatch 'launcher\update') | Out-Null
New-Item -ItemType Directory -Force -Path $github | Out-Null

Copy-Item -LiteralPath $manifest -Destination (Join-Path $webPatch 'launcher\update\stable.json') -Force
Copy-Item -LiteralPath $zip -Destination (Join-Path $github "TurulLauncher-$Version.zip") -Force
Copy-Item -LiteralPath $setup -Destination (Join-Path $github 'TurulLauncher-Setup.exe') -Force

$zipGh="https://github.com/$GitHubRepo/releases/download/v$Version/TurulLauncher-$Version.zip"
$setupGh="https://github.com/$GitHubRepo/releases/download/v$Version/TurulLauncher-Setup.exe"
$redirects=@(
  "/launcher/releases/TurulLauncher-$Version.zip $zipGh 302",
  "/launcher/download/TurulLauncher-Setup.exe $setupGh 302"
)
$redirects | Set-Content -LiteralPath (Join-Path $webPatch 'ADD_TO__redirects.txt') -Encoding UTF8

$headers=@(
  '/launcher/update/*',
  '  Cache-Control: no-store, no-cache, must-revalidate'
)
$headers | Set-Content -LiteralPath (Join-Path $webPatch 'ADD_TO__headers.txt') -Encoding UTF8

$readme=@"
EGY WEBOLDALOS TURUL HOSTING - $Version

Cloudflare Pages: csak a fo turulnetwork.hu projekt.
Launcher oldal: https://turulnetwork.hu/launcher/
Update JSON: https://turulnetwork.hu/launcher/update/stable.json
Nagy fajlok: GitHub Releases, a fo Pages _redirects fajlja iranyit oda.

1) GitHub assetek:
   publish\GitHubRelease\TurulLauncher-$Version.zip
   publish\GitHubRelease\TurulLauncher-Setup.exe

2) Masold a weboldaladba:
   publish\WebPatch\launcher\update\stable.json
   -> WEB\launcher\update\stable.json

3) Az ADD_TO__redirects.txt ket sorat add a WEB\_redirects vegere.
4) Az ADD_TO__headers.txt sorait add a WEB\_headers vegere.

A launcher.turulnetwork.hu NEM kulon weboldal. Cloudflare Redirect Rule:
launcher.turulnetwork.hu/* -> https://turulnetwork.hu/launcher/*

GitHub repo: $GitHubRepo
Release tag: v$Version
"@
$readme | Set-Content -LiteralPath (Join-Path $webPatch 'README.txt') -Encoding UTF8

Write-Host ''
Write-Host 'Unified hosting package ready.' -ForegroundColor Green
Write-Host "GitHub assets: $github"
Write-Host "Website patch: $webPatch"
