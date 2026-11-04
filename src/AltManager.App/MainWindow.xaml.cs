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

        // Swap window chrome per theme (macOS gets traffic lights instead of Windows buttons).
        Services.Theme.ThemeChanged += ApplyChrome;
        ApplyChrome(Services.Theme.CurrentTheme);
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
            // The handle now exists, so the Mica/Acrylic backdrop for glass themes can be applied.
            Services.Theme.ReapplyBackdrop();
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

    /// <summary>macOS uses traffic lights on the left and no Windows caption buttons; every other theme is the reverse.</summary>
    private void ApplyChrome(string themeName)
    {
        var mac = themeName == "macOS";
        MacControls.Visibility = mac ? Visibility.Visible : Visibility.Collapsed;
        WinControls.Visibility = mac ? Visibility.Collapsed : Visibility.Visible;
        AppIcon.Visibility = mac ? Visibility.Collapsed : Visibility.Visible;
    }

    protected override void OnClosed(EventArgs e)
    {
        Services.Theme.ThemeChanged -= ApplyChrome;
        base.OnClosed(e);
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

    private Point _dragStart;
    private bool _dragArmed;

    /// <summary>Clicking anywhere on an account row toggles its checkbox, except on the controls inside it.</summary>
    private void Row_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridRow { DataContext: AccountViewModel vm }) return;

        _dragStart = e.GetPosition(null);
        _dragArmed = true;

        for (var d = e.OriginalSource as DependencyObject; d is not null && d != sender; d = ParentOf(d))
        {
            if (d is ComboBox or TextBox or ToggleButton) return;                 // group box, editors, the checkbox itself
            if (d is DataGridCell { Column: DataGridTextColumn { IsReadOnly: false } }) return; // alias: click to edit
        }

        vm.IsSelected = !vm.IsSelected;
        e.Handled = true;
    }

    /// <summary>In manual-order mode, dragging a row past the threshold starts a reorder drag.</summary>
    private void Row_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        if (Vm is not { ManualOrder: true }) return;
        if (sender is not DataGridRow { Item: AccountViewModel vm } row) return;

        var p = e.GetPosition(null);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _dragArmed = false;
        DragDrop.DoDragDrop(row, vm, DragDropEffects.Move);
    }

    /// <summary>Drops a dragged row onto this one, reordering the list.</summary>
    private void Row_Drop(object sender, DragEventArgs e)
    {
        if (sender is DataGridRow { Item: AccountViewModel target } &&
            e.Data.GetData(typeof(AccountViewModel)) is AccountViewModel moved)
            Vm?.ReorderTo(moved, target);
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
