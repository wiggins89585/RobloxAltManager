using AltManager.Core.Native;

namespace AltManager.App.Services;

/// <summary>
/// Stops Roblox's ~20-minute idle kick. Each client window launched by this app is brought forward for
/// a moment, gets one small input, and your own window and cursor are put back.
/// "Mouse nudge" (default) jiggles the cursor over the game without moving your character, so things
/// like auto-farm keep running; "Jump" taps Space.
/// </summary>
public static class AntiAfk
{
    public static async Task RunAsync(IReadOnlyList<IntPtr> windows, string mode)
    {
        if (windows.Count == 0) return;
        var userWindow = User32.GetForegroundWindow();
        User32.GetCursorPos(out var cursor);

        foreach (var h in windows)
        {
            if (h == IntPtr.Zero || !User32.IsWindow(h)) continue;
            var wasMinimized = User32.IsIconic(h);
            User32.Focus(h);
            await Task.Delay(150);

            if (mode == "Jump")
            {
                User32.Key(0x20, false); // VK_SPACE
                await Task.Delay(60);
                User32.Key(0x20, true);
            }
            else if (User32.GetWindowRect(h, out var r) && r.Width > 0)
            {
                // Roblox only counts mouse movement over its own window: move there, nudge, come back.
                User32.SetCursorPos(r.Left + r.Width / 2, r.Top + r.Height / 2);
                User32.Mouse(User32.MOUSEEVENTF_MOVE, 4, 0);
                await Task.Delay(40);
                User32.Mouse(User32.MOUSEEVENTF_MOVE, -4, 0);
            }

            await Task.Delay(150);
            if (wasMinimized) User32.ShowWindow(h, User32.SW_SHOWMINNOACTIVE);
        }

        User32.SetCursorPos(cursor.X, cursor.Y);
        if (userWindow != IntPtr.Zero) User32.Focus(userWindow);
    }
}
