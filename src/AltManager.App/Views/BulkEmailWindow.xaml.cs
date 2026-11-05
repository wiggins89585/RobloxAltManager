using System.Windows;

namespace AltManager.App.Views;

/// <summary>Collects one email per account as "username:email" lines.</summary>
public partial class BulkEmailWindow : Window
{
    public Dictionary<string, string> Emails { get; } = new(StringComparer.OrdinalIgnoreCase);

    public BulkEmailWindow(IEnumerable<string> usernames)
    {
        InitializeComponent();
        Lines.Text = string.Concat(usernames.Select(u => u + ":" + Environment.NewLine));
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        Emails.Clear();
        foreach (var line in Lines.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var i = line.IndexOf(':');
            if (i <= 0 || i == line.Length - 1) continue; // no email on this line
            var email = line[(i + 1)..].Trim();
            if (!System.Net.Mail.MailAddress.TryCreate(email, out _))
            {
                MessageBox.Show(this, $"'{email}' isn't a valid email address.", "Add emails");
                return;
            }
            Emails[line[..i].Trim()] = email;
        }
        DialogResult = true;
    }
}
