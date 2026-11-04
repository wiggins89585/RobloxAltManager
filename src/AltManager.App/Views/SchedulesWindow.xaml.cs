using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using AltManager.Core;

namespace AltManager.App.Views;

public partial class SchedulesWindow : Window
{
    /// <summary>Bindable row over a LaunchSchedule with display helpers.</summary>
    public sealed class Row
    {
        public LaunchSchedule Model { get; }
        public Row(LaunchSchedule m) => Model = m;
        public string Time => Model.Time;
        public bool Enabled { get => Model.Enabled; set => Model.Enabled = value; }
        public string GroupLabel => string.IsNullOrEmpty(Model.Group) ? "All accounts" : Model.Group;
        public string Summary => $"→ {Model.Game}" + (Model.Daily ? "  · daily" : "  · once");
    }

    private readonly List<LaunchSchedule> _schedules;
    private readonly Action _save;
    private readonly ObservableCollection<Row> _rows = new();

    public SchedulesWindow(List<LaunchSchedule> schedules, IEnumerable<string> groups, Action save)
    {
        _schedules = schedules;
        _save = save;
        InitializeComponent();
        GroupBox.ItemsSource = groups;
        List.ItemsSource = _rows;
        foreach (var s in _schedules) _rows.Add(new Row(s));
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var time = TimeBox.Text.Trim();
        if (!Regex.IsMatch(time, @"^([01]?\d|2[0-3]):[0-5]\d$")) { MessageBox.Show(this, "Enter a time as HH:mm (24-hour)."); return; }
        if (string.IsNullOrWhiteSpace(GameBox.Text)) { MessageBox.Show(this, "Enter a game (place ID or link)."); return; }

        var s = new LaunchSchedule
        {
            Group = GroupBox.Text?.Trim() ?? "",
            Game = GameBox.Text.Trim(),
            Time = time,
            Daily = DailyBox.IsChecked == true
        };
        _schedules.Add(s);
        _rows.Add(new Row(s));
        _save();
        GameBox.Clear();
    }

    private void Toggle_Click(object sender, RoutedEventArgs e) => _save();

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Row row })
        {
            _schedules.Remove(row.Model);
            _rows.Remove(row);
            _save();
        }
    }
}
