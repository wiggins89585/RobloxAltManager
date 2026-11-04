using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using AltManager.App.Services;
using AltManager.Core;
using AltManager.Core.Launch;
using AltManager.Core.Models;
using AltManager.Core.Roblox;
using AltManager.Core.Vault;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AltManager.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    public const string AllGroups = "All accounts";

    private readonly AccountVault _vault;
    private readonly RobloxApiClient _api;
    private readonly ProcessTracker _tracker;
    private readonly MultiInstanceGuard _guard;
    private readonly LaunchEngine _engine;
    private readonly IDialogService _dialogs;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _autoValidate = new();
    private DispatcherTimer? _clipboardClear;
    private CancellationTokenSource? _launchCts;

    public ObservableCollection<string> Presets { get; } = new();
    /// <summary>Status-column filter: "All", "Valid", "Expired", "Banned", "Unchecked".</summary>
    public string[] StatusFilters { get; } = { "All", "Valid", "Expired", "Banned", "Unchecked" };

    public ObservableCollection<AccountViewModel> Accounts { get; } = new();
    public ObservableCollection<string> Groups { get; } = new();
    public ObservableCollection<TrackedProcess> ActiveProcesses { get; } = new();
    public ICollectionView AccountsView { get; }

    [ObservableProperty] private string _selectedGroup = AllGroups;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _placeInput = "";
    [ObservableProperty] private string _jobId = "";
    [ObservableProperty] private string _privateServerAccessCode = "";
    [ObservableProperty] private string _privateServerLinkCode = "";
    [ObservableProperty] private double _staggerSeconds = 5;
    [ObservableProperty] private int _gridColumns; // 0 = auto
    [ObservableProperty] private string _customUserAgent = "";
    [ObservableProperty] private string _statusFilter = "All";
    [ObservableProperty] private string? _selectedPreset;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusMessage = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchSelectedCommand), nameof(CancelLaunchCommand))]
    private bool _isLaunching;

    public bool MultiInstanceActive => _guard.IsActive;
    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    public MainViewModel(AccountVault vault, RobloxApiClient api, ProcessTracker tracker,
                         MultiInstanceGuard guard, IDialogService dialogs, AppSettings settings)
    {
        _vault = vault;
        _api = api;
        _tracker = tracker;
        _guard = guard;
        _dialogs = dialogs;
        _settings = settings;
        _engine = new LaunchEngine(vault, api, tracker, guard);

        CustomUserAgent = settings.CustomUserAgent;
        IsGridView = settings.RememberView && settings.IsGridView;
        CardColumns = settings.CardColumns;
        ShowGroups = !settings.RememberView || settings.ShowGroups;
        ShowLaunch = !settings.RememberView || settings.ShowLaunch;
        AccountViewModel.Masked = settings.StreamerMode;

        foreach (var a in vault.Accounts) Add(a);
        foreach (var p in settings.Presets) Presets.Add(p.Name);

        AccountsView = CollectionViewSource.GetDefaultView(Accounts);
        AccountsView.Filter = o => o is AccountViewModel vm && MatchesFilter(vm);
        AccountsView.SortDescriptions.Add(new SortDescription(nameof(AccountViewModel.Favorite), ListSortDirection.Descending));
        AccountsView.SortDescriptions.Add(new SortDescription(nameof(AccountViewModel.Group), ListSortDirection.Ascending));
        AccountsView.SortDescriptions.Add(new SortDescription(nameof(AccountViewModel.Label), ListSortDirection.Ascending));
        RebuildGroups(); // after AccountsView exists: it may change SelectedGroup → Refresh()
        if (settings.RememberView && Groups.Contains(settings.LastGroup)) SelectedGroup = settings.LastGroup;

        _autoValidate.Tick += async (_, _) => await ValidateAllAsync();
        ConfigureAutoValidate();

        _api.CookieRotated += OnCookieRotated;
        _tracker.ProcessStarted += (_, p) => OnUi(() => { ActiveProcesses.Add(p); SetPid(p.AccountId, p.Pid); _ = RestoreWindowBoundsAsync(p); });
        _tracker.ProcessExited += (_, p) => OnUi(() =>
        {
            var row = ActiveProcesses.FirstOrDefault(x => x.Pid == p.Pid);
            if (row is not null) ActiveProcesses.Remove(row);
            SetPid(p.AccountId, null);
            PersistWindowBounds(p.AccountId);
            if (_userClosed.Remove(p.AccountId)) { /* closed on purpose — don't rejoin */ }
            else MaybeRejoin(p.AccountId);
        });
        _boundsPoll.Interval = TimeSpan.FromSeconds(3);
        _boundsPoll.Tick += (_, _) =>
        {
            foreach (var p in ActiveProcesses)
                if (GameWindowBounds.Capture(p.MainWindow) is { } b) _liveBounds[p.AccountId] = b;
        };
        _boundsPoll.Start();

        _presencePoll.Interval = TimeSpan.FromSeconds(45);
        _presencePoll.Tick += async (_, _) => await RefreshPresencesAsync();
        _presencePoll.Start();
    }

    // ---------------- Presence (online / in-game dots) ----------------

    private readonly DispatcherTimer _presencePoll = new();

    /// <summary>Refreshes every account's online/in-game status via one authenticated call.</summary>
    public async Task RefreshPresencesAsync()
    {
        string? cookie = null;
        foreach (var a in Accounts) { try { cookie = _vault.GetCookie(a.Model); break; } catch { /* skip */ } }
        if (cookie is null) return;

        var ids = Accounts.Select(a => a.UserId).Where(id => id > 0).ToList();
        if (ids.Count == 0) return;

        var map = await _api.GetPresencesAsync(ids, cookie);
        foreach (var vm in Accounts)
            if (map.TryGetValue(vm.UserId, out var p))
            {
                vm.LastLocation = p.LastLocation;
                vm.Presence = p.Type;
            }
    }

    // ---------------- Per-account game-window size memory ----------------

    private readonly DispatcherTimer _boundsPoll = new();
    private readonly Dictionary<Guid, SavedWindowBounds> _liveBounds = new();

    /// <summary>Restore an account's last window size once its client window appears (and again after Roblox settles).</summary>
    private async Task RestoreWindowBoundsAsync(TrackedProcess p)
    {
        var acct = Accounts.FirstOrDefault(a => a.Model.Id == p.AccountId)?.Model;
        if (acct?.WindowBounds is not { } b) return;

        for (var i = 0; i < 40; i++) // ~20s for the window to exist
        {
            if (!_tracker.IsRunning(p.AccountId)) return;
            if (p.MainWindow != IntPtr.Zero)
            {
                GameWindowBounds.Apply(p.MainWindow, b);
                await Task.Delay(4000);                         // Roblox resizes itself during load…
                if (_tracker.IsRunning(p.AccountId)) GameWindowBounds.Apply(p.MainWindow, b); // …so set it again
                return;
            }
            await Task.Delay(500);
        }
    }

    private void PersistWindowBounds(Guid accountId)
    {
        if (!_liveBounds.Remove(accountId, out var b)) return;
        var vm = Accounts.FirstOrDefault(a => a.Model.Id == accountId);
        if (vm is null) return;
        vm.Model.WindowBounds = b;
        _ = _vault.SaveAsync();
    }

    // ---------------- Filtering / groups ----------------

    public bool CanEditSelectedGroup => IsCustomGroup(SelectedGroup);

    partial void OnSelectedGroupChanged(string value)
    {
        OnPropertyChanged(nameof(CanEditSelectedGroup));
        SafeRefresh();
    }
    partial void OnSearchTextChanged(string value) => SafeRefresh();
    partial void OnStatusFilterChanged(string value) => SafeRefresh();

    /// <summary>
    /// Refresh the view, but never during a DataGrid edit — committing a cell edit (e.g. the Game
    /// column) raises Changed → RebuildGroups while the grid still holds an EditItem transaction,
    /// and CollectionView.Refresh() throws then. Defer to after the transaction closes.
    /// </summary>
    private void SafeRefresh()
    {
        if (AccountsView is IEditableCollectionView { IsEditingItem: true } or IEditableCollectionView { IsAddingNew: true })
            Application.Current.Dispatcher.BeginInvoke(new Action(() => AccountsView.Refresh()), DispatcherPriority.Background);
        else
            AccountsView.Refresh();
    }

    private bool MatchesFilter(AccountViewModel vm)
    {
        // SelectedGroup is briefly null while the ListBox rebuilds its items.
        if (SelectedGroup is { } g && g != AllGroups && vm.Group != g) return false;
        if (StatusFilter != "All" && StatusLabel(vm.Status) != StatusFilter) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        var q = SearchText.Trim();
        // Search the real username even in streamer mode, so filtering still works.
        return vm.Model.Username.Contains(q, StringComparison.OrdinalIgnoreCase)
            || vm.Notes.Contains(q, StringComparison.OrdinalIgnoreCase)
            || vm.GameOverride.Contains(q, StringComparison.OrdinalIgnoreCase)
            || vm.UserId.ToString().Contains(q);
    }

    private static string StatusLabel(AccountStatus s) => s switch
    {
        AccountStatus.Valid => "Valid",
        AccountStatus.Expired => "Expired",
        AccountStatus.Banned => "Banned",
        _ => "Unchecked"
    };

    private void ConfigureAutoValidate()
    {
        _autoValidate.Stop();
        if (_settings.AutoValidateIntervalMinutes > 0)
        {
            _autoValidate.Interval = TimeSpan.FromMinutes(_settings.AutoValidateIntervalMinutes);
            _autoValidate.Start();
        }
    }

    /// <summary>Called once from the window after it loads, so startup validation doesn't block the UI appearing.</summary>
    public async Task RunStartupTasksAsync()
    {
        if (_settings.AutoValidateOnStartup && Accounts.Count > 0)
            await ValidateAllAsync();
        await RefreshPresencesAsync();
    }

    /// <summary>Persist remembered view/group; called when the window closes.</summary>
    public void SaveViewState(double width, double height)
    {
        if (!_settings.RememberView) return;
        _settings.IsGridView = IsGridView;
        _settings.CardColumns = CardColumns;
        _settings.LastGroup = SelectedGroup;
        _settings.WindowWidth = width;
        _settings.WindowHeight = height;
        _settings.Save();
    }

    private void RebuildGroups()
    {
        var current = SelectedGroup;
        var wanted = new[] { AllGroups }
            .Concat(new[] { "Main", "Trading", "Farming Alts" })        // sensible defaults
            .Concat(_settings.CustomGroups)
            .Concat(Accounts.Select(a => a.Group))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Groups.Clear();
        foreach (var g in wanted) Groups.Add(g);
        SelectedGroup = Groups.Contains(current) ? current : AllGroups;
    }

    [RelayCommand]
    private void AddGroup()
    {
        var name = _dialogs.Prompt("Add group", "Group name:");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (name.Equals(AllGroups, StringComparison.OrdinalIgnoreCase) ||
            Groups.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            StatusMessage = $"Group '{name}' already exists or is reserved.";
            return;
        }

        _settings.CustomGroups.Add(name);
        _settings.Save();
        RebuildGroups();
        SelectedGroup = name;
    }

    [RelayCommand]
    private async Task EditGroupAsync()
    {
        if (!IsCustomGroup(SelectedGroup))
        {
            StatusMessage = "Select a custom group to edit.";
            return;
        }

        var oldName = SelectedGroup;
        var newName = _dialogs.Prompt("Edit group", "Group name:", oldName);
        if (string.IsNullOrWhiteSpace(newName)) return;
        newName = newName.Trim();
        if (newName.Equals(AllGroups, StringComparison.OrdinalIgnoreCase) ||
            Groups.Any(g => !g.Equals(oldName, StringComparison.OrdinalIgnoreCase) &&
                            g.Equals(newName, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = $"Group '{newName}' already exists or is reserved.";
            return;
        }

        var index = _settings.CustomGroups.FindIndex(g => g.Equals(oldName, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        _settings.CustomGroups[index] = newName;
        foreach (var account in Accounts.Where(a => a.Group.Equals(oldName, StringComparison.OrdinalIgnoreCase)))
        {
            account.Model.Group = newName;
            account.Refresh();
        }
        _settings.Save();
        await _vault.SaveAsync();
        RebuildGroups();
        SelectedGroup = newName;
        StatusMessage = $"Group renamed to '{newName}'.";
    }

    [RelayCommand]
    private async Task DeleteGroupAsync()
    {
        if (!IsCustomGroup(SelectedGroup))
        {
            StatusMessage = "Select a custom group to delete.";
            return;
        }

        var name = SelectedGroup;
        if (!_dialogs.Confirm($"Delete the group '{name}'? Accounts in it will move to Ungrouped.")) return;
        _settings.CustomGroups.RemoveAll(g => g.Equals(name, StringComparison.OrdinalIgnoreCase));
        foreach (var account in Accounts.Where(a => a.Group.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            account.Model.Group = "Ungrouped";
            account.Refresh();
        }
        _settings.Save();
        await _vault.SaveAsync();
        SelectedGroup = AllGroups;
        RebuildGroups();
        StatusMessage = $"Group '{name}' deleted.";
    }

    private bool IsCustomGroup(string? name) =>
        name is not null && _settings.CustomGroups.Contains(name, StringComparer.OrdinalIgnoreCase);

    [RelayCommand]
    private void SelectAllVisible()
    {
        var visible = AccountsView.Cast<AccountViewModel>().ToList();
        var target = !visible.All(v => v.IsSelected);
        foreach (var v in visible) v.IsSelected = target;
    }

    // ---------------- Account intake ----------------

    [RelayCommand]
    private async Task ImportCookiesAsync()
    {
        var result = _dialogs.ImportCookies(Groups.Where(g => g != AllGroups));
        if (result is null) return;

        var (cookies, group) = result.Value;
        int ok = 0, bad = 0;
        await RunBusy($"Validating {cookies.Count} cookie(s)…", async () =>
        {
            foreach (var c in cookies)
            {
                if (await AddValidatedCookieAsync(c, group)) ok++; else bad++;
            }
        });
        StatusMessage = $"Imported {ok} account(s)" + (bad > 0 ? $", {bad} invalid/expired." : ".");
    }

    [RelayCommand]
    private async Task BrowserLoginAsync()
    {
        var cookie = _dialogs.BrowserLogin(string.IsNullOrWhiteSpace(CustomUserAgent) ? null : CustomUserAgent);
        if (cookie is null) return;

        var group = SelectedGroup == AllGroups ? "Ungrouped" : SelectedGroup;
        await RunBusy("Validating login…", async () =>
        {
            StatusMessage = await AddValidatedCookieAsync(cookie, group)
                ? "Account added from browser login."
                : "Login captured but Roblox rejected the cookie.";
        });
    }

    [RelayCommand]
    private async Task BulkLoginAsync()
    {
        var input = _dialogs.BulkLoginInput(Groups.Where(g => g != AllGroups));
        if (input is null) return;

        var (creds, group, delay) = input.Value;
        var ua = string.IsNullOrWhiteSpace(CustomUserAgent) ? null : CustomUserAgent;
        int ok = 0;
        var failed = new List<string>();

        for (var i = 0; i < creds.Count; i++)
        {
            var (user, pass) = creds[i];
            if (i > 0)
                for (var left = delay; left > 0; left--) // pace logins so Roblox doesn't rate-limit the IP
                {
                    StatusMessage = $"Bulk import: next login ({user}) in {left}s…";
                    await Task.Delay(1000);
                }
            StatusMessage = $"Bulk import {i + 1}/{creds.Count}: {user}…";
            var result = _dialogs.BulkBrowserLogin(ua, user, pass, $"{i + 1}/{creds.Count}");

            if (result.Cookie is null)
            {
                failed.Add(result.NeedsManual
                    ? $"{user} (verification not completed)"
                    : $"{user} (window closed)");
                if (i < creds.Count - 1 && !_dialogs.Confirm($"Login for '{user}' was closed.\nContinue with the remaining {creds.Count - i - 1} account(s)?"))
                    break;
                continue;
            }
            var (added, why) = await TryAddCookieAsync(result.Cookie, group, user, pass);
            if (added) ok++; else failed.Add($"{user} ({why})");
        }

        StatusMessage = $"Bulk import: {ok} added" + (failed.Count > 0 ? $", {failed.Count} failed." : ".");
        if (failed.Count > 0)
            _dialogs.Error($"Bulk import finished: {ok} added, {failed.Count} failed.\n\n" + string.Join("\n", failed));
    }

    private async Task<bool> AddValidatedCookieAsync(string cookie, string group) =>
        (await TryAddCookieAsync(cookie, group)).Added;

    /// <summary>
    /// Validates and stores a cookie. Never throws for Roblox/network errors; returns why it
    /// failed instead. Banned accounts are still added, flagged with <see cref="AccountStatus.Banned"/>.
    /// </summary>
    private async Task<(bool Added, string Reason)> TryAddCookieAsync(string cookie, string group, string? knownUsername = null, string? password = null)
    {
        var (state, user) = await _api.GetAccountStateAsync(cookie);

        if (state == AccountState.Invalid) return (false, "cookie rejected");
        if (state == AccountState.Error) return (false, "check failed (network/rate-limit) — try again");

        if (state == AccountState.Banned)
        {
            // A banned account won't identify itself; resolve its id from the typed username so we can still list it.
            user = knownUsername is null ? null : await _api.ResolveUserAsync(knownUsername);
            if (user is null) return (false, "banned (couldn't resolve account id)");
            await StoreAsync(user, cookie, group, AccountStatus.Banned, password);
            return (true, "banned");
        }

        await StoreAsync(user!, cookie, group, AccountStatus.Valid, password);
        return (true, "");
    }

    /// <summary>Upserts the account into the vault + list and sets its status.</summary>
    private async Task StoreAsync(AuthenticatedUser user, string cookie, string group, AccountStatus status, string? password = null)
    {
        var existing = Accounts.FirstOrDefault(a => a.UserId == user.Id);
        var model = _vault.Upsert(user, cookie);
        model.Status = status;
        if (password is not null) _vault.SetPassword(model, password); // keep an older saved password otherwise
        if (existing is null)
        {
            model.Group = group;
            Add(model);
        }
        else existing.Status = status;
        existing?.Refresh();

        await _vault.SaveAsync();
        RebuildGroups();
        await LoadAvatarsAsync(new[] { user.Id });
    }

    [RelayCommand]
    private async Task ValidateAllAsync()
    {
        await RunBusy("Validating accounts…", async () =>
        {
            int valid = 0, expired = 0, banned = 0;
            foreach (var vm in Accounts.ToList())
            {
                var (state, user) = await _api.GetAccountStateAsync(_vault.GetCookie(vm.Model));
                vm.Model.LastValidated = DateTimeOffset.UtcNow;
                switch (state)
                {
                    case AccountState.Valid:
                        vm.Status = AccountStatus.Valid;
                        vm.Model.Username = user!.Name;     // handle username changes
                        vm.Model.DisplayName = user.DisplayName;
                        vm.Refresh();
                        valid++;
                        break;
                    case AccountState.Banned:
                        vm.Status = AccountStatus.Banned;
                        banned++;
                        break;
                    case AccountState.Invalid:
                        vm.Status = AccountStatus.Expired;
                        expired++;
                        break;
                    default:
                        vm.Status = AccountStatus.Error;
                        break;
                }
                await Task.Delay(250); // be polite to the API
            }
            await _vault.SaveAsync();
            await LoadAvatarsAsync(Accounts.Select(a => a.UserId));
            foreach (var vm in Accounts) vm.RefreshDisplay(); // update "last checked" times
            StatusMessage = $"{valid} valid, {expired} expired" + (banned > 0 ? $", {banned} banned." : ".");
        });
    }

    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        var sel = Accounts.Where(a => a.IsSelected).ToList();
        if (sel.Count == 0 || !_dialogs.Confirm($"Remove {sel.Count} account(s) from the vault?")) return;
        foreach (var vm in sel)
        {
            _vault.Remove(vm.Model.Id);
            Accounts.Remove(vm);
        }
        await _vault.SaveAsync();
        RebuildGroups();
    }

    public async Task LoadAvatarsAsync(IEnumerable<long> ids)
    {
        try
        {
            var map = await _api.GetHeadshotsAsync(ids);
            foreach (var vm in Accounts)
                if (map.TryGetValue(vm.UserId, out var url)) vm.AvatarUrl = url;
        }
        catch (Exception ex) { StatusMessage = "Avatar fetch failed: " + ex.Message; }
    }

    // ---------------- Per-account (right-click menu) ----------------

    [RelayCommand]
    private async Task RecheckAccountAsync(AccountViewModel? vm)
    {
        if (vm is null) return;
        var targets = TargetsFor(vm);
        int i = 0;
        foreach (var t in targets)
        {
            StatusMessage = targets.Count > 1 ? $"Checking {++i}/{targets.Count}…" : $"Checking {t.Label}…";
            var (state, user) = await _api.GetAccountStateAsync(_vault.GetCookie(t.Model));
            t.Model.LastValidated = DateTimeOffset.UtcNow;
            t.Status = state switch
            {
                AccountState.Valid => AccountStatus.Valid,
                AccountState.Banned => AccountStatus.Banned,
                AccountState.Invalid => AccountStatus.Expired,
                _ => AccountStatus.Error
            };
            if (user is not null) { t.Model.Username = user.Name; t.Model.DisplayName = user.DisplayName; }
            t.Refresh();
            if (targets.Count > 1) await Task.Delay(250); // be polite to the API
        }
        await _vault.SaveAsync();
        StatusMessage = targets.Count > 1 ? $"Re-checked {targets.Count} account(s)." : $"{vm.Label}: {vm.Status}.";
    }

    [RelayCommand]
    private async Task ReloginAccountAsync(AccountViewModel? vm)
    {
        if (vm is null) return;
        var ua = string.IsNullOrWhiteSpace(CustomUserAgent) ? null : CustomUserAgent;
        var targets = TargetsFor(vm);
        int ok = 0;
        foreach (var t in targets)
        {
            var pass = _vault.GetPassword(t.Model);
            StatusMessage = $"Re-login: {t.Label}…";
            string? cookie;
            if (pass is null)
                cookie = _dialogs.BrowserLogin(ua); // no saved password: plain login window
            else
            {
                var r = _dialogs.BulkBrowserLogin(ua, t.Model.Username, pass, "", _vault.GetTotpSecret(t.Model));
                cookie = r.Cookie;
            }
            if (cookie is null) continue; // cancelled this one; move on

            var (added, _) = await TryAddCookieAsync(cookie, t.Group, t.Model.Username, pass);
            if (added) ok++;
        }
        StatusMessage = $"Re-logged in {ok}/{targets.Count} account(s).";
    }

    [RelayCommand]
    private async Task RefreshInfoAsync(AccountViewModel? vm)
    {
        var targets = vm is not null ? TargetsFor(vm) : Accounts.Where(a => a.IsSelected).ToList();
        if (targets.Count == 0) targets = Accounts.ToList();
        if (targets.Count == 0) return;

        int i = 0;
        foreach (var t in targets)
        {
            StatusMessage = $"Fetching info {++i}/{targets.Count}: {t.Label}…";
            string cookie;
            try { cookie = _vault.GetCookie(t.Model); } catch { continue; }

            t.Model.Robux = await _api.GetRobuxAsync(cookie);
            t.Model.Premium = await _api.GetPremiumAsync(t.UserId, cookie);
            t.Model.Created ??= await _api.GetCreatedAsync(t.UserId); // created never changes
            t.Model.InfoUpdated = DateTimeOffset.UtcNow;
            t.RefreshInfo();
            if (targets.Count > 1) await Task.Delay(200);
        }
        await _vault.SaveAsync();
        StatusMessage = $"Refreshed info for {targets.Count} account(s).";
    }

    [RelayCommand]
    private async Task JoinGroupAsync(AccountViewModel? vm)
    {
        var targets = vm is not null ? TargetsFor(vm) : Accounts.Where(a => a.IsSelected).ToList();
        if (targets.Count == 0) { StatusMessage = "Select accounts first."; return; }
        var input = _dialogs.Prompt("Join Roblox group", $"Group ID for {targets.Count} account(s) to join:");
        if (!long.TryParse(input, out var groupId) || groupId <= 0) return;

        int ok = 0;
        var failed = new List<string>();
        for (var i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            StatusMessage = $"Joining group {i + 1}/{targets.Count}: {t.Label}…";
            string cookie;
            try { cookie = _vault.GetCookie(t.Model); } catch { failed.Add($"{t.Label} (no cookie)"); continue; }
            var (joined, msg) = await _api.JoinGroupAsync(groupId, cookie);
            if (joined) ok++; else failed.Add($"{t.Label} ({msg})");
            if (targets.Count > 1) await Task.Delay(400); // pace to avoid rate limits
        }
        StatusMessage = $"Joined group {groupId}: {ok}/{targets.Count}.";
        if (failed.Count > 0) _dialogs.Error($"Joined {ok}/{targets.Count}.\n\nFailed:\n" + string.Join("\n", failed));
    }

    [RelayCommand]
    private void GroupPayout(AccountViewModel? vm)
    {
        var recipients = vm is not null ? TargetsFor(vm) : Accounts.Where(a => a.IsSelected).ToList();
        if (recipients.Count == 0) { StatusMessage = "Select the accounts to pay first."; return; }
        _dialogs.OpenGroupPayout(Accounts.ToList(), recipients, _api,
            a => { try { return _vault.GetCookie(a.Model); } catch { return null; } });
    }

    [RelayCommand]
    private async Task SetTotpSecretAsync(AccountViewModel? vm)
    {
        if (vm is null) return;
        var current = _vault.GetTotpSecret(vm.Model);
        var input = _dialogs.Prompt("2FA secret",
            $"Authenticator setup key for {vm.Label} (the base32 secret from the QR code). Blank to remove:", current ?? "");
        if (input is null) return;
        if (input.Length > 0 && !Totp.IsValidSecret(input))
        {
            _dialogs.Error("That doesn't look like a valid base32 authenticator key.");
            return;
        }
        _vault.SetTotpSecret(vm.Model, input.Length == 0 ? null : input);
        await _vault.SaveAsync();
        vm.RefreshInfo();
        StatusMessage = input.Length == 0 ? $"Removed 2FA secret for {vm.Label}." : $"Saved 2FA secret for {vm.Label}.";
    }

    [RelayCommand]
    private void CopyTotpCode(AccountViewModel? vm)
    {
        if (vm is null) return;
        var secret = _vault.GetTotpSecret(vm.Model);
        if (secret is null) { StatusMessage = $"No 2FA secret saved for {vm.Label}."; return; }
        try { Copy(Totp.Generate(secret), "2FA code"); }
        catch { StatusMessage = "That saved 2FA secret is invalid."; }
    }

    [RelayCommand]
    private async Task RedeemPromoAsync(AccountViewModel? vm)
    {
        var targets = vm is not null ? TargetsFor(vm) : Accounts.Where(a => a.IsSelected).ToList();
        if (targets.Count == 0) { StatusMessage = "Select accounts first."; return; }
        var code = _dialogs.Prompt("Redeem promo code", $"Promo code for {targets.Count} account(s):");
        if (string.IsNullOrWhiteSpace(code)) return;

        int ok = 0;
        var failed = new List<string>();
        for (var i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            StatusMessage = $"Redeeming {i + 1}/{targets.Count}: {t.Label}…";
            string cookie;
            try { cookie = _vault.GetCookie(t.Model); } catch { failed.Add($"{t.Label} (no cookie)"); continue; }
            var (okr, msg) = await _api.RedeemPromoCodeAsync(code, cookie);
            if (okr) ok++; else failed.Add($"{t.Label} ({msg})");
            if (targets.Count > 1) await Task.Delay(400);
        }
        StatusMessage = $"Promo '{code}': {ok}/{targets.Count} redeemed.";
        if (failed.Count > 0) _dialogs.Error($"Redeemed {ok}/{targets.Count}.\n\nFailed:\n" + string.Join("\n", failed));
    }

    [RelayCommand]
    private void OpenBrowser(AccountViewModel? vm)
    {
        if (vm is null) return;
        var targets = TargetsFor(vm);
        if (targets.Count > 4 &&
            !_dialogs.Confirm($"Open {targets.Count} browser windows, one per account?")) return;
        foreach (var t in targets)
        {
            try { _dialogs.OpenRobloxBrowser(_vault.GetCookie(t.Model), t.Model.Id.ToString(), t.Label, t.UserId); }
            catch (Exception ex) { _dialogs.Error($"Could not open browser for {t.Label}: " + ex.Message); }
        }
    }

    [RelayCommand]
    private void CopyUserPass(AccountViewModel? vm) => CopyEach(vm, "user:pass", t =>
    {
        var p = _vault.GetPassword(t.Model);
        return p is null ? null : $"{t.Model.Username}:{p}";
    });

    [RelayCommand]
    private void CopyPassword(AccountViewModel? vm) => CopyEach(vm, "password", t => _vault.GetPassword(t.Model));

    [RelayCommand] private void CopyUsername(AccountViewModel? vm) => CopyEach(vm, "username", t => t.Model.Username);
    [RelayCommand] private void CopyUserId(AccountViewModel? vm) => CopyEach(vm, "Roblox ID", t => t.UserId.ToString());
    [RelayCommand]
    private void CopyProfileLink(AccountViewModel? vm) =>
        CopyEach(vm, "profile link", t => $"https://www.roblox.com/users/{t.UserId}/profile");

    /// <summary>Copies one line per selected account; skips accounts the selector can't produce (e.g. no password).</summary>
    private void CopyEach(AccountViewModel? vm, string what, Func<AccountViewModel, string?> line)
    {
        if (vm is null) return;
        var targets = TargetsFor(vm);
        var lines = targets.Select(line).OfType<string>().ToList();
        if (lines.Count == 0) { StatusMessage = $"No {what} available for the selected account(s)."; return; }
        var label = lines.Count > 1 ? $"{what} ×{lines.Count}" : what;
        Copy(string.Join(Environment.NewLine, lines), label == what ? what : label);
    }

    /// <summary>Gives the account its own game: whatever is in the Launch panel's game box right now.</summary>
    [RelayCommand]
    private void UseLaunchGameForAccount(AccountViewModel? vm)
    {
        if (vm is null) return;
        if (string.IsNullOrWhiteSpace(PlaceInput)) { StatusMessage = "Type a game in the Launch panel first."; return; }
        var placeId = LaunchTargetParser.Parse(PlaceInput).PlaceId;
        if (placeId is null or <= 0) { StatusMessage = "The Launch panel input has no game ID."; return; }
        foreach (var a in TargetsFor(vm)) a.GameOverride = placeId.Value.ToString();
        StatusMessage = $"Game set for {TargetsFor(vm).Count} account(s).";
    }

    [RelayCommand]
    private void ClearAccountGame(AccountViewModel? vm)
    {
        if (vm is null) return;
        foreach (var a in TargetsFor(vm)) a.GameOverride = "";
        StatusMessage = "Account game cleared — it will use the Launch panel's game.";
    }

    /// <summary>Right-clicking a checked account applies game changes to every checked account.</summary>
    private List<AccountViewModel> TargetsFor(AccountViewModel vm) =>
        vm.IsSelected ? Accounts.Where(a => a.IsSelected).ToList() : [vm];

    [RelayCommand]
    private void ToggleFavorite(AccountViewModel? vm)
    {
        if (vm is null) return;
        foreach (var a in TargetsFor(vm)) a.Favorite = !vm.Favorite;
        SafeRefresh();
    }

    [RelayCommand]
    private async Task EditNoteAsync(AccountViewModel? vm)
    {
        if (vm is null) return;
        var targets = TargetsFor(vm);
        var label = targets.Count > 1 ? $"{targets.Count} account(s)" : vm.Label;
        var note = _dialogs.Prompt("Note", $"Note for {label}:", vm.Notes);
        if (note is null) return;
        foreach (var t in targets) t.Notes = note;
        await _vault.SaveAsync();
    }

    [RelayCommand]
    private async Task SetGroupForSelectedAsync(AccountViewModel? vm)
    {
        var targets = vm is not null ? TargetsFor(vm) : Accounts.Where(a => a.IsSelected).ToList();
        if (targets.Count == 0) { StatusMessage = "Select accounts first."; return; }
        var group = _dialogs.Prompt("Move to group", $"Group for {targets.Count} account(s):", targets[0].Group);
        if (string.IsNullOrWhiteSpace(group)) return;
        foreach (var a in targets) a.Group = group;
        await _vault.SaveAsync();
        RebuildGroups();
    }

    private void Copy(string text, string what)
    {
        var secret = what.StartsWith("password") || what.StartsWith("user:pass");
        if (secret && _settings.ConfirmBeforeCopyPassword &&
            !_dialogs.Confirm("Copy the password(s) to the clipboard in plain text?")) return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { StatusMessage = "Clipboard is busy — try again."; return; }

        StatusMessage = $"Copied {what}.";
        if (secret && _settings.ClipboardClearSeconds > 0) ScheduleClipboardClear(text);
    }

    private void ScheduleClipboardClear(string copied)
    {
        _clipboardClear?.Stop();
        _clipboardClear = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_settings.ClipboardClearSeconds) };
        _clipboardClear.Tick += (_, _) =>
        {
            _clipboardClear!.Stop();
            try { if (Clipboard.ContainsText() && Clipboard.GetText() == copied) { Clipboard.Clear(); StatusMessage = "Clipboard cleared."; } }
            catch (System.Runtime.InteropServices.COMException) { /* clipboard busy; leave it */ }
        };
        _clipboardClear.Start();
    }

    // ---------------- View / settings ----------------

    [ObservableProperty] private bool _isGridView;
    /// <summary>Cards per row in grid view. 0 = fit as many as the width allows.</summary>
    [ObservableProperty] private int _cardColumns;
    [ObservableProperty] private bool _showGroups = true;
    [ObservableProperty] private bool _showLaunch = true;

    partial void OnShowGroupsChanged(bool value) { _settings.ShowGroups = value; _settings.Save(); }
    partial void OnShowLaunchChanged(bool value) { _settings.ShowLaunch = value; _settings.Save(); }

    [RelayCommand] private void ShowList() => IsGridView = false;
    [RelayCommand] private void ShowGrid() => IsGridView = true;

    // ---------------- Game history ----------------

    private void RecordGameHistory(IEnumerable<long> placeIds)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pid in placeIds.Where(p => p > 0).Distinct())
        {
            var e = _settings.GameHistory.FirstOrDefault(g => g.PlaceId == pid);
            if (e is null) { e = new GameHistoryEntry { PlaceId = pid }; _settings.GameHistory.Add(e); }
            e.LastPlayed = now;
            e.LaunchCount++;
        }
        _settings.GameHistory = _settings.GameHistory.OrderByDescending(g => g.LastPlayed).Take(200).ToList();
        _settings.Save();
    }

    [RelayCommand]
    private void OpenGameHistory()
    {
        string? cookie = null;
        foreach (var a in Accounts) { try { cookie = _vault.GetCookie(a.Model); break; } catch { /* skip unreadable */ } }

        _dialogs.OpenGameHistory(_settings.GameHistory, _api, cookie,
            onUse: pid => { PlaceInput = pid.ToString(); StatusMessage = $"Launch game set to place {pid}."; },
            onClose: removed =>
            {
                if (removed.Count > 0) _settings.GameHistory.RemoveAll(g => removed.Contains(g.PlaceId));
                _settings.Save(); // also persists any game names resolved while the window was open
            });
    }

    [RelayCommand]
    private void OpenSettings()
    {
        if (!_dialogs.EditSettings(_settings)) return;
        CustomUserAgent = _settings.CustomUserAgent;
        if (Application.Current.MainWindow is Window mainWindow)
            mainWindow.Topmost = _settings.AlwaysOnTop;
        ApplyStreamerMode(_settings.StreamerMode);
        ConfigureAutoValidate();
        OnPropertyChanged(nameof(CardWidth));
    }

    [RelayCommand]
    private void ToggleStreamer()
    {
        _settings.StreamerMode = !_settings.StreamerMode;
        _settings.Save();
        ApplyStreamerMode(_settings.StreamerMode);
    }

    public bool StreamerMode => _settings.StreamerMode;
    public int CardWidth => _settings.CardWidth;

    private void ApplyStreamerMode(bool on)
    {
        AccountViewModel.Masked = on;
        foreach (var vm in Accounts) vm.RefreshDisplay();
        OnPropertyChanged(nameof(StreamerMode));
    }

    // ---------------- Launch presets ----------------

    partial void OnSelectedPresetChanged(string? value)
    {
        var p = _settings.Presets.FirstOrDefault(x => x.Name == value);
        if (p is null) return;
        PlaceInput = p.Game;
        JobId = p.JobId;
        PrivateServerAccessCode = p.PrivateServerAccessCode;
        PrivateServerLinkCode = p.PrivateServerLinkCode;
        StatusMessage = $"Loaded preset '{p.Name}'.";
    }

    [RelayCommand]
    private void SavePreset()
    {
        if (string.IsNullOrWhiteSpace(PlaceInput)) { StatusMessage = "Enter a game to save as a preset."; return; }
        var name = _dialogs.Prompt("Save preset", "Preset name:", SelectedPreset ?? "");
        if (string.IsNullOrWhiteSpace(name)) return;

        var preset = _settings.Presets.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? new LaunchPreset();
        preset.Name = name;
        preset.Game = PlaceInput;
        preset.JobId = JobId;
        preset.PrivateServerAccessCode = PrivateServerAccessCode;
        preset.PrivateServerLinkCode = PrivateServerLinkCode;
        if (!_settings.Presets.Contains(preset)) _settings.Presets.Add(preset);
        _settings.Save();

        if (!Presets.Contains(name)) Presets.Add(name);
        SelectedPreset = name;
        StatusMessage = $"Saved preset '{name}'.";
    }

    [RelayCommand]
    private void DeletePreset()
    {
        if (SelectedPreset is null) return;
        _settings.Presets.RemoveAll(x => x.Name == SelectedPreset);
        _settings.Save();
        Presets.Remove(SelectedPreset);
        SelectedPreset = null;
    }

    // ---------------- Launching ----------------

    private bool CanLaunch() => !IsLaunching;

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task LaunchSelectedAsync()
    {
        var accounts = AccountsView.Cast<AccountViewModel>().Where(a => a.IsSelected).ToList();
        if (accounts.Count == 0) { StatusMessage = "Select at least one account."; return; }

        // Each account goes to its own game if it has one, otherwise to the Launch panel's game.
        var targets = new Dictionary<Guid, LaunchTarget>();
        LaunchTarget? defaultTarget = null;
        foreach (var a in accounts)
        {
            if (!string.IsNullOrWhiteSpace(a.GameOverride))
            {
                var own = await BuildTargetAsync(a.GameOverride, a.Model, useLaunchPanelExtras: false);
                if (own is null) { StatusMessage = $"{a.Label}'s game: {StatusMessage}"; return; }
                targets[a.Model.Id] = own;
            }
            else
            {
                defaultTarget ??= await BuildTargetAsync(PlaceInput, a.Model, useLaunchPanelExtras: true);
                if (defaultTarget is null) return;
                targets[a.Model.Id] = defaultTarget;
            }
        }

        if (!_guard.IsActive && !_guard.Acquire() && accounts.Count > 1)
        {
            _dialogs.Error("Multi-instance mode is off because a Roblox client was already open when the manager " +
                           "started. Close every Roblox window, then try again.");
            OnPropertyChanged(nameof(MultiInstanceActive));
            return;
        }
        OnPropertyChanged(nameof(MultiInstanceActive));

        RecordGameHistory(targets.Values.Select(t => t.PlaceId));

        // Remember each account's target and treat this as intentional, so auto-rejoin applies on a later crash.
        foreach (var kv in targets) { _lastTarget[kv.Key] = kv.Value; _userClosed.Remove(kv.Key); }

        IsLaunching = true;
        _launchCts = new CancellationTokenSource();
        var progress = new Progress<LaunchProgress>(p =>
        {
            var vm = Accounts.FirstOrDefault(a => a.Model.Id == p.Account.Id);
            if (vm is null) return;
            vm.LaunchState = p.Stage + (p.Message is null ? "" : $": {p.Message}");
            if (p.Stage == LaunchStage.Failed) vm.Refresh(); // engine may have flagged the model Expired
            StatusMessage = $"{vm.Label}: {vm.LaunchState}";
        });

        try
        {
            await _engine.LaunchBatchAsync(
                accounts.Select(a => a.Model).ToList(), a => targets[a.Id],
                new LaunchOptions { StaggerDelay = TimeSpan.FromSeconds(Math.Max(0, StaggerSeconds)) },
                progress, _launchCts.Token);
            StatusMessage = "Batch finished.";
        }
        finally
        {
            IsLaunching = false;
            _launchCts.Dispose();
            _launchCts = null;
            await _vault.SaveAsync(); // persist any Expired flags / rotated cookies
        }
    }

    private bool CanCancel() => IsLaunching;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelLaunch() => _launchCts?.Cancel();

    /// <param name="useLaunchPanelExtras">
    /// Apply the panel's Job ID / private-server fields. Off for per-account games, whose
    /// link already says where to go.
    /// </param>
    private async Task<LaunchTarget?> BuildTargetAsync(string input, Account resolver, bool useLaunchPanelExtras)
    {
        if (string.IsNullOrWhiteSpace(input)) { StatusMessage = "Enter a Place ID or game/server link."; return null; }

        var parsed = LaunchTargetParser.Parse(input);
        var placeId = parsed.PlaceId;
        var linkCode = !useLaunchPanelExtras || string.IsNullOrWhiteSpace(PrivateServerLinkCode) ? parsed.LinkCode : PrivateServerLinkCode;

        if (parsed.ShareCode is not null)
        {
            try
            {
                var resolved = await _api.ResolveServerShareLinkAsync(_vault.GetCookie(resolver), parsed.ShareCode);
                if (resolved is null) { StatusMessage = "Share link is not a private-server invite."; return null; }
                (placeId, linkCode) = (resolved.Value.PlaceId, resolved.Value.LinkCode);
            }
            catch (Exception ex) { StatusMessage = "Could not resolve share link: " + ex.Message; return null; }
        }

        if (placeId is null or <= 0) { StatusMessage = "Could not find a Place ID in that input."; return null; }

        if (!useLaunchPanelExtras)
            return new LaunchTarget(placeId.Value, PrivateServerLinkCode: linkCode);

        return new LaunchTarget(placeId.Value,
            JobId: string.IsNullOrWhiteSpace(JobId) ? null : JobId.Trim(),
            PrivateServerAccessCode: string.IsNullOrWhiteSpace(PrivateServerAccessCode) ? null : PrivateServerAccessCode.Trim(),
            PrivateServerLinkCode: linkCode);
    }

    // ---------------- Processes / windows ----------------

    [RelayCommand]
    private void ArrangeWindows()
    {
        var hwnds = ActiveProcesses.Select(p => p.MainWindow).ToList();
        var n = WindowArranger.Arrange(hwnds, GridColumns > 0 ? GridColumns : null);
        StatusMessage = n == 0 ? "No game windows found yet." : $"Arranged {n} window(s).";
    }

    [RelayCommand]
    private void KillProcess(TrackedProcess? p)
    {
        if (p is null) return;
        _userClosed.Add(p.AccountId);  // intentional close → no auto-rejoin
        _tracker.Kill(p.Pid);
    }

    [RelayCommand]
    private void KillAll()
    {
        if (ActiveProcesses.Count == 0 || !_dialogs.Confirm($"Close all {ActiveProcesses.Count} tracked clients?")) return;
        foreach (var p in ActiveProcesses.ToList()) { _userClosed.Add(p.AccountId); _tracker.Kill(p.Pid); }
    }

    /// <summary>Panic hotkey: close every tracked client at once, no confirm.</summary>
    [RelayCommand]
    private void PanicClose()
    {
        foreach (var p in ActiveProcesses.ToList()) { _userClosed.Add(p.AccountId); _tracker.Kill(p.Pid); }
        StatusMessage = "Panic close: all clients killed.";
    }

    // ---------------- Auto-rejoin watchdog ----------------

    private readonly Dictionary<Guid, LaunchTarget> _lastTarget = new();
    private readonly HashSet<Guid> _userClosed = new();
    private readonly Dictionary<Guid, (int Count, DateTime Since)> _rejoinBudget = new();

    private void MaybeRejoin(Guid accountId)
    {
        if (!_settings.AutoRejoin || !_lastTarget.TryGetValue(accountId, out var target)) return;
        var vm = Accounts.FirstOrDefault(a => a.Model.Id == accountId);
        if (vm is null) return;

        // At most 3 rejoins in a 5-minute window, so a crash-loop doesn't spin forever.
        var now = DateTime.UtcNow;
        var b = _rejoinBudget.TryGetValue(accountId, out var x) ? x : (Count: 0, Since: now);
        if ((now - b.Since).TotalMinutes > 5) b = (0, now);
        if (b.Count >= 3) { StatusMessage = $"{vm.Label}: auto-rejoin paused (too many restarts)."; return; }
        _rejoinBudget[accountId] = (b.Count + 1, b.Since);
        _ = RejoinAsync(vm, target);
    }

    private async Task RejoinAsync(AccountViewModel vm, LaunchTarget target)
    {
        StatusMessage = $"Auto-rejoin: {vm.Label} in 5s…";
        await Task.Delay(5000);
        if (!_settings.AutoRejoin || _tracker.IsRunning(vm.Model.Id)) return;
        var progress = new Progress<LaunchProgress>(pr =>
        {
            var v = Accounts.FirstOrDefault(a => a.Model.Id == pr.Account.Id);
            if (v is not null) v.LaunchState = pr.Stage.ToString();
        });
        try { await _engine.LaunchAsync(vm.Model, target, new LaunchOptions(), progress); } catch { /* best effort */ }
    }

    // ---------------- Helpers ----------------

    private void Add(Account a)
    {
        var vm = new AccountViewModel(a);
        // Defer to Background so a cell-edit commit finishes before we save/rebuild (which can Refresh the view).
        vm.Changed += _ => Application.Current.Dispatcher.BeginInvoke(
            new Action(async () => { await _vault.SaveAsync(); RebuildGroups(); }), DispatcherPriority.Background);
        Accounts.Add(vm);
    }

    private void SetPid(Guid accountId, int? pid)
    {
        var vm = Accounts.FirstOrDefault(a => a.Model.Id == accountId);
        if (vm is null) return;
        vm.Pid = pid;
        vm.LaunchState = pid is null ? "Exited" : "Running";
    }

    private void OnCookieRotated(object? sender, CookieRotatedEventArgs e)
    {
        OnUi(async () =>
        {
            foreach (var vm in Accounts)
            {
                if (_vault.GetCookie(vm.Model) != e.OldCookie) continue;
                _vault.SetCookie(vm.Model, e.NewCookie);
                await _vault.SaveAsync();
                break;
            }
        });
    }

    private async Task RunBusy(string message, Func<Task> work)
    {
        IsBusy = true;
        StatusMessage = message;
        try { await work(); }
        catch (Exception ex) { _dialogs.Error(ex.Message); }
        finally { IsBusy = false; }
    }

    private static void OnUi(Action a) => Application.Current.Dispatcher.BeginInvoke(a);

    public void Dispose()
    {
        _boundsPoll.Stop();
        _presencePoll.Stop();
        _autoValidate.Stop();
        _clipboardClear?.Stop();
        _launchCts?.Cancel();
        _tracker.Dispose();
        _api.Dispose();
        _vault.Dispose();
        _guard.Dispose();
    }
}
