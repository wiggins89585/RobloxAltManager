# Releasing

1. Bump `<Version>` in `src/AltManager.App/AltManager.App.csproj` (the auto-updater compares versions, so every release must be higher than the last tag).
2. Run the release script:

   ```powershell
   pwsh scripts/release.ps1 -Version 1.0.19
   ```

   It publishes, signs (if a certificate is configured), zips as `RobloxAltManager-v<ver>-win-x64.zip`, and creates the GitHub release. The zip name and the release's SHA-256 digest are what the in-app updater verifies.

## Code signing (removes the SmartScreen warning)

Right now the `.exe` is **unsigned**, so Windows SmartScreen shows an "unknown publisher" warning the first time someone runs it or when it auto-updates. To remove that, you need an **Authenticode code-signing certificate**. This has to be bought from a certificate authority (DigiCert, Sectigo, SSL.com, etc.) — a self-made certificate won't be trusted on other people's machines, and signing can't be done without one.

Two kinds exist:
- **OV (standard)** — cheaper; SmartScreen trust builds up over downloads/time.
- **EV** — more expensive, usually on a hardware token; grants SmartScreen reputation immediately.

Once you have a certificate, configure **one** of these before releasing, and `sign.ps1` picks it up automatically:

```powershell
# A .pfx file on disk:
$env:CODESIGN_PFX = 'C:\path\to\cert.pfx'
$env:CODESIGN_PASSWORD = '<pfx password>'

# …or a certificate already installed in CurrentUser\My:
$env:CODESIGN_THUMBPRINT = '<thumbprint>'
```

Signing also needs `signtool.exe`, which comes with the Windows 10/11 SDK. With nothing configured, the scripts still work — they just publish unsigned and print a warning.
