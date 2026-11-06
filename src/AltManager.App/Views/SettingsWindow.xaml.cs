using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using AltManager.Core;
using AltManager.Core.Vault;
using Microsoft.Win32;

namespace AltManager.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _s;
    private readonly string _origTheme;
    private readonly string _origAccent;
    private readonly int _origFrost;
    private bool _loadingTheme;

    public SettingsWindow(AppSettings settings)
    {
        _s = settings;
        _origTheme = settings.Theme;
        _origAccent = settings.AccentColor;
        _origFrost = settings.GlassFrost;
        InitializeComponent();
        LaunchOnStartup.IsChecked = _s.LaunchOnWindowsStartup;
        AlwaysOnTop.IsChecked = _s.AlwaysOnTop;
        AutoUpdate.IsChecked = _s.AutoUpdateEnabled;
        AutoStartup.IsChecked = _s.AutoValidateOnStartup;
        Interval.Text = _s.AutoValidateIntervalMinutes.ToString();
        AutoRejoin.IsChecked = _s.AutoRejoin;
        ReloginExpired.IsChecked = _s.AutoReloginExpired;
        OptimizeClient.IsChecked = _s.OptimizeClient;
        FpsCap.Text = _s.OptimizeFpsCap.ToString();
        ParallelBrowser.IsChecked = _s.ParallelBrowserTasks;
        Streamer.IsChecked = _s.StreamerMode;
        ConfirmCopy.IsChecked = _s.ConfirmBeforeCopyPassword;
        ClipClear.Text = _s.ClipboardClearSeconds.ToString();
        AutoLock.Text = _s.AutoLockMinutes.ToString();
        RememberView.IsChecked = _s.RememberView;
        CardWidth.Text = _s.CardWidth.ToString();
        UserAgent.Text = _s.CustomUserAgent;
        WebView2Folder.Text = _s.WebView2Folder;
        ApiEnabled.IsChecked = _s.LocalApiEnabled;
        ApiPort.Text = _s.LocalApiPort.ToString();
        ApiToken.Text = string.IsNullOrWhiteSpace(_s.LocalApiToken)
            ? Services.LocalApiServer.NewToken()
            : _s.LocalApiToken;

        _loadingTheme = true;
        foreach (var t in Services.Theme.Themes) ThemeBox.Items.Add(t.Name);
        ThemeBox.SelectedItem = Services.Theme.Find(_s.Theme).Name;
        FrostSlider.Value = _s.GlassFrost;

        BuildAccentSwatches();
        AccentHex.Text = string.IsNullOrWhiteSpace(_s.AccentColor) ? Services.Theme.DefaultAccent : _s.AccentColor;
        AccentHex.TextChanged += (_, _) => OnAccentChanged();
        UpdateAccentPreview();
        _loadingTheme = false;
    }

    /// <summary>Switching theme previews it live and resets the accent to that theme's own accent.</summary>
    private void ThemeBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTheme || ThemeBox.SelectedItem is not string name) return;
        var preset = Services.Theme.Find(name);
        _loadingTheme = true;
        AccentHex.Text = preset.Accent;   // show the theme's accent (does not re-apply twice)
        _loadingTheme = false;
        UpdateAccentPreview();
        Services.Theme.ApplyTheme(name, preset.Accent);
    }

    /// <summary>Accent edited (typed or swatch) — preview it live on top of the selected theme.</summary>
    private void OnAccentChanged()
    {
        UpdateAccentPreview();
        if (_loadingTheme) return;
        var theme = ThemeBox.SelectedItem as string ?? _origTheme;
        Services.Theme.ApplyTheme(theme, AccentHex.Text);
    }

    /// <summary>Frost slider moved — preview the new Liquid Glass opacity live.</summary>
    private void FrostSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingTheme) return;
        Services.Theme.GlassFrost = (int)FrostSlider.Value;
        Services.Theme.ApplyTheme(ThemeBox.SelectedItem as string ?? _origTheme, AccentHex.Text);
    }

    private void BuildAccentSwatches()
    {
        foreach (var (name, hex) in Services.Theme.AccentPresets)
        {
            if (!Services.Theme.TryParseColor(hex, out var c)) continue;
            var btn = new System.Windows.Controls.Button
            {
                Width = 22, Height = 22, Margin = new Thickness(0, 0, 6, 6),
                Background = new System.Windows.Media.SolidColorBrush(c),
                BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = $"{name} ({hex})", Tag = hex
            };
            btn.Click += (s, _) => AccentHex.Text = (string)((System.Windows.Controls.Button)s!).Tag;
            AccentSwatches.Children.Add(btn);
        }
    }

    private void UpdateAccentPreview()
    {
        if (Services.Theme.TryParseColor(AccentHex.Text, out var c))
            AccentPreview.Background = new System.Windows.Media.SolidColorBrush(c);
    }

    protected override void OnClosed(EventArgs e)
    {
        // Theme previews live while the dialog is open; if the user cancelled, put it back.
        if (DialogResult != true)
        {
            Services.Theme.GlassFrost = _origFrost;
            Services.Theme.ApplyTheme(_origTheme, _origAccent);
        }
        base.OnClosed(e);
    }

    private void CopyToken_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(ApiToken.Text); } catch { /* clipboard busy */ }
    }

    private void RegenToken_Click(object sender, RoutedEventArgs e) =>
        ApiToken.Text = Services.LocalApiServer.NewToken();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var launchOnStartup = LaunchOnStartup.IsChecked == true;
        try
        {
            SetLaunchOnWindowsStartup(launchOnStartup);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            MessageBox.Show(this, "Could not update the Windows startup setting: " + ex.Message,
                "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _s.LaunchOnWindowsStartup = launchOnStartup;
        _s.AlwaysOnTop = AlwaysOnTop.IsChecked == true;
        _s.AutoUpdateEnabled = AutoUpdate.IsChecked == true;
        _s.AutoValidateOnStartup = AutoStartup.IsChecked == true;
        _s.AutoValidateIntervalMinutes = ParseClamp(Interval.Text, 0, 0, 1440);
        _s.AutoRejoin = AutoRejoin.IsChecked == true;
        _s.AutoReloginExpired = ReloginExpired.IsChecked == true;
        _s.OptimizeClient = OptimizeClient.IsChecked == true;
        _s.OptimizeFpsCap = ParseClamp(FpsCap.Text, _s.OptimizeFpsCap, 0, 240);
        _s.ParallelBrowserTasks = ParallelBrowser.IsChecked == true;
        _s.StreamerMode = Streamer.IsChecked == true;
        _s.ConfirmBeforeCopyPassword = ConfirmCopy.IsChecked == true;
        _s.ClipboardClearSeconds = ParseClamp(ClipClear.Text, 20, 0, 600);
        _s.AutoLockMinutes = ParseClamp(AutoLock.Text, 0, 0, 1440);
        _s.RememberView = RememberView.IsChecked == true;
        _s.CardWidth = ParseClamp(CardWidth.Text, 165, 120, 320);
        _s.CustomUserAgent = UserAgent.Text.Trim();
        _s.WebView2Folder = WebView2Folder.Text.Trim();
        Services.WebViewEnvironmentFactory.BrowserExecutableFolder = _s.WebView2Folder; // applies to new browser windows
        _s.LocalApiEnabled = ApiEnabled.IsChecked == true;
        _s.LocalApiPort = ParseClamp(ApiPort.Text, 7963, 1024, 65535);
        _s.LocalApiToken = ApiToken.Text.Trim();
        _s.Theme = ThemeBox.SelectedItem as string ?? Services.Theme.DefaultTheme;
        _s.AccentColor = Services.Theme.TryParseColor(AccentHex.Text, out _)
            ? AccentHex.Text.Trim()
            : Services.Theme.DefaultAccent;
        _s.GlassFrost = (int)FrostSlider.Value;
        _s.Save();
        // Apply the low-resource FFlags immediately so turning the mode off un-caps normal play now,
        // not only on the next launch from the manager.
        AltManager.Core.Launch.ClientOptimizer.SyncFFlags(_s.OptimizeClient, _s.OptimizeFpsCap);
        DialogResult = true;
    }

    private void BrowseWebView2_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select the WebView2 runtime folder" };
        if (!string.IsNullOrWhiteSpace(WebView2Folder.Text) && Directory.Exists(WebView2Folder.Text))
            dlg.InitialDirectory = WebView2Folder.Text;
        if (dlg.ShowDialog(this) == true) WebView2Folder.Text = dlg.FolderName;
    }

    private void GetWebView2_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                Services.WebViewEnvironmentFactory.RuntimeDownloadUrl) { UseShellExecute = true });
        }
        catch { MessageBox.Show(this, "Couldn't open the browser. Go to: " + Services.WebViewEnvironmentFactory.RuntimeDownloadUrl, "WebView2 Runtime"); }
    }

    private static void SetLaunchOnWindowsStartup(bool enabled)
    {
        using var runKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")
            ?? throw new IOException("Could not open the current-user startup registry key.");
        if (!enabled)
        {
            runKey.DeleteValue("RobloxAltManager", throwOnMissingValue: false);
            return;
        }

        var executable = Environment.ProcessPath
            ?? throw new IOException("Could not determine the application executable path.");
        var passwordVault = Environment.GetCommandLineArgs().Skip(1)
            .Contains("--password-vault", StringComparer.OrdinalIgnoreCase);
        var command = $"\"{executable}\"" + (passwordVault ? " --password-vault" : "");
        runKey.SetValue("RobloxAltManager", command);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(AccountVault.DefaultPath)) { MessageBox.Show(this, "No vault file to export yet."); return; }
        var dlg = new SaveFileDialog
        {
            FileName = $"altmanager-vault-{DateTime.Now:yyyy-MM-dd}.json",
            Filter = "Vault backup (*.json)|*.json"
        };
        if (dlg.ShowDialog(this) != true) return;
        try { File.Copy(AccountVault.DefaultPath, dlg.FileName, overwrite: true); MessageBox.Show(this, "Vault exported."); }
        catch (Exception ex) { MessageBox.Show(this, "Export failed: " + ex.Message); }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Vault backup (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        if (MessageBox.Show(this,
                "This replaces your current vault with the chosen backup. You'll need that backup's master password, " +
                "and the app will close so the change takes effect on next launch.\n\nContinue?",
                "Import vault", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AccountVault.DefaultPath)!);
            if (File.Exists(AccountVault.DefaultPath))
                File.Copy(AccountVault.DefaultPath, AccountVault.DefaultPath + ".bak", overwrite: true);
            File.Copy(dlg.FileName, AccountVault.DefaultPath, overwrite: true);
            MessageBox.Show(this, "Vault imported. The app will close now — reopen it to use the imported accounts.");
            Application.Current.Shutdown();
        }
        catch (Exception ex) { MessageBox.Show(this, "Import failed: " + ex.Message); }
    }

    private static int ParseClamp(string text, int fallback, int min, int max) =>
        int.TryParse(text, out var v) ? Math.Clamp(v, min, max) : fallback;
}
