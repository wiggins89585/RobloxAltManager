# Roblox Alt Manager (WPF / .NET 8)

Manage several Roblox accounts you own: import cookies or log in through an embedded browser that renders FunCaptcha properly, browse Roblox in the account manager's dark theme, keep cookies encrypted at rest, organize accounts with notes and custom groups, and launch many clients at once with staggered starts, PID tracking and window tiling.

## Releases

Download the Windows x64 package from [GitHub Releases](../../releases/latest). The package requires the .NET 8 Desktop Runtime and WebView2 Runtime.

Release builds check GitHub Releases at startup and ask before downloading and installing a newer version. The setting is enabled by default and can be changed under **Settings → Updates**. Updates verify the published SHA-256 digest, confirm the installed version before restarting, and require write access to the install folder. Version 1.0.0 cannot update itself; install version 1.0.1 once to enable automatic updates.

## Build

Build requirements: Windows 10 1809 or newer and the .NET 8 SDK. Running the app also requires the WebView2 Runtime (already on Windows 11).

```powershell
dotnet build RobloxAltManager.sln -c Release
.\src\AltManager.App\bin\Release\net8.0-windows10.0.19041.0\AltManager.exe
# Run with --password-vault the first time to use an AES-GCM master password instead of DPAPI
```

## Architecture

```
AltManager.Core   (no UI references; unit-testable)
├─ Models/        Account, LaunchTarget, AuthenticatedUser
├─ Vault/         IVaultCipher → DpapiCipher | AesGcmCipher ; AccountVault (atomic JSON store)
├─ Roblox/        RobloxApiClient (auth check, CSRF, auth tickets, thumbnails, share links, cookie rotation)
├─ Launch/        MultiInstanceGuard, ProtocolUriBuilder, LaunchTargetParser,
│                 LaunchEngine (single + batch), ProcessTracker, WindowArranger
└─ Native/        Win32 P/Invoke (LibraryImport)

AltManager.App    (WPF, MVVM via CommunityToolkit.Mvvm)
├─ App.xaml.cs                 composition root: mutex → vault unlock → VM
├─ Services/                   WebViewEnvironmentFactory (the CAPTCHA fixes), DialogService
├─ ViewModels/                 MainViewModel, AccountViewModel
└─ Views/                      MainWindow, WebLoginWindow, ImportCookieWindow
```

Dependencies only point from App to Core. The view-models never touch windows directly; they go through `IDialogService`.

## Launch pipeline

1. `vault.GetCookie(account)` decrypts the cookie, with the account Guid bound in as entropy or associated data.
2. `POST auth.roblox.com/v1/authentication-ticket`, retried once with the `x-csrf-token` header from the first 403. The ticket is read from the `rbx-authentication-ticket` response header.
3. `roblox-player:1+launchmode:play+gameinfo:{ticket}+…+placelauncherurl:{PlaceLauncher.ashx?request=…}`
   * `RequestGame` joins any server of the place
   * `RequestGameJob&gameId={JobId}` joins a specific server
   * `RequestPrivateGame&accessCode=…/linkCode=…` joins a private server. Share links (`/share?code=…&type=Server`) are resolved to a linkCode first.
4. The engine snapshots `RobloxPlayerBeta` PIDs, starts the URI through the shell, and claims the first new PID. A lock around steps 3–4 keeps each PID matched to the right account.
5. Batch mode spaces launches by a fixed time from one start to the next (5 s by default) and can be cancelled.

## Why embedded FunCaptcha breaks, and the fixes

These are documented in `WebViewEnvironmentFactory.cs` and `WebLoginWindow.xaml.cs`:

| Symptom | Cause | Fix |
|---|---|---|
| Blank or grey puzzle box | WebGL blocked (GPU blocklist, RDP, VM) | `--ignore-gpu-blocklist`, `--use-angle=d3d11`, `--enable-unsafe-swiftshader` (Chromium 137+ dropped the automatic software fallback) |
| Puzzle loops or re-challenges | Third-party storage partitioned or blocked for the arkoselabs iframe | Tracking prevention set to `None`, partitioning features disabled |
| Hardest puzzle tier on every login | A new temp profile each time looks like a new device | Persistent user-data folder; only the auth cookie is deleted afterwards |
| "Automated browser" escalation | Custom UA disagrees with the `Sec-CH-UA` client hints | The engine's own UA is the default; a custom UA is opt-in |
| Captured session dies at once | App called `/logout` to reset the browser | The cookie is deleted locally and the logout endpoint is never called |
| Iframe never paints | WebView hidden, collapsed or zero-size | Normal visible window, 480×640 minimum |

## Security notes

* **DPAPI** (the default): the key is tied to your Windows account. It stops someone who copies the file to another PC, but not malware running as you.
* **AES-GCM**: PBKDF2-SHA256 with 600k iterations. The vault file can be moved between machines, and stays locked while the app is closed.
* Every cookie blob is bound to its record, so swapping ciphertexts between accounts fails authentication.
* Roblox rotates cookies through `Set-Cookie`. `RobloxApiClient.CookieRotated` writes the new value back to the vault so stored cookies don't quietly go stale.
* The `HttpClient` has `UseCookies = false`, so no cookie jar is shared between accounts.

## Caveats

* **Multi-instance:** this depends on Roblox client internals (the `ROBLOX_singletonMutex` / `ROBLOX_singletonEvent` names) and can break when Roblox updates. Start the manager before any Roblox client is running. Roblox's anti-tamper does not currently flag holding the mutex, but this isn't guaranteed.
* **ToS:** use only accounts you own, and keep to Roblox's Terms of Use. Automating gameplay across alts can still get accounts actioned, even if launching them is allowed.
* Avatar images load straight from Roblox's CDN URLs and are not cached on disk.

## Development

Claude Code / Codex assisted.
