using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace AltManager.App.Services;

/// <summary>System-wide hotkeys (work while a game window has focus), via RegisterHotKey.</summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private readonly IntPtr _hwnd;
    private readonly HwndSource? _source;
    private readonly Dictionary<int, string> _actions = new();
    private int _nextId = 0xA000;

    public event Action<string>? Pressed;

    public HotkeyService(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
    }

    /// <summary>Replaces all hotkeys. Returns the combos Windows refused (already used by another app).</summary>
    public List<string> Register(IEnumerable<(string Action, string Keys)> bindings)
    {
        UnregisterAll();
        var failed = new List<string>();
        foreach (var (action, keys) in bindings)
        {
            if (!TryParse(keys, out var mods, out var vk)) continue;
            var id = _nextId++;
            if (RegisterHotKey(_hwnd, id, mods | 0x4000 /* MOD_NOREPEAT */, vk)) _actions[id] = action;
            else failed.Add($"{keys} ({action})");
        }
        return failed;
    }

    /// <summary>Virtual-key codes of the registered keys (so the macro recorder can ignore them).</summary>
    public static IEnumerable<int> KeyCodes(IEnumerable<string> combos)
    {
        foreach (var c in combos)
            if (TryParse(c, out _, out var vk)) yield return (int)vk;
    }

    public static bool TryParse(string? text, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= 0x2; break;
                case "alt": mods |= 0x1; break;
                case "shift": mods |= 0x4; break;
                case "win": mods |= 0x8; break;
                default:
                    if (!Enum.TryParse<Key>(raw, true, out var key)) return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        return vk != 0 && mods != 0; // require a modifier so normal typing isn't swallowed
    }

    /// <summary>Formats a key press as "Ctrl+Alt+K" (for the hotkey capture boxes).</summary>
    public static string Format(ModifierKeys mods, Key key)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            Pressed?.Invoke(action);
        }
        return IntPtr.Zero;
    }

    private void UnregisterAll()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_hwnd, id);
        _actions.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.RemoveHook(WndProc);
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
