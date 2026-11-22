<#
.SYNOPSIS
  Junction addon/AudioVibes from this repo into a WoW client's Interface\AddOns folder.
.EXAMPLE
  .\scripts\link-addon.ps1
  .\scripts\link-addon.ps1 -Flavor _retail_
  .\scripts\link-addon.ps1 -AddOnsPath "D:\Games\World of Warcraft\_forever_\Interface\AddOns"
#>
param(
  [string]$WowRoot = "C:\Program Files (x86)\World of Warcraft",
  [string]$Flavor = "_forever_",
  [string]$AddOnsPath
)
$ErrorActionPreference = "Stop"
if (-not $AddOnsPath) { $AddOnsPath = Join-Path $WowRoot "$Flavor\Interface\AddOns" }
$source = Resolve-Path (Join-Path $PSScriptRoot "..\addon\AudioVibes")
$target = Join-Path $AddOnsPath "AudioVibes"

if (-not (Test-Path $AddOnsPath)) { throw "AddOns folder not found: $AddOnsPath (use -AddOnsPath)" }
if (-not (Test-Path (Join-Path $source "Commands.lua"))) {
  throw "Commands.lua missing. Run: python generator\generate.py"
}
if (Test-Path $target) {
  $item = Get-Item $target -Force
  if ($item.LinkType -eq "Junction") {
    Write-Host "Removing existing junction $target"
    $item.Delete()
  } else {
    throw "$target exists and is a real folder. Move or delete it first."
  }
}
New-Item -ItemType Junction -Path $target -Target $source | Out-Null
Write-Host "Linked $target -> $source"
Write-Host "Restart WoW fully (not /reload) so it picks up the sound files."
