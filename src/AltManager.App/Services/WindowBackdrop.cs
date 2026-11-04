using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AltManager.App.Services;

/// <summary>
/// Enables the Windows 11 system backdrop (Mica / Acrylic) behind a window, for the "glass" themes.
/// Needs Windows 11 build 22621+; on anything older the DWM call fails and we keep an opaque window.
/// For the backdrop to show, the window background is made transparent so the composited blur shows
/// through; the app's translucent panels then read as frosted glass.
/// </summary>
public static class WindowBackdrop
{
    public enum Kind { None, Mica, Acrylic, MicaAlt }

    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Applies (or clears) the backdrop. Safe to call before the handle exists — it just no-ops.</summary>
    public static void Apply(Window window, Kind kind)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return; // call again from SourceInitialized once the handle is up

        // Try to set the backdrop; the HRESULT tells us whether this OS supports it (don't trust
        // Environment.OSVersion — without a supportedOS manifest it reports Windows 8).
        // DWM_SYSTEMBACKDROP_TYPE: 1 = None, 2 = Mica, 3 = Acrylic, 4 = Mica Alt
        var backdrop = kind switch { Kind.Mica => 2, Kind.Acrylic => 3, Kind.MicaAlt => 4, _ => 1 };
        var hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        var enabled = kind != Kind.None && hr == 0;

        // When glass is on: clear the WPF surface to transparent so the composited backdrop shows
        // through (the glass frame is extended across the client area in XAML). When off: opaque
        // clear + the theme's solid background, so it never renders black.
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } ct)
            ct.BackgroundColor = enabled ? Colors.Transparent : Colors.Black;

        window.Background = enabled
            ? Brushes.Transparent
            : (Application.Current?.Resources["Bg"] as Brush ?? Brushes.White);
    }
}
