using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using AltManager.App.Services;

namespace AltManager.App.Views;

/// <summary>Shows the usernames and passwords that a bulk create will use, and lets the user edit or
/// regenerate them before any account is created.</summary>
public partial class SignupPreviewWindow : Window
{
    private readonly SignupInput _shared; // birthday/gender are the same for every account
    private readonly ObservableCollection<Row> _rows = new();

    /// <summary>The final list, built from the (possibly edited) rows. Null until the user clicks Create.</summary>
    public IReadOnlyList<SignupInput>? Accounts { get; private set; }

    public SignupPreviewWindow(IReadOnlyList<SignupInput> plan)
    {
        _shared = plan[0];
        InitializeComponent();
        foreach (var a in plan) _rows.Add(new Row(_rows.Count + 1, a.Username, a.Password));
        Rows.ItemsSource = _rows;
        CreateButton.Content = $"Create {plan.Count}";
    }

    private void Regenerate_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows)
        {
            r.Username = RandomCredentials.Username();
            r.Password = RandomCredentials.Password();
        }
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows)
        {
            if (r.Username.Trim().Length < 3) { Warn($"Account {r.Index}: username must be at least 3 characters."); return; }
            if (r.Password.Length < 8) { Warn($"Account {r.Index}: password must be at least 8 characters."); return; }
        }

        Accounts = _rows.Select(r => _shared with { Username = r.Username.Trim(), Password = r.Password }).ToList();
        DialogResult = true;
    }

    private void Warn(string message) =>
        MessageBox.Show(this, message, "Create accounts", MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>One editable row; notifies so Regenerate updates the text boxes live.</summary>
    private sealed class Row : INotifyPropertyChanged
    {
        public int Index { get; }
        private string _username, _password;

        public Row(int index, string username, string password)
        {
            Index = index;
            _username = username;
            _password = password;
        }

        public string Username { get => _username; set { _username = value; Changed(nameof(Username)); } }
        public string Password { get => _password; set { _password = value; Changed(nameof(Password)); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
