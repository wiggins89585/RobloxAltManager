using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AltManager.App.Services;
using AltManager.App.ViewModels;
using AltManager.Core;

namespace AltManager.App.Views;

/// <summary>
/// Tools settings. Edits a copy; Save puts it back. Buttons that do something right now (RAM clean,
/// setups, macro) act straight away.
/// </summary>
public partial class ToolsWindow : Window
{
    public sealed class SetupRow
    {
        public SavedSetup Model { get; }
        public SetupRow(SavedSetup m) => Model = m;
        public string Name => Model.Name;
        public string Summary =>
            $"{Model.Accounts.Count} client(s): {string.Join(", ", Model.Accounts.Select(a => a.Label).Take(6))}" +
            (Model.Accounts.Count > 6 ? "…" : "") +
            (Model.MaxAlts ? " · Max alts" : "") + (Model.Borderless ? " · borderless" : "") + (Model.AntiAfk ? " · anti-AFK" : "");
    }

    private readonly MainViewModel _vm;
    private readonly ToolsSettings _edit;
    private bool _loadingSetups;

    public ToolsWindow(MainViewModel vm)
    {
        _vm = vm;
        _edit = JsonSerializer.Deserialize<ToolsSettings>(JsonSerializer.Serialize(vm.Tools))!;
        InitializeComponent();

        QualityBox.Items.Add(new ComboBoxItem { Content = "Auto" });
        for (var i = 1; i <= 10; i++) QualityBox.Items.Add(new ComboBoxItem { Content = i.ToString() });

        DataContext = _edit;
        HotkeyList.ItemsSource = _edit.Hotkeys;
        RefreshSetups();
        RefreshLive();

        _vm.PropertyChanged += OnVmChanged;
        _vm.Macro.StateChanged += OnMacroChanged;
        Closed += (_, _) => { _vm.PropertyChanged -= OnVmChanged; _vm.Macro.StateChanged -= OnMacroChanged; };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.MemoryText) or nameof(MainViewModel.MacroStatus) or nameof(MainViewModel.StatusMessage) or nameof(MainViewModel.DiscordStatus))
            RefreshLive();
    }

    private void OnMacroChanged() => Dispatcher.BeginInvoke(RefreshLive);

    private void RefreshLive()
    {
        RamText.Text = _vm.MemoryText;
        MacroText.Text = _vm.MacroStatus;
        RecordBtn.Content = _vm.Macro.IsRecording ? "■ Stop recording" : "● Record";
        PlayBtn.Content = _vm.Macro.IsPlaying ? "■ Stop" : "▶ Play";
        PinnedText.Text = _vm.Tools.Pinned.Count == 0 ? "No windows pinned." : $"{_vm.Tools.Pinned.Count} window(s) pinned.";
        RpcStatus.Text = "Status: " + _vm.DiscordStatus;
        RejoinState.Text = "Auto-rejoin is " + (_vm.AutoRejoinEnabled ? "on." : "off — turn it on in Settings for the breaker to matter.");
        FooterText.Text = _vm.StatusMessage;
    }

    // ---------------- Save / cancel ----------------

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Commit a text box that still has focus.
        if (Keyboard.FocusedElement is TextBox tb) tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (!string.IsNullOrWhiteSpace(_edit.WebhookUrl) && !WebhookService.IsValidUrl(_edit.WebhookUrl))
        {
            MessageBox.Show(this, "The webhook URL should look like https://discord.com/api/webhooks/…", "Tools");
            return;
        }
        if (_edit.DiscordRpc && !ulong.TryParse(_edit.DiscordAppId.Trim(), out _))
        {
            MessageBox.Show(this, "Rich presence needs your Discord Application ID (a long number).", "Tools");
            return;
        }
        _edit.MacroSpeed = Math.Clamp(_edit.MacroSpeed, 0.1, 10);
        _edit.RamCleanPercent = Math.Clamp(_edit.RamCleanPercent, 30, 99);
        _edit.AntiAfkMinutes = Math.Clamp(_edit.AntiAfkMinutes, 1, 19);
        _vm.SaveTools(_edit);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    // ---------------- Performance ----------------

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        var g = _edit.Graphics;
        g.Enabled = true;
        (g.FpsCap, g.Quality, g.AntiAliasing, g.Textures, g.NoGrass, g.GraySky, g.NoShadows) = (string)((Button)sender).Tag switch
        {
            "Potato"   => (15, "1", "Off", "Low", true, true, true),
            "Low"      => (30, "3", "Off", "Low", true, false, true),
            "Balanced" => (60, "5", "2x", "Medium", false, false, false),
            "High"     => (144, "8", "4x", "High", false, false, false),
            "Ultra"    => (0, "10", "8x", "High", false, false, false),
            _          => (0, "Auto", "Auto", "Auto", false, false, false),
        };
        if ((string)((Button)sender).Tag == "Auto") { g.Enabled = false; g.GraphicsApi = "Auto"; }
        DataContext = null;
        DataContext = _edit;
        FooterText.Text = $"{((Button)sender).Content} preset filled in. Press Save, then relaunch clients for it to apply.";
    }

    private void CleanRam_Click(object sender, RoutedEventArgs e) => _vm.CleanRamCommand.Execute(null);

    private void DeepClean_Click(object sender, RoutedEventArgs e) => _vm.DeepCleanCommand.Execute(null);

    // ---------------- Windows ----------------

    private void UnpinAll_Click(object sender, RoutedEventArgs e)
    {
        _vm.UnpinAll();
        RefreshLive();
    }

    // ---------------- Hotkeys ----------------

    private void HotkeyBox_KeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (sender is not TextBox { DataContext: HotkeyBinding binding } box) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Back or Key.Delete or Key.Escape) { binding.Keys = ""; box.Text = ""; return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return; // wait for the real key
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.None) { FooterText.Text = "Hold Ctrl, Alt, Shift or Win with the key."; return; }
        var combo = HotkeyService.Format(mods, key);
        var clash = _edit.Hotkeys.FirstOrDefault(h => h != binding && h.Keys.Equals(combo, StringComparison.OrdinalIgnoreCase));
        if (clash is not null) { FooterText.Text = $"{combo} is already used for '{clash.Action}'."; return; }
        binding.Keys = combo;
        box.Text = combo;
        FooterText.Text = $"{binding.Action}: {combo}. Press Save to apply.";
    }

    private void ResetHotkeys_Click(object sender, RoutedEventArgs e)
    {
        _edit.Hotkeys = ToolsSettings.DefaultHotkeys();
        HotkeyList.ItemsSource = _edit.Hotkeys;
    }

    // ---------------- Setups ----------------

    private void RefreshSetups()
    {
        _loadingSetups = true;
        SetupList.ItemsSource = _vm.Tools.Setups.Select(s => new SetupRow(s)).ToList();
        var names = new List<string> { "(none)" };
        names.AddRange(_vm.Tools.Setups.Select(s => s.Name));
        StartupSetupBox.ItemsSource = names;
        StartupSetupBox.SelectedItem = string.IsNullOrEmpty(_edit.StartupSetup) || !names.Contains(_edit.StartupSetup) ? "(none)" : _edit.StartupSetup;
        _loadingSetups = false;
    }

    private void SaveSetup_Click(object sender, RoutedEventArgs e)
    {
        var name = SetupName.Text.Trim();
        if (name.Length == 0) { FooterText.Text = "Type a name for the setup first."; return; }
        if (_vm.SaveSetup(name) is null) { RefreshLive(); return; }
        SetupName.Clear();
        RefreshSetups();
        RefreshLive();
    }

    private async void RunSetup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SetupRow row }) return;
        await _vm.RunSetupAsync(row.Model);
        RefreshLive();
    }

    private void DeleteSetup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SetupRow row }) return;
        if (MessageBox.Show(this, $"Delete setup '{row.Name}'?", "Tools", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _vm.DeleteSetup(row.Name);
        if (_edit.StartupSetup == row.Name) _edit.StartupSetup = "";
        RefreshSetups();
    }

    private void StartupSetup_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSetups) return;
        _edit.StartupSetup = StartupSetupBox.SelectedItem is string s && s != "(none)" ? s : "";
    }

    // ---------------- Macro ----------------

    private void Record_Click(object sender, RoutedEventArgs e) => _vm.ToggleMacroRecord();

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.Macro.IsPlaying)
        {
            // Use the loop/speed typed here even before Save.
            if (Keyboard.FocusedElement is TextBox tb) tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            _vm.Tools.MacroLoops = _edit.MacroLoops;
            _vm.Tools.MacroSpeed = Math.Clamp(_edit.MacroSpeed, 0.1, 10);
        }
        _vm.ToggleMacroPlay();
    }

    private void SaveMacro_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Macro.Events.Count == 0) { FooterText.Text = "Record a macro first."; return; }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save macro", InitialDirectory = MainViewModel.MacroFolder, FileName = "macro.json", Filter = "Macro (*.json)|*.json"
        };
        if (dlg.ShowDialog(this) != true) return;
        _vm.Macro.Save(dlg.FileName);
        FooterText.Text = "Saved " + Path.GetFileName(dlg.FileName);
    }

    private void LoadMacro_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Load macro", InitialDirectory = MainViewModel.MacroFolder, Filter = "Macro (*.json)|*.json"
        };
        if (dlg.ShowDialog(this) != true) return;
        try { _vm.Macro.Load(dlg.FileName); }
        catch (Exception ex) { MessageBox.Show(this, "Couldn't read that macro: " + ex.Message, "Tools"); }
    }

    // ---------------- Discord ----------------

    private void DevPortal_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://discord.com/developers/applications") { UseShellExecute = true });

    private async void TestWebhook_Click(object sender, RoutedEventArgs e)
    {
        WebhookBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        FooterText.Text = "Sending test…";
        var err = await WebhookService.SendAsync(_edit.WebhookUrl.Trim(), "Alt Manager test", "Webhook alerts are working.");
        FooterText.Text = err is null ? "Test sent — check your Discord channel." : "Test failed: " + err;
    }
}
