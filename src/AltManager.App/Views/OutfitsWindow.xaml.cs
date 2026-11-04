using System.Windows;
using AltManager.Core.Roblox;

namespace AltManager.App.Views;

/// <summary>Lists an account's saved outfits; double-clicking one makes the account wear it.</summary>
public partial class OutfitsWindow : Window
{
    public sealed record Item(long Id, string Name);

    private readonly long _userId;
    private readonly string _cookie;
    private readonly RobloxApiClient _api;

    public OutfitsWindow(long userId, string label, string cookie, RobloxApiClient api)
    {
        _userId = userId;
        _cookie = cookie;
        _api = api;
        InitializeComponent();
        HeaderText.Text = $"Outfits — {label}";
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        HeaderText.Text = "Loading outfits…";
        var outfits = await _api.GetOutfitsAsync(_userId, _cookie);
        List.ItemsSource = outfits.Select(o => new Item(o.Id, o.Name)).ToList();
        HeaderText.Text = outfits.Count == 0 ? "No saved outfits" : $"{outfits.Count} outfit(s)";
    }

    private async void List_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (List.SelectedItem is not Item it) return;
        HeaderText.Text = $"Wearing “{it.Name}”…";
        var (ok, msg) = await _api.WearOutfitAsync(it.Id, _cookie);
        MessageBox.Show(this, ok ? $"Now wearing “{it.Name}”." : $"Couldn't wear it: {msg}",
            "Outfits", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        if (ok) Close();
        else HeaderText.Text = "Pick an outfit";
    }
}
