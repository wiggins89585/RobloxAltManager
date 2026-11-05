using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using AltManager.Core.Mail;
using AltManager.Core.Vault;

namespace AltManager.App.Views;

/// <summary>Adds and removes the IMAP inboxes used to read Roblox email codes.</summary>
public partial class InboxesWindow : Window
{
    private sealed record Preset(string Name, string Host, int Port, string PasswordLabel, string Hint);

    private static readonly Preset[] Presets =
    {
        new("Gmail (Sign in with Google)", GoogleInbox.Host, 0, "Client secret",
            "Enter the Desktop OAuth client from your Google Cloud project, then sign in with your browser."),
        new("Gmail (app password)", "imap.gmail.com", 993, "App password",
            "Use a 16-letter app password from myaccount.google.com/apppasswords, not your normal password."),
        new("Proton (needs Bridge)", "127.0.0.1", 1143, "Bridge password",
            "Needs Proton Mail Bridge running (paid Proton plan). Copy the address and password Bridge shows."),
        new("Other IMAP", "", 993, "Password", "Enter your provider's IMAP server. Outlook isn't supported yet."),
    };

    /// <summary>One setup step, with an optional page to open.</summary>
    private sealed record Step(string Text, string? Link = null);

    private static readonly Step[] GoogleSteps =
    {
        new("Create a new project called Alt Manager.", "https://console.cloud.google.com"),
        new("Open the Gmail API page and click Enable.", "https://console.cloud.google.com/apis/library/gmail.googleapis.com"),
        new("Open the sign-in setup page, make sure Alt Manager is the project selected at the top, and click Get started. Choose External and fill in the name and your email.", "https://console.cloud.google.com/auth/overview"),
        new("On the Audience page, under Test users, add every Gmail account you want to connect.", "https://console.cloud.google.com/auth/audience"),
        new("On the Clients page, click Create client, choose Desktop app, then copy the Client ID and Client secret.", "https://console.cloud.google.com/auth/clients"),
        new("Back here, paste both and click Sign in with Google."),
        new("In your browser, sign in. At the \"Google hasn't verified this app\" warning, click Advanced, then Go to Alt Manager."),
        new("Repeat from the previous two steps for each Gmail account, reusing the same Client ID and secret."),
        new("While the project is in Testing mode, the sign-in expires after about 7 days. Remove the inbox and sign in again."),
    };

    private static readonly Step[][] Steps =
    {
        GoogleSteps,
        new[]
        {
            new Step("Make sure 2-Step Verification is on for the account.", "https://myaccount.google.com/signinoptions/two-step-verification"),
            new Step("Create an app password named Alt Manager and copy the 16 letters.", "https://myaccount.google.com/apppasswords"),
            new Step("Back here, enter the Gmail address, paste the app password (not your normal password) and click Test and add."),
        },
        new[]
        {
            new Step("Install Proton Mail Bridge and sign in. It needs a paid Proton plan.", "https://proton.me/mail/bridge"),
            new Step("Keep Bridge running, and copy the address and password it shows for your account."),
            new Step("Back here, paste them and click Test and add."),
        },
        new[]
        {
            new Step("Find your provider's IMAP server and port, and create an app password if it needs one."),
            new Step("Back here, fill everything in and click Test and add."),
        },
    };

    private readonly AccountVault _vault;

    public InboxesWindow(AccountVault vault)
    {
        _vault = vault;
        InitializeComponent();
        foreach (var p in Presets) Provider.Items.Add(p.Name);
        Provider.SelectedIndex = 0;
        Reload();
    }

    private void Reload()
    {
        List.ItemsSource = null;
        List.ItemsSource = _vault.Inboxes;
    }

    private void Provider_Changed(object sender, SelectionChangedEventArgs e)
    {
        var p = Presets[Provider.SelectedIndex];
        Host.Text = p.Host;
        Port.Text = p.Port.ToString();
        Hint.Text = p.Hint;
        PasswordLabel.Text = p.PasswordLabel;
        var google = p.Host == GoogleInbox.Host;
        AddressLabel.Text = google ? "Client ID" : "Email address";
        AddButton.Content = google ? "Sign in with Google" : "Test and add";
        ServerRow.Visibility = p.Host.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        var steps = Steps[Provider.SelectedIndex];
        for (var i = 0; i < steps.Length; i++)
        {
            var step = steps[i];
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
            text.Inlines.Add($"{i + 1}. {step.Text}");
            if (step.Link is { } link)
            {
                var open = new Hyperlink(new Run(" Open page")) { NavigateUri = new Uri(link) };
                open.SetResourceReference(TextElement.ForegroundProperty, "Accent");
                open.RequestNavigate += (_, a) =>
                    Process.Start(new ProcessStartInfo(a.Uri.AbsoluteUri) { UseShellExecute = true });
                text.Inlines.Add(open);
            }
            panel.Children.Add(text);
        }

        var help = new Window
        {
            Title = $"How to add an email: {Presets[Provider.SelectedIndex].Name}",
            Owner = this, Width = 480, MaxHeight = 620, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };
        help.SetResourceReference(BackgroundProperty, "Bg");
        help.SetResourceReference(ForegroundProperty, "Fg");
        help.Show();
    }

    private async Task SignInWithGoogleAsync()
    {
        var clientId = Address.Text.Trim();
        var secret = Password.Password.Trim();
        if (clientId.Length == 0 || secret.Length == 0) { Status.Text = "Enter the Client ID and Client secret."; return; }

        AddButton.IsEnabled = false;
        Status.Text = "Finish signing in with your browser…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var (email, refresh) = await GoogleInbox.SignInAsync(clientId, secret,
                uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }), timeout.Token);
            _vault.AddInbox(new MailInbox { Address = email, Host = GoogleInbox.Host, ClientId = clientId },
                            GoogleInbox.PackSecret(secret, refresh));
            await _vault.SaveAsync();
            Reload();
            Status.Text = $"Connected {email}.";
        }
        catch (OperationCanceledException) { Status.Text = "Sign-in timed out."; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status.Text = ex.Message; }
        finally { AddButton.IsEnabled = true; }
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (Host.Text == GoogleInbox.Host) { await SignInWithGoogleAsync(); return; }

        var address = Address.Text.Trim();
        if (address.Length == 0 || Password.Password.Length == 0 || Host.Text.Trim().Length == 0 ||
            !int.TryParse(Port.Text, out var port))
        {
            Status.Text = "Fill in every field.";
            return;
        }

        var inbox = new MailInbox { Address = address, Host = Host.Text.Trim(), Port = port };
        // Google displays app passwords in groups of four; the spaces aren't part of it.
        var gmail = inbox.Host == "imap.gmail.com";
        var password = gmail ? string.Concat(Password.Password.Where(c => !char.IsWhiteSpace(c))) : Password.Password;
        if (gmail && password.Length != 16)
        {
            Status.Text = $"That's {password.Length} characters. A Gmail app password is exactly 16, so this looks like a normal password.";
            return;
        }
        AddButton.IsEnabled = false;
        Status.Text = "Connecting…";
        try
        {
            await InboxReader.TestAsync(inbox, password);
            _vault.AddInbox(inbox, password);
            await _vault.SaveAsync();
            Password.Clear();
            Address.Clear();
            Reload();
            Status.Text = $"Connected {inbox.Address}.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status.Text = "Couldn't connect: " + ex.Message; }
        finally { AddButton.IsEnabled = true; }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not MailInbox inbox) return;
        _vault.RemoveInbox(inbox);
        await _vault.SaveAsync();
        Reload();
    }
}
