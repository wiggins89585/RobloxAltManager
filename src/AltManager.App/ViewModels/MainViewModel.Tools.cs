using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using AltManager.App.Services;
using AltManager.Core;
using AltManager.Core.Launch;
using AltManager.Core.Models;
using AltManager.Core.Native;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AltManager.App.ViewModels;

/// <summary>
/// The Tools features: Max alts, RAM cleaner, window tools (borderless, pin, minimize, small windows),
/// client limit, crash breaker, global hotkeys, setups, anti-AFK, macros, Discord presence and webhooks.
/// Everything is driven by one 2-second timer so nothing piles up.
/// </summary>
public partial class MainViewModel
{
    private readonly DispatcherTimer _toolsTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<int, bool> _backgrounded = new();   // pid → currently throttled by Max alts
    private readonly Dictionary<Guid, DateTime> _breakerUntil = new();
    private readonly DiscordRpc _discord = new();
    private HotkeyService? _hotkeys;
    private DateTime _lastTrim = DateTime.UtcNow, _lastAfk = DateTime.UtcNow, _lastRamCheck, _lastRamClean;
    private bool _afkRunning, _discordWasOn, _wasBorderless;

    public UsageMonitor Usage { get; } = new();
    public MacroRecorder Macro { get; } = new();
    public ToolsSettings Tools => _settings.Tools;

    /// <summary>Raised after each usage sample (the Monitor window listens).</summary>
    public event Action? UsageUpdated;

    [ObservableProperty] private string _memoryText = "";
    [ObservableProperty] private string _macroStatus = "No macro recorded";

    public bool MaxAltsOn
    {
        get => Tools.MaxAlts;
        set { if (Tools.MaxAlts == value) return; Tools.MaxAlts = value; _settings.Save(); OnPropertyChanged(); ApplyTools(); }
    }

    public bool AntiAfkOn
    {
        get => Tools.AntiAfk;
        set { if (Tools.AntiAfk == value) return; Tools.AntiAfk = value; _lastAfk = DateTime.UtcNow; _settings.Save(); OnPropertyChanged(); }
    }

    private void InitTools()
    {
        _wasBorderless = Tools.Borderless;
        _toolsTimer.Tick += (_, _) => ToolsTick();
        _toolsTimer.Start();
        Macro.StateChanged += () => OnUi(UpdateMacroStatus);
        _tracker.ProcessStarted += (_, p) => OnUi(() => OnClientStarted(p));
        _discord.StatusChanged += s =>
        {
            Log(s.StartsWith("Showing") || s.StartsWith("Connected") || s == "Off" ? ActivityLevel.Info : ActivityLevel.Warn, "Discord: " + s);
            OnUi(() => OnPropertyChanged(nameof(DiscordStatus)));
        };
        ApplyRamText();
    }

    private void DisposeTools()
    {
        _toolsTimer.Stop();
        Macro.Dispose();
        _hotkeys?.Dispose();
        try { _discord.ClearAsync().Wait(1000); } catch { }
        _discord.Dispose();
        foreach (var pid in _backgrounded.Keys) ClientOptimizer.Restore(pid);
    }

    // ---------------- Timer ----------------

    private void ToolsTick()
    {
        var procs = ActiveProcesses.ToList();
        var now = DateTime.UtcNow;

        // Usage (Monitor page)
        Usage.Update(procs.Select(p => p.Pid));
        UsageUpdated?.Invoke();

        // Window tools
        foreach (var p in procs)
        {
            var h = p.MainWindow;
            if (h == IntPtr.Zero) continue;
            if (Tools.Borderless) RobloxWindows.SetBorderless(h, true);
            if (Tools.RenameWindows)
            {
                var label = Accounts.FirstOrDefault(a => a.Model.Id == p.AccountId)?.Label ?? p.AccountLabel;
                RobloxWindows.SetTitle(h, $"Roblox - {label}");
            }
            if (Tools.Pinned.TryGetValue(p.AccountId, out var pin) && RobloxWindows.Rect(h) is { } r && !User32.IsIconic(h) &&
                (r.Left != pin.X || r.Top != pin.Y || r.Width != pin.Width || r.Height != pin.Height))
                RobloxWindows.Place(h, pin.X, pin.Y, pin.Width, pin.Height, Tools.AllowSmallWindows);
        }

        // Max alts: only the client you're looking at gets full speed.
        if (Tools.MaxAlts)
        {
            var fg = User32.ForegroundPid();
            foreach (var p in procs)
            {
                var bg = p.Pid != fg;
                if (_backgrounded.TryGetValue(p.Pid, out var was) && was == bg) continue;
                ClientOptimizer.SetBackground(p.Pid, bg);
                _backgrounded[p.Pid] = bg;
            }
            foreach (var gone in _backgrounded.Keys.Where(k => procs.All(p => p.Pid != k)).ToList()) _backgrounded.Remove(gone);
            if (Tools.MaxAltsTrimMinutes > 0 && (now - _lastTrim).TotalMinutes >= Tools.MaxAltsTrimMinutes)
            {
                _lastTrim = now;
                MemoryCleaner.TrimAll(procs.Where(p => p.Pid != fg).Select(p => p.Pid));
            }
        }

        // RAM: refresh the readout every 10s and auto-clean if memory is tight.
        if ((now - _lastRamCheck).TotalSeconds >= 10)
        {
            _lastRamCheck = now;
            var st = ApplyRamText();
            if (Tools.AutoRamClean && st.LoadPercent >= Tools.RamCleanPercent && (now - _lastRamClean).TotalMinutes >= 5)
            {
                _lastRamClean = now;
                var n = MemoryCleaner.TrimAll(procs.Select(p => p.Pid));
                Log(ActivityLevel.Info, $"RAM at {st.LoadPercent}% — trimmed {n} client(s).");
            }
        }

        // Anti-AFK
        if (Tools.AntiAfk && !_afkRunning && !Macro.IsPlaying && !Macro.IsRecording && procs.Count > 0 &&
            (now - _lastAfk).TotalMinutes >= Math.Max(1, Tools.AntiAfkMinutes))
        {
            _lastAfk = now;
            _ = RunAntiAfkAsync(procs);
        }

        // Discord presence
        if (Tools.DiscordRpc && !string.IsNullOrWhiteSpace(Tools.DiscordAppId))
        {
            _discordWasOn = true;
            _ = _discord.UpdateAsync(Tools.DiscordAppId.Trim(), BuildPresence(procs));
        }
        else if (_discordWasOn)
        {
            _discordWasOn = false;
            _ = _discord.ClearAsync();
        }
    }

    // ---------------- Discord card ----------------

    private const string AppIconUrl =
        "https://raw.githubusercontent.com/wiggins89585/RobloxAltManager/main/src/AltManager.App/Assets/RobloxAltManager.png";
    private const string ReleasesUrl = "https://github.com/wiggins89585/RobloxAltManager/releases/latest";
    private readonly Dictionary<long, string> _gameNames = new();
    private readonly Dictionary<long, string> _gameIcons = new();
    private readonly HashSet<long> _gameLookups = new();

    /// <summary>The profile card: game icon + name while clients run, the app logo while idle.</summary>
    private DiscordRpc.Activity BuildPresence(List<TrackedProcess> procs)
    {
        var version = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3);
        var appText = $"Alt Manager v{version}";
        var button = Tools.DiscordShowButton ? "Get Alt Manager" : null;
        var valid = Accounts.Count(a => a.Status == AccountStatus.Valid);

        if (procs.Count == 0)
            return new DiscordRpc.Activity(
                Details: "Idle — no alts running",
                State: $"{Accounts.Count} accounts · {valid} ready",
                Type: Tools.DiscordActivityType,
                LargeImage: AppIconUrl, LargeText: appText,
                ButtonLabel: button, ButtonUrl: ReleasesUrl);

        var alts = procs.Count == 1 ? "1 alt" : $"{procs.Count} alts";
        var since = procs.Min(p => p.Started).ToUnixTimeSeconds();
        var games = procs.Select(p => _lastTarget.TryGetValue(p.AccountId, out var t) ? t.PlaceId : 0)
                         .Where(id => id > 0).GroupBy(id => id).OrderByDescending(g => g.Count()).ToList();

        string? gameName = null, gameIcon = null;
        if (Tools.DiscordShowGame && games.Count > 0)
        {
            var top = games[0].Key;
            LookUpGame(top);
            _gameNames.TryGetValue(top, out gameName);
            _gameIcons.TryGetValue(top, out gameIcon);
        }

        var details = gameName is not null ? gameName : $"Running {alts}";
        var state = gameName is null ? $"{valid} of {Accounts.Count} accounts ready"
                  : games.Count > 1 ? $"Running {alts} in {games.Count} games"
                  : $"Running {alts}";
        var max = Tools.MaxClients > 0 ? Tools.MaxClients : Math.Max(procs.Count, Accounts.Count);

        return new DiscordRpc.Activity(
            Details: details,
            State: state,
            Type: Tools.DiscordActivityType,
            Start: since,
            LargeImage: gameIcon ?? AppIconUrl, LargeText: gameName ?? appText,
            SmallImage: gameIcon is not null ? AppIconUrl : null, SmallText: gameIcon is not null ? appText : null,
            PartySize: procs.Count, PartyMax: max,
            ButtonLabel: button, ButtonUrl: ReleasesUrl);
    }

    /// <summary>Fetches a game's name and icon once (in the background); the card picks them up next tick.</summary>
    private void LookUpGame(long placeId)
    {
        if (!_gameLookups.Add(placeId)) return;
        var known = _settings.GameHistory.FirstOrDefault(g => g.PlaceId == placeId)?.Name;
        if (!string.IsNullOrWhiteSpace(known)) _gameNames[placeId] = known;
        string? cookie = null;
        foreach (var a in Accounts) { try { cookie = _vault.GetCookie(a.Model); break; } catch { } }
        _ = Task.Run(async () =>
        {
            try
            {
                var icons = await _api.GetPlaceIconsAsync(new[] { placeId });
                var names = _gameNames.ContainsKey(placeId) || cookie is null
                    ? new Dictionary<long, string>()
                    : await _api.GetPlaceNamesAsync(new[] { placeId }, cookie);
                OnUi(() =>
                {
                    if (icons.TryGetValue(placeId, out var i)) _gameIcons[placeId] = i;
                    if (names.TryGetValue(placeId, out var n)) _gameNames[placeId] = n;
                });
            }
            catch { _gameLookups.Remove(placeId); }
        });
    }

    private MemoryCleaner.MemoryStatus ApplyRamText()
    {
        var st = MemoryCleaner.Status();
        MemoryText = $"RAM {st.LoadPercent}% · {st.AvailableMb / 1024.0:0.0} GB free";
        return st;
    }

    private async Task RunAntiAfkAsync(List<TrackedProcess> procs)
    {
        _afkRunning = true;
        try
        {
            await AntiAfk.RunAsync(procs.Select(p => p.MainWindow).Where(h => h != IntPtr.Zero).ToList(), Tools.AntiAfkMode);
            Log(ActivityLevel.Info, $"Anti-AFK: kept {procs.Count} client(s) active.");
        }
        finally { _afkRunning = false; }
    }

    private void OnClientStarted(TrackedProcess p)
    {
        if (Tools.WebhookOnLaunch)
            Webhook("Client started", $"{LabelFor(p.AccountId)} opened Roblox.");
    }

    /// <summary>Called after the Tools window saves: puts everything in line with the new settings.</summary>
    public void ApplyTools()
    {
        var procs = ActiveProcesses.ToList();
        if (!Tools.MaxAlts && _backgrounded.Count > 0)
        {
            foreach (var pid in _backgrounded.Keys)
            {
                if (_settings.OptimizeClient) ClientOptimizer.Apply(pid); else ClientOptimizer.Restore(pid);
            }
            _backgrounded.Clear();
        }
        if (_wasBorderless && !Tools.Borderless)
            foreach (var p in procs) RobloxWindows.SetBorderless(p.MainWindow, false);
        _wasBorderless = Tools.Borderless;
        RegisterHotkeys();
        OnPropertyChanged(nameof(MaxAltsOn));
        OnPropertyChanged(nameof(AntiAfkOn));
        OnPropertyChanged(nameof(Tools));
    }

    /// <summary>Stores the Tools window's edited copy. Pins and setups are changed live, so those are kept.</summary>
    public void SaveTools(ToolsSettings edited)
    {
        edited.Pinned = Tools.Pinned;
        edited.Setups = Tools.Setups;
        if (edited.Setups.All(s => s.Name != edited.StartupSetup)) edited.StartupSetup = "";
        _settings.Tools = edited;
        _settings.Save();
        ApplyTools();
        StatusMessage = "Tools saved.";
    }

    public void UnpinAll()
    {
        Tools.Pinned.Clear();
        _settings.Save();
        StatusMessage = "All windows unpinned.";
    }

    public string DiscordStatus => _discord.Status;
    public bool AutoRejoinEnabled => _settings.AutoRejoin;

    // ---------------- Hotkeys ----------------

    /// <summary>Called once the main window has a handle.</summary>
    public void AttachHotkeys(IntPtr hwnd)
    {
        _hotkeys = new HotkeyService(hwnd);
        _hotkeys.Pressed += OnHotkey;
        RegisterHotkeys();
    }

    private void RegisterHotkeys()
    {
        if (_hotkeys is null) return;
        var failed = _hotkeys.Register(Tools.Hotkeys.Where(h => !string.IsNullOrWhiteSpace(h.Keys)).Select(h => (h.Action, h.Keys)));
        Macro.IgnoreKeys(HotkeyService.KeyCodes(Tools.Hotkeys.Where(h => h.Action.Contains("macro")).Select(h => h.Keys)));
        if (failed.Count > 0) Log(ActivityLevel.Warn, "Hotkeys already used by another app: " + string.Join(", ", failed));
    }

    private void OnHotkey(string action)
    {
        switch (action)
        {
            case "Show Alt Manager":
                if (Application.Current.MainWindow is { } w)
                {
                    if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                    w.Show();
                    w.Activate();
                }
                break;
            case "Arrange windows": ArrangeWindows(); break;
            case "Minimize all clients": MinimizeAll(); break;
            case "Launch selected": if (LaunchSelectedCommand.CanExecute(null)) LaunchSelectedCommand.Execute(null); break;
            case "Close all clients": PanicClose(); break;
            case "Toggle Max alts": MaxAltsOn = !MaxAltsOn; StatusMessage = $"Max alts {(MaxAltsOn ? "on" : "off")}."; break;
            case "Toggle anti-AFK": AntiAfkOn = !AntiAfkOn; StatusMessage = $"Anti-AFK {(AntiAfkOn ? "on" : "off")}."; break;
            case "Record / stop macro": ToggleMacroRecord(); break;
            case "Play / stop macro": ToggleMacroPlay(); break;
        }
    }

    // ---------------- Window commands ----------------

    [RelayCommand]
    private void MinimizeAll()
    {
        var n = 0;
        foreach (var p in ActiveProcesses) { if (p.MainWindow is var h && h != IntPtr.Zero) { RobloxWindows.Minimize(h); n++; } }
        StatusMessage = n == 0 ? "No game windows to minimize." : $"Minimized {n} client(s).";
    }

    [RelayCommand]
    private void TogglePin(TrackedProcess? p)
    {
        if (p is null) return;
        if (Tools.Pinned.Remove(p.AccountId))
        {
            StatusMessage = $"{LabelFor(p.AccountId)}: unpinned.";
        }
        else if (RobloxWindows.Rect(p.MainWindow) is { } r)
        {
            Tools.Pinned[p.AccountId] = new SavedWindowBounds { X = r.Left, Y = r.Top, Width = r.Width, Height = r.Height };
            StatusMessage = $"{LabelFor(p.AccountId)}: pinned — its window stays at this spot and size.";
        }
        else { StatusMessage = "That client has no window yet."; return; }
        _settings.Save();
    }

    public bool IsPinned(Guid accountId) => Tools.Pinned.ContainsKey(accountId);

    public void FocusClient(TrackedProcess p)
    {
        var h = p.MainWindow;
        if (h == IntPtr.Zero) return;
        if (User32.IsIconic(h)) User32.ShowWindow(h, User32.SW_RESTORE);
        User32.Focus(h);
    }

    public void MinimizeClient(TrackedProcess p) => RobloxWindows.Minimize(p.MainWindow);

    public void CloseClient(TrackedProcess p) => KillProcess(p);

    // ---------------- RAM ----------------

    [RelayCommand]
    private void CleanRam()
    {
        var before = MemoryCleaner.Status();
        var n = MemoryCleaner.TrimAll(ActiveProcesses.Select(p => p.Pid));
        var after = ApplyRamText();
        StatusMessage = $"Trimmed {n} client(s): {before.AvailableMb / 1024.0:0.0} → {after.AvailableMb / 1024.0:0.0} GB free.";
    }

    [RelayCommand]
    private async Task DeepCleanAsync()
    {
        CleanRam();
        var before = MemoryCleaner.Status();
        bool ok;
        if (MemoryCleaner.IsAdmin()) ok = MemoryCleaner.DeepClean();
        else
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            StatusMessage = "Deep clean needs admin — approve the Windows prompt.";
            ok = await Task.Run(() => MemoryCleaner.RunElevatedClean(exe));
        }
        var after = ApplyRamText();
        StatusMessage = ok
            ? $"Deep clean done: {before.AvailableMb / 1024.0:0.0} → {after.AvailableMb / 1024.0:0.0} GB free."
            : "Deep clean didn't run (the admin prompt was declined or failed).";
    }

    // ---------------- Follow player / random server ----------------

    [RelayCommand]
    private async Task FollowPlayerAsync()
    {
        var name = _dialogs.Prompt("Follow player", "Roblox username to follow (their joins must be on for you):");
        if (string.IsNullOrWhiteSpace(name)) return;
        string? cookie = null;
        foreach (var a in Accounts) { try { cookie = _vault.GetCookie(a.Model); break; } catch { } }
        if (cookie is null) { StatusMessage = "Add an account first (needed to look up presence)."; return; }

        await RunBusy($"Looking up {name}…", async () =>
        {
            var user = await _api.ResolveUserAsync(name.Trim());
            if (user is null) { StatusMessage = $"No Roblox user named '{name}'."; return; }
            var map = await _api.GetPresencesAsync(new[] { user.Id }, cookie);
            if (!map.TryGetValue(user.Id, out var p) || p.Type != PresenceType.InGame)
            {
                StatusMessage = $"{user.Name} isn't in a game right now.";
                return;
            }
            if (p.PlaceId is not { } placeId || placeId <= 0)
            {
                StatusMessage = $"{user.Name} is in a game, but their privacy settings hide which one.";
                return;
            }
            PlaceInput = placeId.ToString();
            JobId = p.GameId ?? "";
            StatusMessage = string.IsNullOrEmpty(p.GameId)
                ? $"{user.Name} is in place {placeId} (server hidden). Launch to join that game."
                : $"Following {user.Name}: server set. Select accounts and press Launch.";
        });
    }

    [RelayCommand]
    private async Task RandomServerAsync()
    {
        if (LaunchTargetParser.Parse(PlaceInput).PlaceId is not { } placeId || placeId <= 0)
        {
            StatusMessage = "Enter a game / place ID in the Launch panel first.";
            return;
        }
        await RunBusy("Finding a server…", async () =>
        {
            var pool = new List<ServerInfo>();
            string? cursor = null;
            for (var page = 0; page < 3; page++)
            {
                var (servers, next) = await _api.GetServersAsync(placeId, cursor);
                pool.AddRange(servers.Where(s => s.Playing < s.MaxPlayers));
                if (string.IsNullOrEmpty(next) || pool.Count >= 200) break;
                cursor = next;
            }
            if (pool.Count == 0) { StatusMessage = "No open public servers found."; return; }
            var pick = pool[Random.Shared.Next(pool.Count)];
            JobId = pick.Id;
            StatusMessage = $"Random server: {pick.Playing}/{pick.MaxPlayers} players. Press Launch to join.";
        });
    }

    // ---------------- Client limit ----------------

    /// <summary>
    /// Applies the max-clients limit before a launch. With auto-close, the oldest clients are closed to
    /// make room; otherwise only as many accounts as fit are launched.
    /// </summary>
    private List<AccountViewModel> ApplyClientLimit(List<AccountViewModel> accounts)
    {
        var max = Tools.MaxClients;
        if (max <= 0) return accounts;
        var launching = accounts.Where(a => !_tracker.IsRunning(a.Model.Id)).ToList();
        var running = ActiveProcesses.Where(p => accounts.All(a => a.Model.Id != p.AccountId)).OrderBy(p => p.Started).ToList();
        var over = running.Count + launching.Count - max;
        if (over <= 0) return accounts;

        if (Tools.AutoClosePrevious)
        {
            foreach (var p in running.Take(over))
            {
                _userClosed.Add(p.AccountId);
                _tracker.Kill(p.Pid);
            }
            Log(ActivityLevel.Info, $"Client limit {max}: closed {Math.Min(over, running.Count)} older client(s).");
            if (launching.Count <= max) return accounts;
            return accounts.Take(max).ToList();
        }

        var room = Math.Max(0, max - running.Count);
        Log(ActivityLevel.Warn, $"Client limit {max}: launching {room} of {launching.Count}.");
        return accounts.Where(a => _tracker.IsRunning(a.Model.Id)).Concat(launching.Take(room)).ToList();
    }

    // ---------------- Crash breaker ----------------

    /// <summary>
    /// Decides whether to rejoin a client that closed on its own. After too many restarts in a short
    /// time the breaker trips and auto-rejoin pauses for that account for the cooldown.
    /// </summary>
    private bool CrashBreakerAllows(AccountViewModel vm)
    {
        var now = DateTime.UtcNow;
        if (_breakerUntil.TryGetValue(vm.Model.Id, out var until) && now < until)
        {
            StatusMessage = $"{vm.Label}: crash breaker active until {until.ToLocalTime():HH:mm}.";
            return false;
        }
        var b = _rejoinBudget.TryGetValue(vm.Model.Id, out var x) ? x : (Count: 0, Since: now);
        if ((now - b.Since).TotalMinutes > Math.Max(1, Tools.RejoinWindowMinutes)) b = (0, now);
        if (b.Count >= Math.Max(1, Tools.RejoinMaxAttempts))
        {
            var cool = Math.Max(1, Tools.RejoinCooldownMinutes);
            _breakerUntil[vm.Model.Id] = now.AddMinutes(cool);
            _rejoinBudget.Remove(vm.Model.Id);
            StatusMessage = $"{vm.Label}: crashed {b.Count} times — auto-rejoin paused for {cool} min.";
            Log(ActivityLevel.Warn, StatusMessage);
            if (Tools.WebhookOnBreaker)
                Webhook("Crash breaker tripped", $"{vm.Label} crashed {b.Count} times in {Tools.RejoinWindowMinutes} min. Auto-rejoin paused for {cool} min.", 0xE5534B);
            return false;
        }
        _rejoinBudget[vm.Model.Id] = (b.Count + 1, b.Since);
        return true;
    }

    private void NotifyCrash(Guid accountId)
    {
        if (Tools.WebhookOnCrash && _lastTarget.ContainsKey(accountId))
            Webhook("Client closed unexpectedly", $"{LabelFor(accountId)}'s Roblox client closed." +
                                                 (_settings.AutoRejoin ? " Auto-rejoin will try again." : ""), 0xF0B232);
    }

    // ---------------- Webhooks ----------------

    private void Webhook(string title, string text, int color = 0x35B779)
    {
        var url = Tools.WebhookUrl?.Trim();
        if (string.IsNullOrEmpty(url)) return;
        _ = Task.Run(async () =>
        {
            var err = await WebhookService.SendAsync(url, title, text, color);
            if (err is not null) Log(ActivityLevel.Warn, "Discord webhook failed: " + err);
        });
    }

    private string LabelFor(Guid accountId) =>
        Accounts.FirstOrDefault(a => a.Model.Id == accountId)?.Label ?? "An account";

    // ---------------- Setups ----------------

    /// <summary>Snapshots the running clients (game, server, window spot) and the tool switches.</summary>
    public SavedSetup? SaveSetup(string name)
    {
        var procs = ActiveProcesses.ToList();
        if (procs.Count == 0) { StatusMessage = "Launch the clients you want in the setup first."; return null; }
        var setup = new SavedSetup
        {
            Name = name,
            MaxAlts = Tools.MaxAlts,
            Borderless = Tools.Borderless,
            AntiAfk = Tools.AntiAfk,
            LowResource = _settings.OptimizeClient,
            Graphics = System.Text.Json.JsonSerializer.Deserialize<GraphicsSettings>(
                System.Text.Json.JsonSerializer.Serialize(Tools.Graphics))!,
        };
        foreach (var p in procs)
        {
            if (!_lastTarget.TryGetValue(p.AccountId, out var t)) continue;
            setup.Accounts.Add(new SetupAccount
            {
                AccountId = p.AccountId,
                Label = Accounts.FirstOrDefault(a => a.Model.Id == p.AccountId)?.Model.Username ?? p.AccountLabel,
                PlaceId = t.PlaceId,
                JobId = t.JobId,
                LinkCode = t.PrivateServerLinkCode,
                Bounds = GameWindowBounds.Capture(p.MainWindow),
            });
        }
        if (setup.Accounts.Count == 0) { StatusMessage = "None of the running clients were launched from here."; return null; }
        Tools.Setups.RemoveAll(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Tools.Setups.Add(setup);
        _settings.Save();
        StatusMessage = $"Saved setup '{name}' ({setup.Accounts.Count} clients).";
        return setup;
    }

    /// <summary>Applies a setup's switches and launches its accounts into their games and window spots.</summary>
    public async Task RunSetupAsync(SavedSetup setup)
    {
        if (IsLaunching) { StatusMessage = "A launch is already running."; return; }
        Tools.MaxAlts = setup.MaxAlts;
        Tools.Borderless = setup.Borderless;
        Tools.AntiAfk = setup.AntiAfk;
        Tools.Graphics = setup.Graphics;
        _settings.OptimizeClient = setup.LowResource;
        _settings.Save();
        ApplyTools();

        var accounts = new List<AccountViewModel>();
        var targets = new Dictionary<Guid, LaunchTarget>();
        foreach (var sa in setup.Accounts)
        {
            var vm = Accounts.FirstOrDefault(a => a.Model.Id == sa.AccountId);
            if (vm is null || _tracker.IsRunning(sa.AccountId)) continue;
            if (sa.Bounds is not null) vm.Model.WindowBounds = sa.Bounds;
            accounts.Add(vm);
            targets[sa.AccountId] = new LaunchTarget(sa.PlaceId, JobId: sa.JobId, PrivateServerLinkCode: sa.LinkCode);
        }
        if (accounts.Count == 0) { StatusMessage = $"Setup '{setup.Name}': everything is already running."; return; }
        if (!_guard.IsActive && !_guard.Acquire() && accounts.Count > 1)
        {
            StatusMessage = "Multi-instance mode is off — close all Roblox windows first.";
            OnPropertyChanged(nameof(MultiInstanceActive));
            return;
        }
        Log(ActivityLevel.Info, $"Running setup '{setup.Name}': {accounts.Count} client(s).");
        StatusMessage = $"Running setup '{setup.Name}'…";
        await RunApiBatchAsync(accounts, targets);
    }

    public void DeleteSetup(string name)
    {
        Tools.Setups.RemoveAll(s => s.Name == name);
        if (Tools.StartupSetup == name) Tools.StartupSetup = "";
        _settings.Save();
    }

    private async Task RunStartupSetupAsync()
    {
        if (string.IsNullOrEmpty(Tools.StartupSetup)) return;
        var s = Tools.Setups.FirstOrDefault(x => x.Name == Tools.StartupSetup);
        if (s is not null) await RunSetupAsync(s);
    }

    // ---------------- Macro ----------------

    public void ToggleMacroRecord()
    {
        if (Macro.IsPlaying) return;
        if (Macro.IsRecording) Macro.StopRecording(); else Macro.StartRecording();
    }

    public void ToggleMacroPlay()
    {
        if (Macro.IsRecording) return;
        if (Macro.IsPlaying) Macro.StopPlaying(); else Macro.Play(Tools.MacroLoops, Tools.MacroSpeed);
    }

    private void UpdateMacroStatus()
    {
        MacroStatus = Macro.IsRecording ? "● Recording…"
            : Macro.IsPlaying ? "▶ Playing…"
            : Macro.Events.Count == 0 ? "No macro recorded"
            : $"{Macro.Events.Count} inputs · {Macro.Length.TotalSeconds:0.0}s";
    }

    public static string MacroFolder
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AltManager", "Macros");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    // ---------------- Windows ----------------

    [RelayCommand]
    private void OpenTools() => _dialogs.OpenTools(this);

    [RelayCommand]
    private void OpenMonitor() => _dialogs.OpenMonitor(this);
}
