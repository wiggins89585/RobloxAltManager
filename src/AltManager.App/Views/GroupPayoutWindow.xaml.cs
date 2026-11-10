using System.Windows;
using AltManager.App.ViewModels;
using AltManager.Core.Roblox;

namespace AltManager.App.Views;

/// <summary>
/// One-time group payout: the payer account (which must own/control the group and its funds)
/// pays a fixed Robux amount to each selected recipient account.
///
/// Recipients are paid one request each, so one account Roblox won't accept (not in the group, or
/// joined too recently) no longer fails the whole batch, and the result says exactly who was paid.
/// </summary>
public partial class GroupPayoutWindow : Window
{
    private readonly IReadOnlyList<AccountViewModel> _recipients;
    private readonly RobloxApiClient _api;
    private readonly Func<AccountViewModel, string?> _getCookie;
    private readonly Func<AccountViewModel, string?>? _getTotp;

    public GroupPayoutWindow(IReadOnlyList<AccountViewModel> allAccounts, IReadOnlyList<AccountViewModel> recipients,
                             RobloxApiClient api, Func<AccountViewModel, string?> getCookie,
                             Func<AccountViewModel, string?>? getTotp = null)
    {
        _recipients = recipients;
        _api = api;
        _getCookie = getCookie;
        _getTotp = getTotp;
        InitializeComponent();
        PayerBox.ItemsSource = allAccounts;
        // Default to an account that isn't one of the recipients, if there is one: it's usually the owner.
        PayerBox.SelectedItem = allAccounts.FirstOrDefault(a => !recipients.Contains(a)) ?? allAccounts.FirstOrDefault();
        Recipients.ItemsSource = recipients;
        UpdateTotal();
    }

    private int Amount => int.TryParse(AmountBox.Text.Trim(), out var a) && a > 0 ? a : 0;

    private void Amount_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateTotal();

    private void UpdateTotal()
    {
        if (TotalText is null) return; // fires during InitializeComponent
        TotalText.Text = $"Total: {(long)Amount * _recipients.Count:N0} Robux  →  {_recipients.Count} account(s)";
    }

    private async void CheckFunds_Click(object sender, RoutedEventArgs e)
    {
        if (PayerBox.SelectedItem is not AccountViewModel payer) { FundsText.Text = "Pick a payer account."; return; }
        if (!long.TryParse(GroupBox.Text.Trim(), out var groupId) || groupId <= 0) { FundsText.Text = "Enter a group ID."; return; }
        if (_getCookie(payer) is not { } cookie) { FundsText.Text = "That payer has no usable cookie."; return; }

        FundsText.Text = "Checking…";
        var funds = await _api.GetGroupFundsAsync(groupId, cookie);
        FundsText.Text = funds is { } f
            ? $"Group funds: {f:N0} Robux"
            : "Could not read group funds — the payer needs permission to spend this group's funds.";
    }

    private async void Pay_Click(object sender, RoutedEventArgs e)
    {
        if (PayerBox.SelectedItem is not AccountViewModel payer) { MessageBox.Show(this, "Pick a payer account."); return; }
        if (!long.TryParse(GroupBox.Text.Trim(), out var groupId) || groupId <= 0) { MessageBox.Show(this, "Enter a valid group ID."); return; }
        if (Amount <= 0) { MessageBox.Show(this, "Enter a Robux amount greater than 0."); return; }
        if (_getCookie(payer) is not { } cookie) { MessageBox.Show(this, "That payer has no usable cookie."); return; }

        PayButton.IsEnabled = false;
        var report = new List<string>();
        try
        {
            var everyone = _recipients.Where(r => r.UserId > 0).ToList();

            // 1. Ask Roblox who can actually be paid, so the confirmation shows real numbers.
            Progress("Checking who can be paid…");
            var eligibility = await _api.GetPayoutEligibilityAsync(groupId, cookie, everyone.Select(r => r.UserId).ToList());
            var payable = new List<AccountViewModel>();
            foreach (var r in everyone)
            {
                if (eligibility is not null && eligibility.TryGetValue(r.UserId, out var state) &&
                    !state.Equals("Eligible", StringComparison.OrdinalIgnoreCase))
                    report.Add($"✗ {r.Label} — {Explain(state)}");
                else
                    payable.Add(r);
            }
            if (payable.Count == 0)
            {
                ShowReport(report, "Nobody could be paid.");
                return;
            }

            // 2. Make sure the group can cover it.
            var total = (long)Amount * payable.Count;
            var funds = await _api.GetGroupFundsAsync(groupId, cookie);
            if (funds is { } f)
            {
                FundsText.Text = $"Group funds: {f:N0} Robux";
                if (f < total)
                {
                    ShowReport(report, $"Not enough group funds: {f:N0} Robux available, {total:N0} needed.");
                    return;
                }
            }

            // 3. Confirm with the real numbers.
            var skipped = everyone.Count - payable.Count;
            if (MessageBox.Show(this,
                    $"Pay {Amount:N0} Robux to each of {payable.Count} account(s) — {total:N0} Robux total — from group {groupId}?" +
                    (skipped > 0 ? $"\n\n{skipped} account(s) can't be paid and will be skipped." : "") +
                    "\n\nThis spends real group funds and cannot be undone.",
                    "Confirm payout", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                Progress("");
                return;
            }

            // 4. Pay one at a time.
            var totp = _getTotp?.Invoke(payer);
            var paid = 0;
            for (var i = 0; i < payable.Count; i++)
            {
                var r = payable[i];
                Progress($"Paying {i + 1}/{payable.Count}: {r.Label}…");
                var (ok, msg) = await _api.GroupPayoutAsync(groupId, cookie, new[] { (r.UserId, (long)Amount) }, totp);
                if (ok)
                {
                    paid++;
                    report.Add($"✓ {r.Label} — paid {Amount:N0}");
                }
                else
                {
                    report.Add($"✗ {r.Label} — {msg}");
                    if (StopsEverything(msg))
                    {
                        var left = payable.Count - i - 1;
                        if (left > 0) report.Add($"Stopped — the remaining {left} account(s) were not attempted.");
                        break;
                    }
                }
                if (i < payable.Count - 1) await Task.Delay(700); // stay under Roblox's rate limit
            }

            ShowReport(report, $"Paid {paid} of {payable.Count} account(s) — {(long)Amount * paid:N0} Robux.");
        }
        finally
        {
            PayButton.IsEnabled = true;
        }
    }

    private void Progress(string text) => TotalText.Text = text.Length > 0 ? text
        : $"Total: {(long)Amount * _recipients.Count:N0} Robux  →  {_recipients.Count} account(s)";

    private void ShowReport(List<string> lines, string summary)
    {
        TotalText.Text = summary;
        ResultBox.Text = string.Join(Environment.NewLine, lines);
        ResultBox.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Explain(string state) => state switch
    {
        "NotInGroup" => "not a member of the group",
        "PayoutRestricted" => "joined the group too recently (Roblox makes new members wait before payouts)",
        _ => state
    };

    /// <summary>Failures that would hit every remaining recipient too, so there's no point continuing.</summary>
    private static bool StopsEverything(string message) =>
        message.Contains("2-step", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("verification", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("security check", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("funds", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("authorized", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Too many requests", StringComparison.OrdinalIgnoreCase);
}
