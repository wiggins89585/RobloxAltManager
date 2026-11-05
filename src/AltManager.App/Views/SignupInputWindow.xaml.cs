using System.Windows;
using AltManager.App.Services;

namespace AltManager.App.Views;

/// <summary>Collects the details for one or more new Roblox accounts so the browser window can fill
/// them in, leaving only the CAPTCHA for the user.</summary>
public partial class SignupInputWindow : Window
{
    private static readonly string[] Months =
        { "January", "February", "March", "April", "May", "June",
          "July", "August", "September", "October", "November", "December" };

    /// <summary>The entered details, or null if cancelled.</summary>
    public SignupInput? Result { get; private set; }

    /// <summary>How many accounts to create.</summary>
    public int Count { get; private set; } = 1;

    public SignupInputWindow()
    {
        InitializeComponent();

        foreach (var m in Months) MonthBox.Items.Add(m);
        for (var d = 1; d <= 31; d++) DayBox.Items.Add(d);
        var thisYear = DateTime.Now.Year;
        for (var y = thisYear - 13; y >= thisYear - 90; y--) YearBox.Items.Add(y);
        foreach (var g in new[] { "Female", "Male" }) GenderBox.Items.Add(g);

        // A plausible adult default so the account isn't put into Roblox's under-13 experience.
        MonthBox.SelectedIndex = 0;
        DayBox.SelectedIndex = 0;
        YearBox.SelectedItem = thisYear - 20;
        GenderBox.SelectedIndex = 0;
        UsernameBox.Text = RandomCredentials.Username();
        PasswordBox.Text = RandomCredentials.Password();

        CountBox.TextChanged += (_, _) => BulkHint.Visibility =
            int.TryParse(CountBox.Text, out var n) && n > 1 ? Visibility.Visible : Visibility.Collapsed;

        Loaded += (_, _) => { UsernameBox.Focus(); UsernameBox.SelectAll(); };
    }

    private void RandomUser_Click(object sender, RoutedEventArgs e) => UsernameBox.Text = RandomCredentials.Username();
    private void RandomPass_Click(object sender, RoutedEventArgs e) => PasswordBox.Text = RandomCredentials.Password();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var user = UsernameBox.Text.Trim();
        if (user.Length < 3)
        {
            Warn("Enter a username of at least 3 characters.");
            return;
        }
        if (PasswordBox.Text.Length < 8)
        {
            Warn("Enter a password of at least 8 characters.");
            return;
        }
        if (!int.TryParse(CountBox.Text, out var count) || count < 1 || count > 50)
        {
            Warn("Enter how many accounts to create (1–50).");
            return;
        }

        Count = count;
        Result = new SignupInput(user, PasswordBox.Text, MonthBox.SelectedIndex + 1,
            (int)DayBox.SelectedItem, (int)YearBox.SelectedItem, (string)GenderBox.SelectedItem);
        DialogResult = true;
    }

    private void Warn(string message) =>
        MessageBox.Show(this, message, "Create account", MessageBoxButton.OK, MessageBoxImage.Warning);
}

/// <param name="Month">1–12.</param>
public readonly record struct SignupInput(
    string Username, string Password, int Month, int Day, int Year, string Gender);
