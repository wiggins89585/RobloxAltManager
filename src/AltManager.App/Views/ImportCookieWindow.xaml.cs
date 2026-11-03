using System.Windows;
using AltManager.Core.Roblox;

namespace AltManager.App.Views;

public partial class ImportCookieWindow : Window
{
    public IReadOnlyList<string> Cookies { get; private set; } = [];
    public string Group => string.IsNullOrWhiteSpace(GroupCombo.Text) ? "Ungrouped" : GroupCombo.Text.Trim();

    public ImportCookieWindow(IEnumerable<string> existingGroups)
    {
        InitializeComponent();
        GroupCombo.ItemsSource = existingGroups;
        GroupCombo.Text = "Ungrouped";
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        Cookies = CookieBox.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(RobloxCookie.Normalize)
            .OfType<string>()
            .Distinct()
            .ToList();

        if (Cookies.Count == 0)
        {
            MessageBox.Show(this, "No .ROBLOSECURITY values found.", "Import");
            return;
        }
        DialogResult = true;
    }
}
