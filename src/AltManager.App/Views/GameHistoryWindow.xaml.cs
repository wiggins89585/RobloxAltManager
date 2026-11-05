using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AltManager.Core;
using AltManager.Core.Roblox;

namespace AltManager.App.Views;

/// <summary>
/// Grid of previously launched games (newest first) with icons, searchable, and a right-click
/// menu to copy place details or push a game back into the Launch panel.
/// </summary>
public partial class GameHistoryWindow : Window
{
    public sealed class GameItem : INotifyPropertyChanged
    {
        public long PlaceId { get; init; }
        public string Name { get; set; } = "";
        public string Subtitle { get; init; } = "";
        public IReadOnlyList<PlayRow> Plays { get; init; } = Array.Empty<PlayRow>();
        private string? _iconUrl;
        public string? IconUrl { get => _iconUrl; set { _iconUrl = value; PropertyChanged?.Invoke(this, new(nameof(IconUrl))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public sealed record PlayRow(string Name, string Detail);

    private readonly ObservableCollection<GameItem> _all = new();      // launch history
    private readonly List<GameItem> _online = new();                   // current Roblox search results
    private readonly ObservableCollection<GameItem> _shown = new();
    private readonly Action<long> _onUse;
    private readonly RobloxApiClient _api;
    private readonly string? _cookie;
    private readonly System.Windows.Threading.DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private int _searchSeq; // guards against stale search results

    public GameHistoryWindow(IReadOnlyList<GameHistoryEntry> history, RobloxApiClient api, string? cookie, Action<long> onUse)
    {
        _onUse = onUse;
        _api = api;
        _cookie = cookie;
        InitializeComponent();
        Items.ItemsSource = _shown;

        foreach (var g in history.OrderByDescending(h => h.LastPlayed))
            _all.Add(new GameItem
            {
                PlaceId = g.PlaceId,
                Name = string.IsNullOrWhiteSpace(g.Name) ? $"Place {g.PlaceId}" : g.Name,
                Subtitle = g.Plays.Count > 0
                    ? $"{Span(g.Plays.Sum(p => p.Seconds))} · {Ago(g.LastPlayed)}"
                    : $"{g.PlaceId} · {Ago(g.LastPlayed)}",
                Plays = g.Plays.OrderByDescending(p => p.Seconds)
                    .Select(p => new PlayRow(p.Username,
                        $"{Span(p.Seconds)} · {p.Sessions} session{(p.Sessions == 1 ? "" : "s")} · {Ago(p.LastPlayed)}"))
                    .ToList()
            });

        ApplyFilter("");
        _searchDebounce.Tick += async (_, _) => { _searchDebounce.Stop(); await SearchRobloxAsync(SearchBox.Text.Trim()); };
        Loaded += async (_, _) => await LoadMetaAsync(history, api, cookie);
    }

    private async Task LoadMetaAsync(IReadOnlyList<GameHistoryEntry> history, RobloxApiClient api, string? cookie)
    {
        var ids = _all.Select(i => i.PlaceId).ToList();
        if (ids.Count == 0) return;

        var icons = await api.GetPlaceIconsAsync(ids);
        foreach (var item in _all)
            if (icons.TryGetValue(item.PlaceId, out var url)) item.IconUrl = url;

        // Names only where we don't already have one and a cookie is available.
        if (cookie is not null)
        {
            var missing = _all.Where(i => i.Name.StartsWith("Place ")).Select(i => i.PlaceId).ToList();
            if (missing.Count > 0)
            {
                var names = await api.GetPlaceNamesAsync(missing, cookie);
                foreach (var item in _all)
                    if (names.TryGetValue(item.PlaceId, out var n))
                    {
                        item.Name = n;
                        var h = history.FirstOrDefault(x => x.PlaceId == item.PlaceId);
                        if (h is not null) h.Name = n; // persisted by the caller's settings on next save
                    }
                ApplyFilter(SearchBox.Text); // re-render names
            }
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter(SearchBox.Text);                 // filter history immediately
        _online.Clear();
        _searchDebounce.Stop();
        if (SearchBox.Text.Trim().Length >= 2) _searchDebounce.Start();  // then search Roblox
    }

    private void ApplyFilter(string q)
    {
        q = q.Trim();
        _shown.Clear();
        var shownIds = new HashSet<long>();
        foreach (var i in _all)
            if (q.Length == 0 || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || i.PlaceId.ToString().Contains(q))
            { _shown.Add(i); shownIds.Add(i.PlaceId); }
        foreach (var i in _online)                    // Roblox results not already in history
            if (shownIds.Add(i.PlaceId)) _shown.Add(i);
        CountText.Text = $"{_shown.Count} game(s)";
        EmptyText.Visibility = _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task SearchRobloxAsync(string query)
    {
        if (query.Length < 2) return;
        var seq = ++_searchSeq;
        var results = await _api.SearchGamesAsync(query);
        if (seq != _searchSeq) return;               // a newer search superseded this one

        _online.Clear();
        foreach (var r in results.Take(40))
            _online.Add(new GameItem
            {
                PlaceId = r.PlaceId,
                Name = r.Name,
                Subtitle = r.Players is { } p ? $"{p:N0} playing · Roblox" : "Roblox"
            });
        ApplyFilter(SearchBox.Text);

        var icons = await _api.GetPlaceIconsAsync(_online.Select(i => i.PlaceId).ToList());
        if (seq != _searchSeq) return;
        foreach (var item in _online)
            if (icons.TryGetValue(item.PlaceId, out var url)) item.IconUrl = url;
    }

    private static string Span(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{Math.Max(1, (int)t.TotalMinutes)}m";
    }

    private void Card_Enter(object sender, MouseEventArgs e)
    {
        if (ItemOf(sender) is not { } g) return;
        PlaysTitle.Text = g.Plays.Count > 0 ? g.Name : $"{g.Name} — no recorded play time yet";
        PlaysList.ItemsSource = g.Plays;
    }

    private static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.UtcNow - t;
        return d < TimeSpan.FromMinutes(1) ? "just now"
            : d < TimeSpan.FromHours(1) ? $"{(int)d.TotalMinutes}m ago"
            : d < TimeSpan.FromDays(1) ? $"{(int)d.TotalHours}h ago"
            : $"{(int)d.TotalDays}d ago";
    }

    private static GameItem? ItemOf(object sender) => sender switch
    {
        FrameworkElement { DataContext: GameItem g } => g,
        _ => null
    };

    private void Copy(string text)
    {
        try { Clipboard.SetText(text); } catch (System.Runtime.InteropServices.COMException) { }
    }

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } g) Copy($"https://www.roblox.com/games/{g.PlaceId}");
    }
    private void CopyId_Click(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } g) Copy(g.PlaceId.ToString()); }
    private void CopyName_Click(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } g) Copy(g.Name); }
    private void CopyDetails_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } g)
            Copy($"{g.Name}\nPlaceId: {g.PlaceId}\nhttps://www.roblox.com/games/{g.PlaceId}");
    }

    private void Use_Click(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } g) { _onUse(g.PlaceId); Close(); } }
    private void Card_Click(object sender, MouseButtonEventArgs e) { if (ItemOf(sender) is { } g) { _onUse(g.PlaceId); Close(); } }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } g) return;
        _all.Remove(g);
        _shown.Remove(g);
        RemovedPlaceIds.Add(g.PlaceId);
        CountText.Text = $"{_shown.Count} game(s)";
    }

    /// <summary>Place ids the user removed; the caller prunes them from settings when the window closes.</summary>
    public List<long> RemovedPlaceIds { get; } = new();
}
