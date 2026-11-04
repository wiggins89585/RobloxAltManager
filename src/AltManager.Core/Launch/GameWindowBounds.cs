using AltManager.Core.Models;
using AltManager.Core.Native;

namespace AltManager.Core.Launch;

/// <summary>
/// Reads and restores a game-client window's rectangle so each account reopens at the size
/// and position it was last left at. Coordinates are physical pixels (the app is Per-Monitor-V2).
/// </summary>
public static class GameWindowBounds
{
    public static SavedWindowBounds? Capture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return null;
        if (!Win32.GetWindowRect(hwnd, out var r)) return null;
        if (r.Width <= 0 || r.Height <= 0) return null;
        return new SavedWindowBounds { X = r.Left, Y = r.Top, Width = r.Width, Height = r.Height };
    }

    public static void Apply(IntPtr hwnd, SavedWindowBounds b)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return;
        Win32.ShowWindow(hwnd, Win32.SW_RESTORE); // leave full-screen/maximized first, or the move is ignored
        Win32.SetWindowPos(hwnd, IntPtr.Zero, b.X, b.Y, b.Width, b.Height,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
    }
}
