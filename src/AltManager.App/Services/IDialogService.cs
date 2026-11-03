using System.Windows;
using System.Windows.Controls;
using AltManager.App.Views;
using AltManager.Core;

namespace AltManager.App.Services;

/// <summary>Keeps windows out of the view-models.</summary>
public interface IDialogService
{
    (IReadOnlyList<string> Cookies, string Group)? ImportCookies(IEnumerable<string> groups);
    string? BrowserLogin(string? customUserAgent);
    (IReadOnlyList<(string User, string Pass)> Credentials, string Group, int DelaySeconds)? BulkLoginInput(IEnumerable<string> groups);
    /// <summary>Bulk browser login: fills + submits automatically; flags NeedsManual on a verification challenge.</summary>
    BrowserLoginResult BulkBrowserLogin(string? customUserAgent, string username, string password, string progress);
    /// <summary>Manual browser login: fields pre-filled but the user clicks Log In and enters any code.</summary>
    string? BrowserLoginManual(string? customUserAgent, string username, string password, string progress);
    bool Confirm(string message);
    void Error(string message);
    /// <summary>Single-line text prompt. Returns the entered text, or null if cancelled.</summary>
    string? Prompt(string title, string label, string initial = "");
    /// <summary>Opens the settings window; returns true if the user saved.</summary>
    bool EditSettings(AppSettings settings);
    /// <summary>Opens roblox.com in a browser window signed in as this account (non-modal).</summary>
    void OpenRobloxBrowser(string cookie, string profileKey, string label);
}

/// <param name="Cookie">Captured cookie, or null.</param>
/// <param name="NeedsManual">Auto-login hit a verification challenge; queue for manual sign-in.</param>
public readonly record struct BrowserLoginResult(string? Cookie, bool NeedsManual);

public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current.MainWindow;

    public (IReadOnlyList<string> Cookies, string Group)? ImportCookies(IEnumerable<string> groups)
    {
        var w = new ImportCookieWindow(groups) { Owner = Owner };
        return w.ShowDialog() == true ? (w.Cookies, w.Group) : null;
    }

    public string? BrowserLogin(string? customUserAgent)
    {
        var w = new WebLoginWindow(customUserAgent) { Owner = Owner };
        return w.ShowDialog() == true ? w.CapturedCookie : null;
    }

    public (IReadOnlyList<(string User, string Pass)> Credentials, string Group, int DelaySeconds)? BulkLoginInput(IEnumerable<string> groups)
    {
        var w = new BulkLoginWindow(groups) { Owner = Owner };
        return w.ShowDialog() == true ? (w.Credentials, w.Group, w.DelaySeconds) : null;
    }

    public BrowserLoginResult BulkBrowserLogin(string? customUserAgent, string username, string password, string progress)
    {
        var w = new WebLoginWindow(customUserAgent, (username, password), progress) { Owner = Owner };
        w.ShowDialog();
        return new BrowserLoginResult(w.CapturedCookie, w.NeedsManual);
    }

    public string? BrowserLoginManual(string? customUserAgent, string username, string password, string progress)
    {
        var w = new WebLoginWindow(customUserAgent, (username, password), progress, autoSubmit: false, abortOnChallenge: false)
        { Owner = Owner };
        return w.ShowDialog() == true ? w.CapturedCookie : null;
    }

    public bool Confirm(string message) =>
        MessageBox.Show(Owner!, message, "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void Error(string message) =>
        MessageBox.Show(Owner!, message, "Alt Manager", MessageBoxButton.OK, MessageBoxImage.Warning);

    public string? Prompt(string title, string label, string initial = "")
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 8), MinWidth = 300,
                                Background = Brush("#1E1F22"), Foreground = Brush("#DBDEE1"),
                                CaretBrush = Brush("#DBDEE1"), BorderBrush = Brush("#3F4147"), Padding = new Thickness(4, 3, 4, 3) };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80, HorizontalAlignment = HorizontalAlignment.Right,
                              Background = Brush("#5865F2"), Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(0, 4, 0, 4) };
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = label, Foreground = Brush("#DBDEE1"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(ok);
        var w = new Window
        {
            Title = title, Content = panel, SizeToContent = SizeToContent.WidthAndHeight, Background = Brush("#2B2D31"),
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Owner
        };
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) => { box.SelectAll(); box.Focus(); };
        return w.ShowDialog() == true ? box.Text.Trim() : null;
    }

    public bool EditSettings(AppSettings settings)
    {
        var w = new SettingsWindow(settings) { Owner = Owner };
        return w.ShowDialog() == true;
    }

    public void OpenRobloxBrowser(string cookie, string profileKey, string label) =>
        new RobloxBrowserWindow(cookie, profileKey, label).Show(); // non-modal: keep using the app

    private static System.Windows.Media.Brush Brush(string hex) =>
        (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(hex)!;
}
