using System.Runtime.InteropServices;
using AltManager.Core.Native;

namespace AltManager.Core.Launch;

/// <summary>
/// Window tools for game clients: borderless, rename, minimize, exact placement (including below the
/// 800x600 size Roblox normally insists on), and screenshots for the Monitor page. Only ordinary
/// Windows window calls are used — nothing inside Roblox is touched.
/// </summary>
public static class RobloxWindows
{
    private static readonly Dictionary<IntPtr, long> OriginalStyles = new();

    /// <summary>Removes (or restores) the title bar and border.</summary>
    public static void SetBorderless(IntPtr h, bool on)
    {
        if (h == IntPtr.Zero || !User32.IsWindow(h)) return;
        var style = (long)User32.GetWindowLongPtr(h, User32.GWL_STYLE);
        const long frame = User32.WS_CAPTION | User32.WS_THICKFRAME | User32.WS_SYSMENU |
                           User32.WS_MINIMIZEBOX | User32.WS_MAXIMIZEBOX;
        lock (OriginalStyles)
        {
            if (on)
            {
                if ((style & User32.WS_CAPTION) == 0) return; // already borderless
                OriginalStyles.TryAdd(h, style);
                User32.SetWindowLongPtr(h, User32.GWL_STYLE, (IntPtr)(style & ~frame));
            }
            else
            {
                if (!OriginalStyles.Remove(h, out var orig)) return;
                User32.SetWindowLongPtr(h, User32.GWL_STYLE, (IntPtr)orig);
            }
        }
        User32.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE | User32.SWP_FRAMECHANGED);
    }

    public static bool IsBorderless(IntPtr h) =>
        h != IntPtr.Zero && ((long)User32.GetWindowLongPtr(h, User32.GWL_STYLE) & User32.WS_CAPTION) == 0;

    /// <summary>Sets the window title (e.g. to the account name), only if it differs.</summary>
    public static void SetTitle(IntPtr h, string title)
    {
        if (h == IntPtr.Zero || !User32.IsWindow(h)) return;
        if (User32.GetTitle(h) != title) User32.SetWindowText(h, title);
    }

    public static void Minimize(IntPtr h)
    {
        if (h != IntPtr.Zero && User32.IsWindow(h)) User32.ShowWindow(h, User32.SW_SHOWMINNOACTIVE);
    }

    /// <summary>
    /// Moves/sizes a window. With <paramref name="allowSmall"/>, Roblox isn't asked about its minimum size
    /// (SWP_NOSENDCHANGING), so windows can be smaller than 800x600 and more clients fit on screen.
    /// </summary>
    public static void Place(IntPtr h, int x, int y, int w, int hgt, bool allowSmall)
    {
        if (h == IntPtr.Zero || !User32.IsWindow(h)) return;
        if (User32.IsIconic(h)) User32.ShowWindow(h, User32.SW_RESTORE);
        var flags = User32.SWP_NOZORDER | User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW;
        if (allowSmall) flags |= User32.SWP_NOSENDCHANGING;
        User32.SetWindowPos(h, IntPtr.Zero, x, y, w, hgt, flags);
    }

    public static User32.RECT? Rect(IntPtr h) =>
        h != IntPtr.Zero && User32.IsWindow(h) && User32.GetWindowRect(h, out var r) ? r : null;

    /// <summary>Grid layout on the monitor of the first window, with a gap between cells.</summary>
    public static int Arrange(IReadOnlyList<IntPtr> windows, int? columns, int gap, bool allowSmall)
    {
        var hwnds = windows.Where(h => h != IntPtr.Zero && User32.IsWindow(h)).ToList();
        if (hwnds.Count == 0) return 0;
        var mi = new User32.MONITORINFO { cbSize = Marshal.SizeOf<User32.MONITORINFO>() };
        if (!User32.GetMonitorInfo(User32.MonitorFromWindow(hwnds[0], 2), ref mi)) return 0;
        var area = mi.rcWork;

        var n = hwnds.Count;
        var cols = Math.Clamp(columns ?? (int)Math.Ceiling(Math.Sqrt(n)), 1, n);
        var rows = (int)Math.Ceiling(n / (double)cols);
        var cellW = (area.Width - gap * (cols + 1)) / cols;
        var cellH = (area.Height - gap * (rows + 1)) / rows;
        for (var i = 0; i < n; i++)
        {
            var x = area.Left + gap + i % cols * (cellW + gap);
            var y = area.Top + gap + i / cols * (cellH + gap);
            Place(hwnds[i], x, y, cellW, cellH, allowSmall);
        }
        return n;
    }

    /// <summary>
    /// A small snapshot of the window (even when covered), or null. Returned as 32-bit BGRA pixels so the
    /// caller can wrap them in any image type. GPU windows sometimes come back black; callers check that.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height)? Capture(IntPtr h, int maxWidth)
    {
        if (Rect(h) is not { Width: > 0, Height: > 0 } r) return null;
        var tw = Math.Min(maxWidth, r.Width);
        var th = Math.Max(1, r.Height * tw / r.Width);

        var screen = User32.GetDC(IntPtr.Zero);
        var fullDc = User32.CreateCompatibleDC(screen);
        var smallDc = User32.CreateCompatibleDC(screen);
        var fullBmp = User32.CreateCompatibleBitmap(screen, r.Width, r.Height);
        var smallBmp = User32.CreateCompatibleBitmap(screen, tw, th);
        var oldFull = User32.SelectObject(fullDc, fullBmp);
        var oldSmall = User32.SelectObject(smallDc, smallBmp);
        try
        {
            if (!User32.PrintWindow(h, fullDc, 2 /* PW_RENDERFULLCONTENT */)) return null;
            User32.SetStretchBltMode(smallDc, 4 /* HALFTONE */);
            User32.StretchBlt(smallDc, 0, 0, tw, th, fullDc, 0, 0, r.Width, r.Height, 0x00CC0020 /* SRCCOPY */);
            User32.SelectObject(smallDc, oldSmall);
            var pixels = new byte[tw * th * 4];
            var bmi = new BITMAPINFO { biSize = Marshal.SizeOf<BITMAPINFO>(), biWidth = tw, biHeight = -th, biPlanes = 1, biBitCount = 32 };
            GetDIBits(screen, smallBmp, 0, (uint)th, pixels, ref bmi, 0);
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255; // opaque
            return (pixels, tw, th);
        }
        finally
        {
            User32.SelectObject(fullDc, oldFull);
            User32.DeleteObject(fullBmp); User32.DeleteObject(smallBmp);
            User32.DeleteDC(fullDc); User32.DeleteDC(smallDc);
            User32.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFO bmi, uint usage);
}
