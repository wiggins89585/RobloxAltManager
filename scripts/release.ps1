<#
.SYNOPSIS
  Builds, (optionally) signs, zips and publishes a GitHub release for the given version.

.EXAMPLE
  pwsh scripts/release.ps1 -Version 1.0.19

.NOTES
  - Version must match <Version> in src/AltManager.App/AltManager.App.csproj (bump that first).
  - Signing runs automatically if a certificate is configured (see scripts/sign.ps1); otherwise the
    build is published unsigned.
  - Requires the GitHub CLI (gh) to be installed and authenticated.
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$NotesFile
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$csproj = 'src/AltManager.App/AltManager.App.csproj'
if ((Get-Content $csproj -Raw) -notmatch "<Version>$([regex]::Escape($Version))</Version>") {
    throw "csproj <Version> does not match $Version. Bump it in $csproj first."
}

$out = Join-Path $env:TEMP "ram-release-$Version"
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish src/AltManager.App -c Release -r win-x64 --self-contained false -o $out
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

Get-ChildItem $out -Recurse -Filter *.pdb | Remove-Item -Force

# Sign the executable before zipping (no-op if no certificate is configured).
& (Join-Path $PSScriptRoot 'sign.ps1') -Path (Join-Path $out 'AltManager.exe')

$zip = Join-Path $env:TEMP "RobloxAltManager-v$Version-win-x64.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive "$out/*" $zip

$installed = (Get-Item (Join-Path $out 'AltManager.dll')).VersionInfo.ProductVersion
Write-Host "Built version $installed"
Write-Host ("SHA-256: " + (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower())

$args = @("v$Version", $zip, '--target', 'main', '--title', "v$Version")
if ($NotesFile) { $args += @('--notes-file', $NotesFile) } else { $args += @('--generate-notes') }
gh release create @args
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }
Write-Host "Released v$Version"
