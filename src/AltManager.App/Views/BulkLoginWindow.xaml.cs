using System.Windows;

namespace AltManager.App.Views;

public partial class BulkLoginWindow : Window
{
    public IReadOnlyList<(string User, string Pass)> Credentials { get; private set; } = [];
    public int DelaySeconds => int.TryParse(DelayBox.Text, out var s) ? Math.Clamp(s, 0, 600) : 2;
    public string Group =>string.IsNullOrWhiteSpace(GroupCombo.Text) ? "Ungrouped" : GroupCombo.Text.Trim();

    public BulkLoginWindow(IEnumerable<string> existingGroups)
    {
        InitializeComponent();
        GroupCombo.ItemsSource = existingGroups;
        GroupCombo.Text = "Ungrouped";
        InputBox.TextChanged += (_, _) => CountText.Text = $"{Parse(InputBox.Text).Count} account(s)";
        DataObject.AddPastingHandler(InputBox, OnPaste);
    }

    /// <summary>
    /// Smart paste: pasting a single value at the end alternates username → password.
    /// A username gets ':' appended; pasting after a password inserts ',' first.
    /// Pastes that already contain ':' / ',' / newlines, or that aren't at the end, paste normally.
    /// </summary>
    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (e.SourceDataObject.GetData(DataFormats.UnicodeText) is not string raw) return;
        var value = raw.Trim();
        if (value.Length == 0 || value.IndexOfAny([':', ',', '\n', '\r']) >= 0) return;

        var text = InputBox.Text;
        if (InputBox.CaretIndex != text.Length || InputBox.SelectionLength > 0) return;

        var before = text.TrimEnd();
        string insert;
        if (before.Length == 0 || before.EndsWith(',')) insert = value + ":";   // new username
        else if (before.EndsWith(':')) insert = value;                           // password
        else insert = "," + value + ":";                                         // next username after a password

        e.CancelCommand();
        InputBox.Text = before + insert;
        InputBox.CaretIndex = InputBox.Text.Length;
    }

    /// <summary>"user:pass,user:pass" or one per line. Splits each entry on the first ':' only.</summary>
    public static List<(string User, string Pass)> Parse(string text) =>
        text.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.Split(':', 2))
            .Where(p => p.Length == 2 && p[0].Trim().Length > 0 && p[1].Length > 0)
            .Select(p => (p[0].Trim(), p[1]))
            .DistinctBy(c => c.Item1, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        Credentials = Parse(InputBox.Text);
        if (Credentials.Count == 0)
        {
            MessageBox.Show(this, "No user:pass entries found.", "Bulk import");
            return;
        }
        InputBox.Clear();
        DialogResult = true;
    }
}
