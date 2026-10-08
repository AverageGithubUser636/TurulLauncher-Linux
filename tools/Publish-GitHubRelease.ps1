param(
  [string]$GitHubRepo = 'AverageGithubUser636/TurulLauncher-Releases',
  [string]$Version = ''
)

$ErrorActionPreference = 'Stop'

if ($GitHubRepo -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
  throw 'GitHubRepo formatum: owner/repo'
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
  throw 'A GitHub CLI (gh) nincs telepitve. Futtasd: winget install --id GitHub.cli'
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

# Ha nincs explicit verzio, a Launcher projektbol olvassuk ki.
if ([string]::IsNullOrWhiteSpace($Version)) {
  $csproj = Join-Path $root 'src\TurulMC.Launcher\TurulMC.Launcher.csproj'
  if (-not (Test-Path -LiteralPath $csproj)) {
    throw "Nem talalom a Launcher projektet: $csproj"
  }
  [xml]$projectXml = Get-Content -LiteralPath $csproj -Raw
  $Version = [string]$projectXml.Project.PropertyGroup.Version | Select-Object -First 1
  if ([string]::IsNullOrWhiteSpace($Version)) {
    throw 'Nem sikerult kiolvasni a <Version> erteket a TurulMC.Launcher.csproj fajlbol.'
  }
}

Write-Host "Repo:    $GitHubRepo" -ForegroundColor Cyan
Write-Host "Verzio:  $Version" -ForegroundColor Cyan
Write-Host ''

# GitHub CLI auth ellenorzes. A native stderr ne valjon terminating PowerShell hibava.
$oldEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
& gh auth status 1>$null 2>$null
$authExit = $LASTEXITCODE
$ErrorActionPreference = $oldEap
if ($authExit -ne 0) {
  throw 'Nincs GitHub CLI bejelentkezes. Futtasd: gh auth login'
}

# Assetek: elsodlegesen publish\GitHubRelease, fallbackkent publish gyoker.
$assetDirs = @(
  (Join-Path $root 'publish\GitHubRelease'),
  (Join-Path $root 'publish')
)

$zip = $null
$setup = $null
foreach ($dir in $assetDirs) {
  $zipCandidate = Join-Path $dir "TurulLauncher-$Version.zip"
  $setupCandidate = Join-Path $dir 'TurulLauncher-Setup.exe'
  if (-not $setupCandidate -or -not (Test-Path -LiteralPath $setupCandidate)) {
    $setupCandidate = Join-Path $dir 'Installer\TurulLauncher-Setup.exe'
  }
  if ((Test-Path -LiteralPath $zipCandidate) -and (Test-Path -LiteralPath $setupCandidate)) {
    $zip = $zipCandidate
    $setup = $setupCandidate
    break
  }
}

if (-not $zip -or -not $setup) {
  throw "Nem talalom a release asseteket. Futtasd elobb a publish-all.cmd-t. Keresett: TurulLauncher-$Version.zip es TurulLauncher-Setup.exe"
}

Write-Host "ZIP:     $zip"
Write-Host "Setup:   $setup"
Write-Host ''

# Repo ellenorzes + ures repo automatikus inicializalasa.
$oldEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$repoJson = & gh repo view $GitHubRepo --json isEmpty --jq '.isEmpty' 2>$null
$repoExit = $LASTEXITCODE
$ErrorActionPreference = $oldEap
if ($repoExit -ne 0) {
  throw "A repo nem erheto el: $GitHubRepo. Ellenorizd a gh bejelentkezest/jogosultsagot."
}

if (($repoJson | Out-String).Trim().ToLowerInvariant() -eq 'true') {
  Write-Host 'A GitHub repo ures; elso README commit letrehozasa...'
  $readme = @"
# TurulLauncher Releases

Official TurulLauncher release assets.
"@
  $content = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($readme))
  & gh api -X PUT "repos/$GitHubRepo/contents/README.md" -f 'message=Initialize release repository' -f "content=$content" 1>$null
  if ($LASTEXITCODE -ne 0) {
    throw 'Nem sikerult inicializalni az ures GitHub repot.'
  }
  Write-Host 'Repo inicializalva.' -ForegroundColor Green
}

# Release letezes ellenorzese. A "release not found" teljesen normalis uj verzional.
$oldEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
& gh release view "v$Version" --repo $GitHubRepo 1>$null 2>$null
$releaseExists = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = $oldEap

if ($releaseExists) {
  Write-Host "A v$Version release mar letezik; assetek frissitese..."
  & gh release upload "v$Version" $zip $setup --clobber --repo $GitHubRepo
} else {
  Write-Host "v$Version release letrehozasa..."
  & gh release create "v$Version" $zip $setup --repo $GitHubRepo --title "TurulLauncher $Version" --notes "TurulLauncher $Version release"
}

if ($LASTEXITCODE -ne 0) {
  throw 'GitHub release feltoltes sikertelen.'
}

Write-Host ''
Write-Host "KESZ: https://github.com/$GitHubRepo/releases/tag/v$Version" -ForegroundColor Green
