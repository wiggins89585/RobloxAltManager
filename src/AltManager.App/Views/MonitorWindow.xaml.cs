using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AltManager.App.ViewModels;
using AltManager.Core.Launch;
using AltManager.Core.Native;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AltManager.App.Views;

/// <summary>Live view of every running client: preview, CPU and RAM with history, and quick actions.</summary>
public partial class MonitorWindow : Window
{
    public sealed partial class Card : ObservableObject
    {
        public TrackedProcess Process { get; }
        public Card(TrackedProcess p) => Process = p;

        [ObservableProperty] private string _label = "";
        [ObservableProperty] private string _uptime = "";
        [ObservableProperty] private string _cpuText = "–";
        [ObservableProperty] private string _memText = "–";
        [ObservableProperty] private PointCollection _cpuLine = new();
        [ObservableProperty] private PointCollection _memLine = new();
        [ObservableProperty] private ImageSource? _preview;
        [ObservableProperty] private string _previewNote = "Waiting for window…";
        [ObservableProperty] private string _pinText = "Pin";
        [ObservableProperty] private Visibility _pinnedVisibility = Visibility.Collapsed;
    }

    private const double LineWidth = 135, LineHeight = 28;
    private readonly MainViewModel _vm;
    private readonly ObservableCollection<Card> _cards = new();
    private int _tick;

    public MonitorWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        Cards.ItemsSource = _cards;
        _vm.UsageUpdated += Refresh;
        Closed += (_, _) => _vm.UsageUpdated -= Refresh;
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(Refresh); return; }
        var procs = _vm.ActiveProcesses.ToList();

        foreach (var gone in _cards.Where(c => procs.All(p => p.Pid != c.Process.Pid)).ToList()) _cards.Remove(gone);
        foreach (var p in procs.Where(p => _cards.All(c => c.Process.Pid != p.Pid))) _cards.Add(new Card(p));

        var previews = PreviewsBox.IsChecked == true && IsVisible && WindowState != WindowState.Minimized && _tick++ % 2 == 0;
        double totalCpu = 0, totalMem = 0;
        foreach (var c in _cards)
        {
            var p = c.Process;
            c.Label = _vm.Accounts.FirstOrDefault(a => a.Model.Id == p.AccountId)?.Label ?? p.AccountLabel;
            var up = DateTimeOffset.UtcNow - p.Started;
            c.Uptime = up.TotalHours >= 1 ? $"{(int)up.TotalHours}h {up.Minutes}m" : $"{up.Minutes}m {up.Seconds}s";
            var pinned = _vm.IsPinned(p.AccountId);
            c.PinText = pinned ? "Unpin" : "Pin";
            c.PinnedVisibility = pinned ? Visibility.Visible : Visibility.Collapsed;

            if (_vm.Usage.Samples.TryGetValue(p.Pid, out var s))
            {
                totalCpu += s.Cpu;
                totalMem += s.MemoryMb;
                c.CpuText = $"{s.Cpu:0.0}%";
                c.MemText = s.MemoryMb >= 1024 ? $"{s.MemoryMb / 1024:0.00} GB" : $"{s.MemoryMb:0} MB";
                c.CpuLine = Line(s.CpuHistory, Math.Max(10, s.CpuHistory.DefaultIfEmpty(0).Max()));
                c.MemLine = Line(s.MemHistory, Math.Max(100, s.MemHistory.DefaultIfEmpty(0).Max()));
            }

            if (!previews) continue;
            var h = p.MainWindow;
            if (h == IntPtr.Zero) { c.PreviewNote = "Waiting for window…"; c.Preview = null; continue; }
            if (User32.IsIconic(h)) { c.PreviewNote = "Minimized"; c.Preview = null; continue; }
            if (RobloxWindows.Capture(h, 300) is { } shot)
            {
                var bmp = BitmapSource.Create(shot.Width, shot.Height, 96, 96, PixelFormats.Bgr32, null, shot.Pixels, shot.Width * 4);
                bmp.Freeze();
                c.Preview = bmp;
                c.PreviewNote = "";
            }
        }

        EmptyText.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = _cards.Count == 0 ? "No clients" :
            $"{_cards.Count} client{(_cards.Count == 1 ? "" : "s")} · CPU {totalCpu:0}% · RAM {totalMem / 1024:0.0} GB";
        RamText.Text = _vm.MemoryText;
    }

    private static PointCollection Line(IEnumerable<double> values, double max)
    {
        var list = values.ToList();
        var pts = new PointCollection();
        if (list.Count == 0) return pts;
        var step = LineWidth / Math.Max(1, Services.UsageMonitor.HistoryLength - 1);
        var x0 = LineWidth - step * (list.Count - 1);
        for (var i = 0; i < list.Count; i++)
            pts.Add(new Point(x0 + i * step, LineHeight - list[i] / max * (LineHeight - 2) + 1));
        pts.Freeze();
        return pts;
    }

    private Card? CardOf(object sender) => (sender as FrameworkElement)?.DataContext as Card;

    private void Focus_Click(object sender, RoutedEventArgs e) { if (CardOf(sender) is { } c) _vm.FocusClient(c.Process); }
    private void Minimize_Click(object sender, RoutedEventArgs e) { if (CardOf(sender) is { } c) _vm.MinimizeClient(c.Process); }
    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } c) return;
        _vm.TogglePinCommand.Execute(c.Process);
        Refresh();
    }
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } c) return;
        if (MessageBox.Show(this, $"Close {c.Label}'s Roblox client?", "Monitor", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            _vm.CloseClient(c.Process);
    }

    private void Arrange_Click(object sender, RoutedEventArgs e) => _vm.ArrangeWindowsCommand.Execute(null);
    private void MinimizeAll_Click(object sender, RoutedEventArgs e) => _vm.MinimizeAllCommand.Execute(null);
    private void CleanRam_Click(object sender, RoutedEventArgs e) => _vm.CleanRamCommand.Execute(null);
}
