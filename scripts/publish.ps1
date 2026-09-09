<#
.SYNOPSIS
  Builds a single-file tv-audio-relay.exe into dist\<rid>.
.EXAMPLE
  .\scripts\publish.ps1            # win-x64
  .\scripts\publish.ps1 -Rid win-arm64
#>
param(
    [string]$Rid = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "dist\$Rid"

dotnet publish (Join-Path $root "src\TvAudioRelay") `
    -c Release -r $Rid --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $out

Write-Host ""
Write-Host "Built: $out\tv-audio-relay.exe"
Write-Host "Next : $out\tv-audio-relay.exe devices"
