<#
.SYNOPSIS
  Code-signs a file (the app .exe or the release .zip's contents) if a certificate is configured.

.DESCRIPTION
  Signing removes the Windows SmartScreen "unknown publisher" warning that users otherwise see when
  they download and run the app or let it auto-update. It needs an Authenticode code-signing
  certificate, which must be purchased from a CA (e.g. DigiCert, Sectigo) — it can't be generated for
  real-world trust. Configure ONE of:

    $env:CODESIGN_PFX        = path to your .pfx file
    $env:CODESIGN_PASSWORD   = its password
  -- or --
    $env:CODESIGN_THUMBPRINT = thumbprint of a cert already in CurrentUser\My

  With nothing configured, this prints a warning and does nothing, so unsigned local builds still work.
#>
param(
    [Parameter(Mandatory = $true)][string]$Path
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Path)) { throw "File not found: $Path" }

# Find the newest signtool.exe from the installed Windows SDK(s).
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $signtool) {
    Write-Warning "signtool.exe not found (install the Windows 10/11 SDK). Skipping signing of $Path."
    return
}

$timestamp = 'http://timestamp.digicert.com'
if ($env:CODESIGN_PFX) {
    & $signtool sign /fd SHA256 /f $env:CODESIGN_PFX /p $env:CODESIGN_PASSWORD /tr $timestamp /td SHA256 $Path
} elseif ($env:CODESIGN_THUMBPRINT) {
    & $signtool sign /fd SHA256 /sha1 $env:CODESIGN_THUMBPRINT /tr $timestamp /td SHA256 $Path
} else {
    Write-Warning "No code-signing certificate configured (set CODESIGN_PFX + CODESIGN_PASSWORD, or CODESIGN_THUMBPRINT). Skipping signing of $Path."
    return
}
if ($LASTEXITCODE -ne 0) { throw "signtool failed for $Path (exit $LASTEXITCODE)." }
Write-Host "Signed $Path"
