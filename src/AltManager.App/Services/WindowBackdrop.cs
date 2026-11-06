using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace AltManager.App.Services;

/// <summary>
/// Puts a Mica or Acrylic backdrop behind a window, for the "glass" themes. The window surface goes
/// transparent so the blur shows through and the translucent panels read as frosted glass. If the
/// OS refuses, the window falls back to the theme's opaque background.
/// </summary>
public static class WindowBackdrop
{
    public enum Kind { None, Mica, Acrylic, MicaAlt }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    /// <summary>Applies (or clears) the backdrop. Safe to call before the handle exists — it just no-ops.</summary>
    public static void Apply(Window window, Kind kind, bool dark = false)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        // DWM_SYSTEMBACKDROP_TYPE: 1 = None, 2 = Mica, 3 = Acrylic, 4 = Mica Alt
        var backdrop = kind switch { Kind.Mica => 2, Kind.Acrylic => 3, Kind.MicaAlt => 4, _ => 1 };
        var hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        var enabled = kind != Kind.None && hr == 0;

        var darkMode = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

        if (enabled) ExtendFrame(window, hwnd);

        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } ct)
            ct.BackgroundColor = enabled ? Colors.Transparent : Colors.Black;

        // Every glass window gets the translucent frost as its base (not a fully transparent surface).
        // A transparent base lets the raw, brighter acrylic show through the gaps between cards, which
        // reads as random white lines while scrolling; the frost base blends those gaps in. The frost
        // brush itself is driven by the frost slider, so the slider still has something to change.
        if (enabled) window.SetResourceReference(Window.BackgroundProperty, "Bg");
        else window.SetResourceReference(Window.BackgroundProperty, "BgSolid");
    }

    private static void ExtendFrame(Window window, IntPtr hwnd)
    {
        if (WindowChrome.GetWindowChrome(window) is { IsFrozen: false } chrome)
        {
            chrome.GlassFrameThickness = new Thickness(-1);
            return;
        }
        var all = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref all);
    }
}
