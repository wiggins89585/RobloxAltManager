using System.Windows;
using AltManager.App.ViewModels;
using AltManager.Core.Roblox;

namespace AltManager.App.Views;

/// <summary>
/// One-time group payout: the payer account (which must own/control the group and its funds)
/// pays a fixed Robux amount to each selected recipient account.
/// </summary>
public partial class GroupPayoutWindow : Window
{
    private readonly IReadOnlyList<AccountViewModel> _recipients;
    private readonly RobloxApiClient _api;
    private readonly Func<AccountViewModel, string?> _getCookie;

    public GroupPayoutWindow(IReadOnlyList<AccountViewModel> allAccounts, IReadOnlyList<AccountViewModel> recipients,
                             RobloxApiClient api, Func<AccountViewModel, string?> getCookie)
    {
        _recipients = recipients;
        _api = api;
        _getCookie = getCookie;
        InitializeComponent();
        PayerBox.ItemsSource = allAccounts;
        PayerBox.SelectedItem = allAccounts.FirstOrDefault();
        Recipients.ItemsSource = recipients;
        UpdateTotal();
    }

    private int Amount => int.TryParse(AmountBox.Text.Trim(), out var a) && a > 0 ? a : 0;

    private void Amount_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateTotal();

    private void UpdateTotal() =>
        TotalText.Text = $"Total: {(long)Amount * _recipients.Count:N0} Robux  →  {_recipients.Count} account(s)";

    private async void CheckFunds_Click(object sender, RoutedEventArgs e)
    {
        if (PayerBox.SelectedItem is not AccountViewModel payer) { FundsText.Text = "Pick a payer account."; return; }
        if (!long.TryParse(GroupBox.Text.Trim(), out var groupId) || groupId <= 0) { FundsText.Text = "Enter a group ID."; return; }
        if (_getCookie(payer) is not { } cookie) { FundsText.Text = "That payer has no usable cookie."; return; }

        FundsText.Text = "Checking…";
        var funds = await _api.GetGroupFundsAsync(groupId, cookie);
        FundsText.Text = funds is { } f ? $"Group funds: {f:N0} Robux" : "Could not read group funds (is this account the owner?).";
    }

    private async void Pay_Click(object sender, RoutedEventArgs e)
    {
        if (PayerBox.SelectedItem is not AccountViewModel payer) { MessageBox.Show(this, "Pick a payer account."); return; }
        if (!long.TryParse(GroupBox.Text.Trim(), out var groupId) || groupId <= 0) { MessageBox.Show(this, "Enter a valid group ID."); return; }
        if (Amount <= 0) { MessageBox.Show(this, "Enter a Robux amount greater than 0."); return; }
        if (_getCookie(payer) is not { } cookie) { MessageBox.Show(this, "That payer has no usable cookie."); return; }

        var total = (long)Amount * _recipients.Count;
        if (MessageBox.Show(this,
                $"Pay {Amount:N0} Robux to each of {_recipients.Count} account(s) — {total:N0} Robux total — from group {groupId}?\n\n" +
                "This spends real group funds and cannot be undone.",
                "Confirm payout", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        PayButton.IsEnabled = false;
        var recipients = _recipients.Where(r => r.UserId > 0).Select(r => (r.UserId, (long)Amount)).ToList();
        var (ok, msg) = await _api.GroupPayoutAsync(groupId, cookie, recipients);
        PayButton.IsEnabled = true;

        MessageBox.Show(this,
            ok ? $"Paid {Amount:N0} Robux to {recipients.Count} account(s)." : $"Payout failed: {msg}",
            "Group payout", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        if (ok) Close();
    }
}
