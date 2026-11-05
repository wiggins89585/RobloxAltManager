using System.Windows;
using System.Windows.Controls;
using AltManager.Core.Mail;
using AltManager.Core.Vault;

namespace AltManager.App.Views;

/// <summary>Adds and removes the IMAP inboxes used to read Roblox email codes.</summary>
public partial class InboxesWindow : Window
{
    private sealed record Preset(string Name, string Host, int Port, string PasswordLabel, string Hint);

    private static readonly Preset[] Presets =
    {
        new("Gmail", "imap.gmail.com", 993, "App password",
            "Turn on 2-Step Verification, then create an app password in your Google account."),
        new("Proton (needs Bridge)", "127.0.0.1", 1143, "Bridge password",
            "Needs Proton Mail Bridge running (paid Proton plan). Copy the address and password Bridge shows."),
        new("Other IMAP", "", 993, "Password", "Enter your provider's IMAP server. Outlook isn't supported yet."),
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
        ServerRow.Visibility = p.Host.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var address = Address.Text.Trim();
        if (address.Length == 0 || Password.Password.Length == 0 || Host.Text.Trim().Length == 0 ||
            !int.TryParse(Port.Text, out var port))
        {
            Status.Text = "Fill in every field.";
            return;
        }

        var inbox = new MailInbox { Address = address, Host = Host.Text.Trim(), Port = port };
        AddButton.IsEnabled = false;
        Status.Text = "Connecting…";
        try
        {
            await InboxReader.TestAsync(inbox, Password.Password);
            _vault.AddInbox(inbox, Password.Password);
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
