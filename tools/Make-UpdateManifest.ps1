param(
  [Parameter(Mandatory=$true)][string]$Zip,
  [Parameter(Mandatory=$true)][string]$Version,
  [ValidateSet('stable','beta')][string]$Channel='stable',
  [switch]$Required
)
$ErrorActionPreference='Stop'

if (-not (Test-Path -LiteralPath $Zip)) {
  throw "Release ZIP nem talalhato: $Zip"
}

$hash=(Get-FileHash -Algorithm SHA256 -LiteralPath $Zip).Hash.ToLowerInvariant()
$out=Join-Path $PSScriptRoot "..\server\update\$Channel.json"

# Keep this .ps1 source ASCII-only so Windows PowerShell 5.1 never misreads
# UTF-8 source bytes as the active ANSI code page. Changelog strings are
# stored as Base64-encoded UTF-8 and decoded at runtime.
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Decode-Utf8Base64([string]$Value) {
  return $utf8.GetString([Convert]::FromBase64String($Value))
}

$changelog=@(
  (Decode-Utf8Base64 'QWJsYWsgYmV6w6Fyw6FzaSB2aXNlbGtlZMOpcyBiZcOhbGzDrXTDoXNh'),
  (Decode-Utf8Base64 'SsOhdMOpa2luZMOtdMOhcyB1dMOhbmkgbGF1bmNoZXIgdmlzZWxrZWTDqXM='),
  (Decode-Utf8Base64 'TWluaW1hbGl6w6Fsw6FzIGEgV2luZG93cyB0w6FsY8OhcmE='),
  (Decode-Utf8Base64 'QmV6w6Fyw6Fza29yIG9wY2lvbsOhbGlzIHLDoWvDqXJkZXrDqXM='),
  (Decode-Utf8Base64 'UGxheXRpbWUvcHJvY2VzcyBrw7Z2ZXTDqXMgc3RhYmlsaXTDoXNpIGphdsOtdMOhc29r')
)
if (Test-Path -LiteralPath $out) {
  try {
    $existing=Get-Content -LiteralPath $out -Raw -Encoding UTF8 | ConvertFrom-Json
    if (($existing.version -eq $Version) -and $existing.changelog -and @($existing.changelog).Count -gt 0) {
      $changelog=@($existing.changelog)
    }
  } catch {
    Write-Warning "A regi manifest changelogja nem olvashato, alapertelmezett changelog lesz hasznalva."
  }
}

$data=[ordered]@{
  version=$Version
  required=[bool]$Required
  url="https://turulnetwork.hu/launcher/releases/TurulLauncher-$Version.zip"
  sha256=$hash
  publishedAt=(Get-Date).ToString('o')
  changelog=$changelog
}
$data | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $out -Encoding UTF8
Write-Host "Manifest: $out"
Write-Host "SHA256:   $hash"
