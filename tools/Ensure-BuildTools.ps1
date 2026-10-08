param(
    [string]$ToolsRoot = (Join-Path $PSScriptRoot 'bin')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Write-Info([string]$Message) {
    Write-Host "[BuildTools] $Message"
}

function Get-ExistingAria2 {
    $local = Join-Path $ToolsRoot 'aria2c.exe'
    if (Test-Path -LiteralPath $local) { return (Resolve-Path $local).Path }
    $cmd = Get-Command aria2c.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Get-Existing7Zip {
    $candidates = @(
        (Join-Path $ToolsRoot '7zip\7za.exe'),
        (Join-Path $ToolsRoot '7zip\7z.exe'),
        (Join-Path $ToolsRoot '7za.exe'),
        (Join-Path $ToolsRoot '7z.exe'),
        (Join-Path $env:ProgramFiles '7-Zip\7z.exe')
    )
    if (${env:ProgramFiles(x86)}) {
        $candidates += (Join-Path ${env:ProgramFiles(x86)} '7-Zip\7z.exe')
    }
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return (Resolve-Path $candidate).Path
        }
    }
    foreach ($name in @('7z.exe','7za.exe')) {
        $cmd = Get-Command $name -ErrorAction SilentlyContinue
        if ($cmd) { return $cmd.Source }
    }
    return $null
}

function Download-File {
    param(
        [Parameter(Mandatory=$true)][string]$Url,
        [Parameter(Mandatory=$true)][string]$Destination,
        [string]$Aria2Path
    )
    $dir = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Remove-Item -LiteralPath $Destination -Force -ErrorAction SilentlyContinue

    if ($Aria2Path -and (Test-Path -LiteralPath $Aria2Path)) {
        Write-Info "aria2c: $Url"
        & $Aria2Path '--allow-overwrite=true' '--auto-file-renaming=false' '--file-allocation=none' '-x' '16' '-s' '16' '-k' '1M' '-d' $dir '-o' (Split-Path -Leaf $Destination) $Url
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $Destination)) { return }
        Write-Info 'aria2c sikertelen, fallback Invoke-WebRequest...'
    }

    Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $Destination
}

New-Item -ItemType Directory -Force -Path $ToolsRoot | Out-Null
$cache = Join-Path $ToolsRoot 'cache'
New-Item -ItemType Directory -Force -Path $cache | Out-Null

# aria2c - pinned portable Windows x64 build for reproducible builds.
$aria2 = Get-ExistingAria2
if (-not $aria2) {
    Write-Info 'aria2c nem talalhato. Portable verzio letoltese...'
    $ariaZip = Join-Path $cache 'aria2-1.37.0-win-64bit-build1.zip'
    $ariaUrl = 'https://github.com/aria2/aria2/releases/download/release-1.37.0/aria2-1.37.0-win-64bit-build1.zip'
    try {
        Download-File -Url $ariaUrl -Destination $ariaZip
        $tmp = Join-Path $cache 'aria2-extract'
        Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
        Expand-Archive -LiteralPath $ariaZip -DestinationPath $tmp -Force
        $found = Get-ChildItem -LiteralPath $tmp -Recurse -Filter aria2c.exe -File | Select-Object -First 1
        if (-not $found) { throw 'aria2c.exe nem talalhato a letoltott csomagban.' }
        $aria2 = Join-Path $ToolsRoot 'aria2c.exe'
        Copy-Item -LiteralPath $found.FullName -Destination $aria2 -Force
        Write-Info "aria2c kesz: $aria2"
    } catch {
        Write-Warning "aria2c automatikus letoltese sikertelen: $($_.Exception.Message)"
        $aria2 = $null
    }
} else {
    Write-Info "aria2c: $aria2"
}

# 7-Zip portable console. Prefer installed 7z; otherwise bootstrap 7zr and extract 7za from the official Extra package.
$sevenZip = Get-Existing7Zip
if (-not $sevenZip) {
    Write-Info '7-Zip nem talalhato. Portable console letoltese...'
    try {
        $sevenDir = Join-Path $ToolsRoot '7zip'
        New-Item -ItemType Directory -Force -Path $sevenDir | Out-Null
        $sevenR = Join-Path $cache '7zr.exe'
        $extra = Join-Path $cache '7z2409-extra.7z'
        Download-File -Url 'https://www.7-zip.org/a/7zr.exe' -Destination $sevenR -Aria2Path $aria2
        Download-File -Url 'https://www.7-zip.org/a/7z2409-extra.7z' -Destination $extra -Aria2Path $aria2
        & $sevenR x $extra "-o$sevenDir" -y | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "7zr extraction error: $LASTEXITCODE" }
        $found = Get-ChildItem -LiteralPath $sevenDir -Recurse -File | Where-Object { $_.Name -in @('7za.exe','7z.exe') } | Select-Object -First 1
        if (-not $found) { throw '7za.exe/7z.exe nem talalhato a portable csomagban.' }
        $sevenZip = $found.FullName
        Write-Info "7-Zip kesz: $sevenZip"
    } catch {
        Write-Warning "7-Zip automatikus letoltese sikertelen: $($_.Exception.Message)"
        $sevenZip = $null
    }
} else {
    Write-Info "7-Zip: $sevenZip"
}

# Emit a cmd file so batch scripts can consume the exact paths without parsing console output.
$envCmd = Join-Path $ToolsRoot 'build-tools.cmd'
$lines = @('@echo off')
if ($aria2) { $lines += ('set "TURUL_ARIA2={0}"' -f $aria2) } else { $lines += 'set "TURUL_ARIA2="' }
if ($sevenZip) { $lines += ('set "TURUL_7ZIP={0}"' -f $sevenZip) } else { $lines += 'set "TURUL_7ZIP="' }
Set-Content -LiteralPath $envCmd -Value $lines -Encoding ASCII
Write-Info "Environment: $envCmd"
