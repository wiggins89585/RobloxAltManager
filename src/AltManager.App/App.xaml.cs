using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AltManager.App.Services;
using AltManager.App.ViewModels;
using AltManager.Core;
using AltManager.Core.Launch;
using AltManager.Core.Roblox;
using AltManager.Core.Vault;

namespace AltManager.App;

/// <summary>
/// Composition root. Order matters:
///   1. Grab the Roblox singleton mutex BEFORE anything can start a client.
///   2. Unlock the vault (DPAPI silently, or prompt for the AES master password).
///   3. Wire services into the view-model.
/// Launch with --password-vault the first time to create an AES-GCM vault instead of DPAPI.
/// </summary>
public partial class App : Application
{
    private MainViewModel? _vm;
    private readonly CancellationTokenSource _shutdown = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown; // don't exit when the password prompt closes

        var guard = new MultiInstanceGuard();
        var guardOk = guard.Acquire();

        AccountVault vault;
        try
        {
            vault = await OpenVaultAsync(e.Args.Contains("--password-vault"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not open vault", MessageBoxButton.OK, MessageBoxImage.Error);
            guard.Dispose();
            Shutdown();
            return;
        }

        var settings = AppSettings.Load();
        _vm = new MainViewModel(vault, new RobloxApiClient(), new ProcessTracker(), guard, new DialogService(), settings);
        var window = new MainWindow { DataContext = _vm };
        if (settings.RememberView && settings is { WindowWidth: >= 820, WindowHeight: >= 420 })
        {
            window.Width = settings.WindowWidth;
            window.Height = settings.WindowHeight;
        }
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();

        if (!guardOk)
            MessageBox.Show(window,
                "A Roblox client was already running, so multi-instance mode could not be enabled.\n" +
                "Close all Roblox windows; the manager will retry on your next launch.",
                "Multi-instance", MessageBoxButton.OK, MessageBoxImage.Information);

        await _vm.LoadAvatarsAsync(vault.Accounts.Select(a => a.UserId));
        await _vm.RunStartupTasksAsync();
        _ = CheckForUpdatesAsync(settings, e.Args.Contains("--password-vault"), window);
    }

    private async Task CheckForUpdatesAsync(AppSettings settings, bool usePasswordVault, Window owner)
    {
#if DEBUG
        return;
#else
        if (!settings.AutoUpdateEnabled)
            return;

        var updater = new GitHubUpdateService();
        AvailableUpdate? availableUpdate;
        try
        {
            var currentVersion = typeof(App).Assembly.GetName().Version ?? new Version(1, 0);
            availableUpdate = await updater.CheckForUpdateAsync(currentVersion, _shutdown.Token);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return;
        }
        catch (HttpRequestException ex)
        {
            Trace.WriteLine($"Update check failed: {ex.Message}");
            return;
        }
        catch (TaskCanceledException ex)
        {
            Trace.WriteLine($"Update check timed out: {ex.Message}");
            return;
        }
        catch (JsonException ex)
        {
            Trace.WriteLine($"Could not read GitHub release metadata: {ex.Message}");
            return;
        }
        catch (InvalidDataException ex)
        {
            MessageBox.Show(owner, ex.Message, "Update unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (availableUpdate is null || _shutdown.IsCancellationRequested)
            return;

        var confirmation = MessageBox.Show(owner,
            $"A new update has been detected. Would you like to update to ver {availableUpdate.Version.ToString(3)}?",
            "Update available", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes || _shutdown.IsCancellationRequested)
            return;

        PreparedUpdate update;
        try
        {
            update = await updater.DownloadAsync(availableUpdate, _shutdown.Token);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return;
        }
        catch (TaskCanceledException ex)
        {
            Trace.WriteLine($"Update download timed out: {ex.Message}");
            MessageBox.Show(owner, "The update download timed out. Try again later.",
                "Update failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        catch (InvalidDataException ex)
        {
            MessageBox.Show(owner, ex.Message, "Update unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        catch (IOException ex)
        {
            MessageBox.Show(owner, $"Could not download or unpack the update: {ex.Message}",
                "Update failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            MessageBox.Show(owner, $"Could not save the update: {ex.Message}",
                "Update failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(owner, ex.Message, "Update failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            updater.StartApply(update, usePasswordVault);
            MessageBox.Show(owner,
                $"Update {update.Version.ToString(3)} is ready. The app will restart to install it.",
                "Roblox Alt Manager update", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            MessageBox.Show(owner, $"Could not start the update helper: {ex.Message}",
                "Update failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (IOException ex)
        {
            MessageBox.Show(owner, $"Could not prepare the update helper: {ex.Message}",
                "Update failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (UnauthorizedAccessException ex)
        {
            MessageBox.Show(owner, $"Could not prepare the update helper: {ex.Message}",
                "Update failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(owner, ex.Message, "Update failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
#endif
    }

    private static async Task<AccountVault> OpenVaultAsync(bool wantPassword)
    {
        var path = AccountVault.DefaultPath;
        var (cipherId, salt) = AccountVault.Inspect(path);

        var usePassword = cipherId == "aesgcm" || (cipherId is null && wantPassword);
        if (!usePassword)
            return await AccountVault.OpenAsync(path, new DpapiCipher());

        var isNew = cipherId is null;
        salt ??= AesGcmCipher.NewSalt();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var pw = PromptPassword(isNew ? "Create a master password for the vault:" : "Vault master password:");
            if (pw is null) throw new OperationCanceledException("Vault unlock cancelled.");
            try
            {
                // Cipher lives for the app lifetime (it is the vault's key holder).
                return await AccountVault.OpenAsync(path, AesGcmCipher.FromPassword(pw, salt), salt);
            }
            catch (CryptographicException) when (!isNew)
            {
                MessageBox.Show("Wrong password.", "Vault", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        throw new CryptographicException("Too many failed attempts.");
    }

    /// <summary>Minimal code-only password dialog (keeps the sample to fewer XAML files).</summary>
    private static string? PromptPassword(string prompt)
    {
        var box = new PasswordBox { Margin = new Thickness(0, 8, 0, 8), MinWidth = 280 };
        var ok = new Button { Content = "Unlock", IsDefault = true, Width = 80, HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = prompt });
        panel.Children.Add(box);
        panel.Children.Add(ok);

        var w = new Window
        {
            Title = "Alt Manager Vault", Content = panel, SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) => box.Focus();
        return w.ShowDialog() == true && box.Password.Length > 0 ? box.Password : null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdown.Cancel();
        _vm?.Dispose(); // releases the singleton mutex last-ish; running clients keep running
        base.OnExit(e);
    }
}
