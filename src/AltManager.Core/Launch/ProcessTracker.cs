using System.Collections.Concurrent;
using System.Diagnostics;

namespace AltManager.Core.Launch;

public sealed record TrackedProcess(int Pid, Guid AccountId, string AccountLabel, DateTimeOffset Started)
{
    public IntPtr MainWindow
    {
        get
        {
            try { using var p = Process.GetProcessById(Pid); return p.MainWindowHandle; }
            catch { return IntPtr.Zero; }
        }
    }
}

/// <summary>
/// Maps RobloxPlayerBeta.exe PIDs to accounts. The protocol handler goes
/// through RobloxPlayerLauncher.exe, so we never get the PID directly; instead
/// we snapshot PIDs before a launch and claim the first new one after it.
/// Launches are serialized by the engine, so attribution is unambiguous.
/// </summary>
public sealed class ProcessTracker : IDisposable
{
    public const string ClientProcessName = "RobloxPlayerBeta";

    private readonly ConcurrentDictionary<int, TrackedProcess> _tracked = new();
    private readonly Timer _reaper;

    public event EventHandler<TrackedProcess>? ProcessStarted;
    public event EventHandler<TrackedProcess>? ProcessExited;

    public IReadOnlyCollection<TrackedProcess> Active => _tracked.Values.ToList();

    public ProcessTracker()
    {
        _reaper = new Timer(_ => Reap(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public static HashSet<int> Snapshot() =>
        Process.GetProcessesByName(ClientProcessName).Select(p => { using (p) return p.Id; }).ToHashSet();

    /// <summary>Waits for a RobloxPlayerBeta PID not present in <paramref name="before"/> and binds it.</summary>
    public async Task<TrackedProcess?> ClaimNewProcessAsync(
        HashSet<int> before, Guid accountId, string label, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var fresh = Snapshot().Where(pid => !before.Contains(pid) && !_tracked.ContainsKey(pid)).ToList();
            if (fresh.Count > 0)
            {
                var tp = new TrackedProcess(fresh.Min(), accountId, label, DateTimeOffset.Now);
                if (_tracked.TryAdd(tp.Pid, tp))
                {
                    ProcessStarted?.Invoke(this, tp);
                    return tp;
                }
            }
            await Task.Delay(500, ct);
        }
        return null;
    }

    public bool IsRunning(Guid accountId) => _tracked.Values.Any(t => t.AccountId == accountId);

    public void Kill(int pid)
    {
        try { using var p = Process.GetProcessById(pid); p.Kill(); }
        catch (ArgumentException) { /* already gone */ }
        Reap();
    }

    private void Reap()
    {
        var alive = Snapshot();
        foreach (var (pid, tp) in _tracked)
            if (!alive.Contains(pid) && _tracked.TryRemove(pid, out _))
                ProcessExited?.Invoke(this, tp);
    }

    public void Dispose() => _reaper.Dispose();
}
