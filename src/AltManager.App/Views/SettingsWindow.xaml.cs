using System.IO;
using System.Security;
using System.Windows;
using AltManager.Core;
using AltManager.Core.Vault;
using Microsoft.Win32;

namespace AltManager.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _s;

    public SettingsWindow(AppSettings settings)
    {
        _s = settings;
        InitializeComponent();
        LaunchOnStartup.IsChecked = _s.LaunchOnWindowsStartup;
        AlwaysOnTop.IsChecked = _s.AlwaysOnTop;
        AutoUpdate.IsChecked = _s.AutoUpdateEnabled;
        AutoStartup.IsChecked = _s.AutoValidateOnStartup;
        Interval.Text = _s.AutoValidateIntervalMinutes.ToString();
        Streamer.IsChecked = _s.StreamerMode;
        ConfirmCopy.IsChecked = _s.ConfirmBeforeCopyPassword;
        ClipClear.Text = _s.ClipboardClearSeconds.ToString();
        RememberView.IsChecked = _s.RememberView;
        CardWidth.Text = _s.CardWidth.ToString();
        UserAgent.Text = _s.CustomUserAgent;
    }

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
        _s.StreamerMode = Streamer.IsChecked == true;
        _s.ConfirmBeforeCopyPassword = ConfirmCopy.IsChecked == true;
        _s.ClipboardClearSeconds = ParseClamp(ClipClear.Text, 20, 0, 600);
        _s.RememberView = RememberView.IsChecked == true;
        _s.CardWidth = ParseClamp(CardWidth.Text, 165, 120, 320);
        _s.CustomUserAgent = UserAgent.Text.Trim();
        _s.Save();
        DialogResult = true;
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
