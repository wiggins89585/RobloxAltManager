using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;

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
    /// Multiline username/password pairs are converted to the same user:pass format.
    /// </summary>
    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (e.SourceDataObject.GetData(DataFormats.UnicodeText) is not string raw) return;
        var value = raw.Trim();
        if (value.Length == 0) return;
        var text = InputBox.Text;
        if (InputBox.CaretIndex != text.Length || InputBox.SelectionLength > 0) return;

        var before = text.TrimEnd();
        if (TryFormatAlternatingPairs(value, out var formatted))
        {
            e.CancelCommand();
            InputBox.Text = before + (before.Length > 0 && !before.EndsWith(',') ? "," : "") + formatted;
            InputBox.CaretIndex = InputBox.Text.Length;
            return;
        }

        if (value.IndexOfAny([':', ',', '\n', '\r']) >= 0) return;
        string insert;
        if (before.Length == 0 || before.EndsWith(',')) insert = value + ":";   // new username
        else if (before.EndsWith(':')) insert = value;                           // password
        else insert = "," + value + ":";                                         // next username after a password

        e.CancelCommand();
        InputBox.Text = before + insert;
        InputBox.CaretIndex = InputBox.Text.Length;
    }

    private static bool TryFormatAlternatingPairs(string text, out string formatted)
    {
        var values = text.Split(['\r', '\n', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length < 2 || values.Length % 2 != 0 ||
            values.Any(value => value.IndexOfAny([':', ',']) >= 0))
        {
            formatted = "";
            return false;
        }

        formatted = string.Join(",", Enumerable.Range(0, values.Length / 2)
            .Select(i => $"{values[i * 2]}:{values[i * 2 + 1]}"));
        return true;
    }

    /// <summary>"user:pass,user:pass" or one per line. Splits each entry on the first ':' only.</summary>
    public static List<(string User, string Pass)> Parse(string text) =>
        text.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.Split(':', 2))
            .Where(p => p.Length == 2 && p[0].Trim().Length > 0 && p[1].Length > 0)
            .Select(p => (p[0].Trim(), p[1]))
            .DistinctBy(c => c.Item1, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Imports a browser password export (CSV from Chrome/Edge/Firefox/Bitwarden, etc.),
    /// keeps only the Roblox logins, and appends them as user:pass. Entries for other sites are skipped.</summary>
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import passwords",
            Filter = "Password export (*.csv)|*.csv|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        string text;
        try { text = File.ReadAllText(dlg.FileName); }
        catch (Exception ex) { MessageBox.Show(this, "Couldn't read the file: " + ex.Message, "Import passwords"); return; }

        var rows = ParseCsv(text);
        if (rows.Count < 2)
        {
            MessageBox.Show(this, "That file has no rows. Export your passwords as CSV from your browser first.", "Import passwords");
            return;
        }

        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToArray();
        int Col(params string[] names) => Array.FindIndex(header, h => names.Contains(h));
        var urlI = Col("url", "uri", "login_uri", "website", "web site", "site", "login url");
        var userI = Col("username", "login_username", "user", "login", "email", "login_email");
        var passI = Col("password", "login_password", "pass", "pwd");
        if (userI < 0 || passI < 0)
        {
            MessageBox.Show(this, "Couldn't find username/password columns in that file. Use a browser password export (CSV from Chrome, Edge, Firefox or a password manager).", "Import passwords");
            return;
        }

        var roblox = new List<string>();
        var otherSites = 0;
        foreach (var r in rows.Skip(1))
        {
            if (userI >= r.Length || passI >= r.Length) continue;
            var user = r[userI].Trim();
            var pass = r[passI];
            if (user.Length == 0 || pass.Length == 0) continue;

            var url = urlI >= 0 && urlI < r.Length ? r[urlI].ToLowerInvariant() : "";
            if (url.Contains("roblox.com")) roblox.Add($"{user}:{pass}");
            else otherSites++;
        }

        if (roblox.Count == 0)
        {
            MessageBox.Show(this, otherSites > 0
                ? $"No Roblox logins in that file (skipped {otherSites} from other sites)."
                : "No logins found in that file.", "Import passwords");
            return;
        }

        var existing = InputBox.Text.TrimEnd();
        InputBox.Text = existing + (existing.Length > 0 && !existing.EndsWith(',') ? "," : "") + string.Join(",", roblox);
        InputBox.CaretIndex = InputBox.Text.Length;
        MessageBox.Show(this,
            $"Added {roblox.Count} Roblox login(s)." + (otherSites > 0 ? $" Skipped {otherSites} from other sites." : ""),
            "Import passwords");
    }

    /// <summary>Minimal RFC-4180 CSV parser: handles quoted fields, embedded commas/newlines and "" escapes.</summary>
    private static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
            }
            else switch (c)
            {
                case '"': inQuotes = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\n': row.Add(field.ToString()); field.Clear(); rows.Add(row.ToArray()); row = new(); break;
                case '\r': break; // part of \r\n
                default: field.Append(c); break;
            }
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }

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
