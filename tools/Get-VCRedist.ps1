param(
    [string]$Output = (Join-Path $PSScriptRoot "..\prereqs\vc_redist.x64.exe")
)
$ErrorActionPreference = "Stop"
$ProgressPreference = 'SilentlyContinue'
$dir = Split-Path -Parent $Output
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$url = "https://aka.ms/vc14/vc_redist.x64.exe"

$aria2 = $null
$envCmd = Join-Path $PSScriptRoot 'bin\build-tools.cmd'
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'bin\aria2c.exe')) {
    $aria2 = Join-Path $PSScriptRoot 'bin\aria2c.exe'
} else {
    $cmd = Get-Command aria2c.exe -ErrorAction SilentlyContinue
    if ($cmd) { $aria2 = $cmd.Source }
}

if (Test-Path -LiteralPath $Output) {
    $existingSig = Get-AuthenticodeSignature -FilePath $Output
    if ($existingSig.Status -eq 'Valid' -and $existingSig.SignerCertificate.Subject -match 'Microsoft') {
        Write-Host "VC++ Redistributable mar cache-elve: $Output"
        exit 0
    }
    Remove-Item -LiteralPath $Output -Force -ErrorAction SilentlyContinue
}

Write-Host "Microsoft Visual C++ Redistributable letoltese..."
if ($aria2) {
    & $aria2 '--allow-overwrite=true' '--auto-file-renaming=false' '--file-allocation=none' '-x' '16' '-s' '16' '-k' '1M' '-d' $dir '-o' (Split-Path -Leaf $Output) $url
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $Output)) {
        Write-Host 'aria2c sikertelen, fallback Invoke-WebRequest...'
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $Output
    }
} else {
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $Output
}

$sig = Get-AuthenticodeSignature -FilePath $Output
if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'Microsoft') {
    Remove-Item -Force $Output -ErrorAction SilentlyContinue
    throw "A letoltott VC++ Redistributable Microsoft alairasa nem ervenyes."
}
Write-Host "VC++ Redistributable OK: $Output"
