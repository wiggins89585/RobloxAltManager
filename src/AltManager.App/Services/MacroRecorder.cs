using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using AltManager.Core.Native;

namespace AltManager.App.Services;

/// <summary>
/// Records mouse and keyboard input (like TinyTask) and plays it back. Recording uses Windows' low-level
/// input hooks; playback sends the same input again with the original timing (scaled by speed).
/// Input the app sends itself is ignored while recording.
/// </summary>
public sealed class MacroRecorder : IDisposable
{
    public sealed record MacroEvent(long T, int Kind, int X, int Y, int Data);
    // Kind: 0 move, 1 left down, 2 left up, 3 right down, 4 right up, 5 middle down, 6 middle up, 7 wheel, 8 key down, 9 key up

    public List<MacroEvent> Events { get; private set; } = new();
    public bool IsRecording { get; private set; }
    public bool IsPlaying => _play is { IsCompleted: false };
    public event Action? StateChanged;

    private IntPtr _mouseHook, _keyHook;
    private LowLevelProc? _mouseProc, _keyProc; // kept alive so the GC can't collect the callbacks
    private Stopwatch _clock = new();
    private CancellationTokenSource? _playCts;
    private Task? _play;
    private readonly HashSet<int> _ignoreKeys = new();

    /// <summary>Keys that toggle recording/playback (the hotkeys), so they aren't recorded themselves.</summary>
    public void IgnoreKeys(IEnumerable<int> vks) { _ignoreKeys.Clear(); foreach (var k in vks) _ignoreKeys.Add(k); }

    public void StartRecording()
    {
        if (IsRecording || IsPlaying) return;
        Events = new List<MacroEvent>();
        _clock = Stopwatch.StartNew();
        _mouseProc = MouseHook;
        _keyProc = KeyHook;
        var mod = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _mouseProc, mod, 0);
        _keyHook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _keyProc, mod, 0);
        IsRecording = true;
        StateChanged?.Invoke();
    }

    public void StopRecording()
    {
        if (!IsRecording) return;
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != IntPtr.Zero) UnhookWindowsHookEx(_keyHook);
        _mouseHook = _keyHook = IntPtr.Zero;
        IsRecording = false;
        // Drop the trailing click on the app's own Stop button / the stop hotkey.
        while (Events.Count > 0 && Events[^1].Kind is 1 or 2 or 9) Events.RemoveAt(Events.Count - 1);
        StateChanged?.Invoke();
    }

    public void Play(int loops, double speed)
    {
        if (IsRecording || IsPlaying || Events.Count == 0) return;
        _playCts = new CancellationTokenSource();
        var ct = _playCts.Token;
        var events = Events.ToList();
        speed = Math.Clamp(speed, 0.1, 10);
        _play = Task.Run(async () =>
        {
            try
            {
                for (var loop = 0; loops <= 0 || loop < loops; loop++)
                {
                    long last = 0;
                    foreach (var e in events)
                    {
                        var wait = (int)((e.T - last) / speed);
                        last = e.T;
                        if (wait > 0) await Task.Delay(wait, ct);
                        Send(e);
                    }
                    await Task.Delay(200, ct);
                }
            }
            catch (OperationCanceledException) { }
            finally { StateChanged?.Invoke(); }
        }, ct);
        StateChanged?.Invoke();
    }

    public void StopPlaying() => _playCts?.Cancel();

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(Events));

    public void Load(string path)
    {
        Events = JsonSerializer.Deserialize<List<MacroEvent>>(File.ReadAllText(path)) ?? new();
        StateChanged?.Invoke();
    }

    public TimeSpan Length => Events.Count == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Events[^1].T);

    private static void Send(MacroEvent e)
    {
        switch (e.Kind)
        {
            case 0: User32.SetCursorPos(e.X, e.Y); break;
            case 1: User32.SetCursorPos(e.X, e.Y); User32.Mouse(User32.MOUSEEVENTF_LEFTDOWN); break;
            case 2: User32.SetCursorPos(e.X, e.Y); User32.Mouse(User32.MOUSEEVENTF_LEFTUP); break;
            case 3: User32.SetCursorPos(e.X, e.Y); User32.Mouse(User32.MOUSEEVENTF_RIGHTDOWN); break;
            case 4: User32.SetCursorPos(e.X, e.Y); User32.Mouse(User32.MOUSEEVENTF_RIGHTUP); break;
            case 5: User32.SetCursorPos(e.X, e.Y); User32.Mouse(User32.MOUSEEVENTF_MIDDLEDOWN); break;
            case 6: User32.SetCursorPos(e.X, e.Y); User32.Mouse(User32.MOUSEEVENTF_MIDDLEUP); break;
            case 7: User32.Mouse(User32.MOUSEEVENTF_WHEEL, data: (uint)e.Data); break;
            case 8: User32.Key((ushort)e.Data, false); break;
            case 9: User32.Key((ushort)e.Data, true); break;
        }
    }

    // ---------------- hooks ----------------

    private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var d = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if ((d.flags & 1) == 0) // not injected
            {
                var kind = (int)wParam switch
                {
                    0x0200 => 0, 0x0201 => 1, 0x0202 => 2, 0x0204 => 3, 0x0205 => 4, 0x0207 => 5, 0x0208 => 6, 0x020A => 7, _ => -1
                };
                if (kind == 0 && Events.Count > 0 && Events[^1].Kind == 0 && _clock.ElapsedMilliseconds - Events[^1].T < 15)
                    Events[^1] = Events[^1] with { T = _clock.ElapsedMilliseconds, X = d.pt.X, Y = d.pt.Y }; // thin out moves
                else if (kind >= 0)
                    Events.Add(new MacroEvent(_clock.ElapsedMilliseconds, kind, d.pt.X, d.pt.Y, kind == 7 ? (short)(d.mouseData >> 16) : 0));
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private IntPtr KeyHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var d = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((d.flags & 0x10) == 0 && !_ignoreKeys.Contains((int)d.vkCode)) // not injected
            {
                var msg = (int)wParam;
                if (msg is 0x0100 or 0x0104) Events.Add(new MacroEvent(_clock.ElapsedMilliseconds, 8, 0, 0, (int)d.vkCode));
                else if (msg is 0x0101 or 0x0105) Events.Add(new MacroEvent(_clock.ElapsedMilliseconds, 9, 0, 0, (int)d.vkCode));
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    public void Dispose()
    {
        StopRecording();
        StopPlaying();
    }

    private delegate IntPtr LowLevelProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public User32.POINT pt; public uint mouseData, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, LowLevelProc fn, IntPtr mod, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
