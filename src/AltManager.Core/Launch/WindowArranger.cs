using System.Runtime.InteropServices;
using AltManager.Core.Native;

namespace AltManager.Core.Launch;

/// <summary>
/// Tiles game windows into a grid on the work area (taskbar excluded) of the
/// monitor that holds the first window. Coordinates are physical pixels, so the
/// host app must be Per-Monitor-V2 DPI aware (see app.manifest).
/// </summary>
public static class WindowArranger
{
    public static int Arrange(IReadOnlyList<IntPtr> windows, int? columns = null, int gap = 4)
    {
        var hwnds = windows.Where(h => h != IntPtr.Zero && Win32.IsWindow(h)).ToList();
        if (hwnds.Count == 0) return 0;

        var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        var mon = Win32.MonitorFromWindow(hwnds[0], Win32.MONITOR_DEFAULTTONEAREST);
        if (!Win32.GetMonitorInfo(mon, ref mi)) return 0;
        var area = mi.rcWork;

        var n = hwnds.Count;
        var cols = Math.Clamp(columns ?? (int)Math.Ceiling(Math.Sqrt(n)), 1, n);
        var rows = (int)Math.Ceiling(n / (double)cols);

        var cellW = (area.Width - gap * (cols + 1)) / cols;
        var cellH = (area.Height - gap * (rows + 1)) / rows;

        for (var i = 0; i < n; i++)
        {
            var c = i % cols;
            var r = i / cols;
            var x = area.Left + gap + c * (cellW + gap);
            var y = area.Top + gap + r * (cellH + gap);

            Win32.ShowWindow(hwnds[i], Win32.SW_RESTORE); // un-maximize/minimize first, or SetWindowPos is ignored
            Win32.SetWindowPos(hwnds[i], IntPtr.Zero, x, y, cellW, cellH,
                Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        }
        return n;
    }
}
