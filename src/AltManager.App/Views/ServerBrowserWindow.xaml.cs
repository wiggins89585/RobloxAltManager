using System.Collections.ObjectModel;
using System.Windows;
using AltManager.Core.Models;
using AltManager.Core.Roblox;

namespace AltManager.App.Views;

/// <summary>
/// Lists public servers for a place. Picking one calls back with its job ID so the main view
/// launches the selected accounts into that server.
/// </summary>
public partial class ServerBrowserWindow : Window
{
    public sealed record Row(string Id, int Playing, int MaxPlayers, double? Fps, int? Ping)
    {
        public string Players => $"{Playing}/{MaxPlayers}";
        public string Right => (Ping is { } p ? $"{p} ms" : "") + (Fps is { } f ? $"  {f:0} fps" : "");
    }

    private readonly long _placeId;
    private readonly RobloxApiClient _api;
    private readonly Action<string> _onJoin;
    private readonly ObservableCollection<Row> _rows = new();
    private List<ServerInfo> _servers = new();

    public ServerBrowserWindow(long placeId, RobloxApiClient api, Action<string> onJoin)
    {
        _placeId = placeId;
        _api = api;
        _onJoin = onJoin;
        InitializeComponent();
        ServerList.ItemsSource = _rows;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        HeaderText.Text = "Loading servers…";
        var (servers, _) = await _api.GetServersAsync(_placeId);
        _servers = servers;
        _rows.Clear();
        foreach (var s in servers.OrderByDescending(s => s.Playing))
            _rows.Add(new Row(s.Id, s.Playing, s.MaxPlayers, s.Fps, s.Ping));
        HeaderText.Text = servers.Count == 0 ? "No servers found" : $"{servers.Count} server(s) for place {_placeId}";
    }

    private void Join(string? jobId)
    {
        if (string.IsNullOrEmpty(jobId)) return;
        _onJoin(jobId);
        Close();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void ServerList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ServerList.SelectedItem is Row r) Join(r.Id);
    }

    private void JoinEmptiest_Click(object sender, RoutedEventArgs e) =>
        Join(_servers.Where(s => s.Playing < s.MaxPlayers).OrderBy(s => s.Playing).FirstOrDefault()?.Id);

    private void JoinFullest_Click(object sender, RoutedEventArgs e) =>
        Join(_servers.Where(s => s.Playing < s.MaxPlayers).OrderByDescending(s => s.Playing).FirstOrDefault()?.Id);

    private void JoinRandom_Click(object sender, RoutedEventArgs e)
    {
        var open = _servers.Where(s => s.Playing < s.MaxPlayers).ToList();
        if (open.Count > 0) Join(open[Random.Shared.Next(open.Count)].Id);
    }
}
