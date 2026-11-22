<#
.SYNOPSIS
  Builds a release: the companion's installer (with auto-update packages) and the addon zip.

.DESCRIPTION
  Output goes to dist\:
    dist\releases\WoWAudioVibes-win-Setup.exe     installer people download (installs per user, no admin)
    dist\releases\WoWAudioVibes-<ver>-full.nupkg  + -delta.nupkg, releases.win.json, RELEASES: update packages
    dist\releases\WoWAudioVibes-win-Portable.zip  no-install version (doesn't auto-update)
    dist\AudioVibes-<ver>.zip                     the addon: unzip into ...\_forever_\Interface\AddOns\

  Every file in dist\releases has to be attached to the GitHub release: the installed app checks
  the latest release's releases.win.json and downloads the .nupkg from there.

  Without -Publish nothing leaves your machine. With -Publish it creates the GitHub release vX.Y.Z and
  uploads everything (needs a token with "contents: write" in $env:GITHUB_TOKEN, plus the gh CLI for
  the addon zip). Pushing a tag v1.2.3 runs the same thing on GitHub Actions (.github/workflows/release.yml).

.EXAMPLE
  .\scripts\release.ps1 -Version 1.2.0
  .\scripts\release.ps1 -Version 1.2.1 -Notes notes.md -Publish
#>
param(
  [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')][string]$Version,
  [string]$Notes,
  [switch]$Publish,
  [switch]$SkipTests,
  [string]$RepoUrl = "https://github.com/cnowacek/wow-audio-vibes"
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$dist = Join-Path $root "dist"
$publishDir = Join-Path $dist "publish"
$releases = Join-Path $dist "releases"
$app = Join-Path $root "companion\src\AudioVibes.App\AudioVibes.App.csproj"

function Step($m) { Write-Host "`n== $m" -ForegroundColor Cyan }
function Check($what) { if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" } }

Push-Location $root
try {
  if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
  if (Test-Path $releases) { Remove-Item $releases -Recurse -Force }
  New-Item -ItemType Directory -Force $publishDir, $releases | Out-Null

  if (-not $SkipTests) {
    Step "Companion tests"
    dotnet test companion\AudioVibes.Companion.sln -c Release --nologo -v q; Check "dotnet test"
  }

  Step "Publishing the companion $Version (self-contained win-x64)"
  dotnet publish $app -c Release -r win-x64 --self-contained -p:Version=$Version -o $publishDir --nologo -v q
  Check "dotnet publish"

  Step "Packing the installer with Velopack"
  dotnet tool restore | Out-Null; Check "dotnet tool restore"
  # Earlier releases let Velopack build a small delta package. Optional: the first release has none.
  try { dotnet vpk download github --repoUrl $RepoUrl -o $releases --token "$env:GITHUB_TOKEN" 2>$null | Out-Null } catch { }
  $pack = @("vpk", "pack", "-u", "WoWAudioVibes", "-v", $Version, "-p", $publishDir, "-e", "WoWAudioVibes.exe",
            "-o", $releases, "--packTitle", "WoW Audio Vibes", "--packAuthors", "WoW Audio Vibes",
            "-i", (Join-Path $root "companion\src\AudioVibes.App\app.ico"), "--shortcuts", "StartMenuRoot")
  if ($Notes) { $pack += @("--releaseNotes", (Resolve-Path $Notes)) }
  dotnet @pack; Check "vpk pack"

  Step "Zipping the addon"
  $stage = Join-Path $dist "addon-stage"
  if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
  New-Item -ItemType Directory $stage | Out-Null
  Copy-Item (Join-Path $root "addon\AudioVibes") (Join-Path $stage "AudioVibes") -Recurse
  # Your Resolved.lua holds your own companion's choices; ship the empty stub instead.
  Set-Content (Join-Path $stage "AudioVibes\Resolved.lua") -Encoding ascii -Value @'
-- Written by the WoW Audio Vibes companion app (the playlist it has chosen for each location, so
-- the addon can show real names and copyable links). This shipped copy is an empty stub; the
-- companion overwrites it. WoW reads it on login and /reload.
AudioVibesResolved = { locations = {}, types = {} }
'@
  Get-ChildItem $stage -Recurse -Include *.tmp | Remove-Item -Force
  $toc = Join-Path $stage "AudioVibes\AudioVibes.toc"
  (Get-Content $toc) -replace '^## Version: .*', "## Version: $Version" | Set-Content $toc -Encoding utf8
  $addonZip = Join-Path $dist "AudioVibes-$Version.zip"
  if (Test-Path $addonZip) { Remove-Item $addonZip }
  # Not Compress-Archive: Windows PowerShell 5.1 writes "\" into entry names, which other unzippers mishandle.
  Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
  $zip = [IO.Compression.ZipFile]::Open($addonZip, [IO.Compression.ZipArchiveMode]::Create)
  try {
    foreach ($f in Get-ChildItem $stage -Recurse -File) {
      $name = $f.FullName.Substring($stage.Length + 1).Replace('\', '/')
      [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
    }
  } finally { $zip.Dispose() }
  Remove-Item $stage -Recurse -Force

  if ($Publish) {
    if (-not $env:GITHUB_TOKEN) { throw "-Publish needs `$env:GITHUB_TOKEN" }
    Step "Publishing GitHub release v$Version"
    $pre = if ($Version -match '-') { @("--pre") } else { @() }
    dotnet vpk upload github --repoUrl $RepoUrl -o $releases --token $env:GITHUB_TOKEN --publish --merge `
      --tag "v$Version" --releaseName "WoW Audio Vibes $Version" @pre
    Check "vpk upload"
    gh release upload "v$Version" $addonZip --clobber --repo ($RepoUrl -replace '^https://github.com/', '')
    Check "gh release upload"
  }

  Step "Done"
  Get-ChildItem $releases, $addonZip | Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize | Out-Host
  if (-not $Publish) {
    Write-Host "Nothing was uploaded. Attach everything in dist\releases plus $(Split-Path $addonZip -Leaf) to a GitHub release v$Version,"
    Write-Host "or push the tag (git tag v$Version; git push origin v$Version) and let the release workflow do it."
  }
}
finally { Pop-Location }
