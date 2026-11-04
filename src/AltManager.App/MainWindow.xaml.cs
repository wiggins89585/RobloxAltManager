using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AltManager.App.ViewModels;

namespace AltManager.App;

/// <summary>
/// Pure view: all behaviour lives in MainViewModel. Code here only drives the custom
/// title bar, rounded corners, and click-a-row-to-select.
/// </summary>
public partial class MainWindow : Window
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = $"v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";
        StateChanged += (_, _) =>
        {
            // Segoe MDL2: E923 = restore, E922 = maximize
            MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
            MaxButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
        };
        Closing += (_, _) =>
        {
            var b = WindowState == WindowState.Normal ? new Rect(0, 0, ActualWidth, ActualHeight) : RestoreBounds;
            (DataContext as AltManager.App.ViewModels.MainViewModel)?.SaveViewState(b.Width, b.Height);
        };
        SourceInitialized += (_, _) =>
        {
            // Windows 11 rounds the borderless window; older Windows ignores this (call just fails).
            var pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        };

        // Any input defers the idle auto-lock.
        PreviewMouseMove += (_, _) => Vm?.ResetIdle();
        PreviewMouseDown += (_, _) => Vm?.ResetIdle();
        PreviewKeyDown += (_, _) => Vm?.ResetIdle();

        // Persist window size automatically a moment after the user stops resizing.
        _saveSize = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveSize.Tick += (_, _) => { _saveSize.Stop(); PersistSize(); };
        SizeChanged += (_, _) => { _saveSize.Stop(); _saveSize.Start(); };
    }

    private readonly System.Windows.Threading.DispatcherTimer _saveSize;

    private void PersistSize()
    {
        if (WindowState != WindowState.Normal) return; // keep the restore size, not the maximized one
        Vm?.SaveViewState(ActualWidth, ActualHeight);
    }

    private AltManager.App.ViewModels.MainViewModel? Vm => DataContext as AltManager.App.ViewModels.MainViewModel;

    private void Unlock_Click(object sender, RoutedEventArgs e) => TryUnlock();
    private void UnlockBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) TryUnlock(); }

    private void TryUnlock()
    {
        if (Vm is null) return;
        if (Vm.TryUnlock(UnlockBox.Password)) { UnlockBox.Clear(); UnlockError.Text = ""; }
        else { UnlockError.Text = "Wrong password."; UnlockBox.SelectAll(); }
    }

    private void LockOverlay_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (LockOverlay.IsVisible) UnlockBox.Focus();
    }

    /// <summary>Clicking anywhere on an account row toggles its checkbox, except on the controls inside it.</summary>
    private void Row_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridRow { DataContext: AccountViewModel vm }) return;

        for (var d = e.OriginalSource as DependencyObject; d is not null && d != sender; d = ParentOf(d))
        {
            if (d is ComboBox or TextBox or ToggleButton) return;                 // group box, editors, the checkbox itself
            if (d is DataGridCell { Column: DataGridTextColumn { IsReadOnly: false } }) return; // alias: click to edit
        }

        vm.IsSelected = !vm.IsSelected;
        e.Handled = true;
    }

    private static DependencyObject? ParentOf(DependencyObject d) =>
        d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    /// <summary>Clicking a card (grid view) toggles the account, like a list row. Right-click opens the menu.</summary>
    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AccountViewModel vm })
            vm.IsSelected = !vm.IsSelected;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
