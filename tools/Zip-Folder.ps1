# ZIP készítés százalékos kijelzéssel — fájlonkénti lista helyett.
# Használat: powershell -NoProfile -ExecutionPolicy Bypass -File tools\Zip-Folder.ps1 -Source publish\GUI -Destination publish\TurulLauncher-4.6.0.zip
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Destination,
    [ValidateSet('Optimal', 'Fastest', 'NoCompression')][string]$Level = 'Optimal',
    [int]$QuietAfterPercent = 100
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue

$sourcePath = (Resolve-Path -LiteralPath $Source).Path
$files = @(Get-ChildItem -LiteralPath $sourcePath -Recurse -File)
if ($files.Count -eq 0) { throw "Nincs mit tömöríteni: $sourcePath" }

$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$destinationDir = [System.IO.Path]::GetDirectoryName($destinationPath)
if ($destinationDir -and -not (Test-Path -LiteralPath $destinationDir)) {
    New-Item -ItemType Directory -Force -Path $destinationDir | Out-Null
}
if (Test-Path -LiteralPath $destinationPath) { Remove-Item -LiteralPath $destinationPath -Force }

$totalBytes = ($files | Measure-Object -Property Length -Sum).Sum
if (-not $totalBytes -or $totalBytes -le 0) { $totalBytes = 1 }
$compression = [System.IO.Compression.CompressionLevel]::$Level

$zip = [System.IO.Compression.ZipFile]::Open($destinationPath, [System.IO.Compression.ZipArchiveMode]::Create)
$doneBytes = 0
$lastPercent = -1
$started = Get-Date
try {
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($sourcePath.Length).TrimStart('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $file.FullName, $relative, $compression)
        $doneBytes += $file.Length

        $percent = [int](100 * $doneBytes / $totalBytes)
        if ($percent -ne $lastPercent) {
            $lastPercent = $percent
            $elapsed = (Get-Date) - $started
            $speed = if ($elapsed.TotalSeconds -gt 0.5) { "{0:N1} MB/s" -f ($doneBytes / 1MB / $elapsed.TotalSeconds) } else { "" }
            Write-Host ("`r  ZIP: {0,3}%   {1,6:N0} / {2:N0} MB   {3}   " -f `
                $percent, ($doneBytes / 1MB), ($totalBytes / 1MB), $speed) -NoNewline
        }
    }
}
finally {
    $zip.Dispose()
}

$sizeMb = (Get-Item -LiteralPath $destinationPath).Length / 1MB
$totalSeconds = ((Get-Date) - $started).TotalSeconds
Write-Host ""
Write-Host ("  ZIP kesz: {0}  ({1:N1} MB, {2} fajl, {3:N1} s)" -f $destinationPath, $sizeMb, $files.Count, $totalSeconds)
