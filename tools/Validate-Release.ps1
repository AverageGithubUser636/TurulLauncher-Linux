param(
  [string]$Version='4.6.0'
)
$ErrorActionPreference='Stop'

$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$zip=Join-Path $root "publish\TurulLauncher-$Version.zip"
$manifestPath=Join-Path $root 'publish\stable.json'
$setup=Join-Path $root 'publish\Installer\TurulLauncher-Setup.exe'
$updater=Join-Path $root 'publish\GUI\TurulMC.Updater.exe'
$launcher=Join-Path $root 'publish\GUI\TurulMC.Launcher.exe'
$pri=Join-Path $root 'publish\GUI\TurulMC.Launcher.pri'
$icon=Join-Path $root 'publish\GUI\Assets\turullauncher.ico'

foreach ($f in @($zip,$manifestPath,$setup,$updater,$launcher,$pri,$icon)) {
  if (-not (Test-Path -LiteralPath $f)) { throw "Hianyzo release fajl: $f" }
}

$m=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($m.version -ne $Version) { throw "Manifest verzio elteres: $($m.version) != $Version" }
$expectedUrl="https://turulnetwork.hu/launcher/releases/TurulLauncher-$Version.zip"
if ($m.url -ne $expectedUrl) { throw "Manifest URL hibas: $($m.url)" }
if ($m.sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Manifest SHA-256 formatuma hibas.' }
$actual=(Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()
if ($actual -ne $m.sha256.ToLowerInvariant()) { throw "SHA-256 nem egyezik. ZIP=$actual JSON=$($m.sha256)" }
if (-not $m.changelog -or @($m.changelog).Count -lt 1) { throw 'A changelog ures.' }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[System.IO.Compression.ZipFile]::OpenRead($zip)
try {
  $pdbEntries=@($archive.Entries | Where-Object { $_.FullName -match '(?i)\.pdb$' })
  if ($pdbEntries.Count -gt 0) {
    throw ('A public release ZIP PDB fajlt tartalmaz: ' + (($pdbEntries | Select-Object -ExpandProperty FullName) -join ', '))
  }
} finally {
  $archive.Dispose()
}

Write-Host 'Release validation: OK'
Write-Host "Version: $Version"
Write-Host "SHA256:  $actual"
Write-Host "Setup:   $setup"
Write-Host "PRI:     $pri"
Write-Host "Icon:    $icon"
