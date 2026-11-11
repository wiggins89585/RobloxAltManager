using System.Diagnostics;

namespace AltManager.App.Services;

/// <summary>CPU and memory of each Roblox client, sampled every couple of seconds, with a short history.</summary>
public sealed class UsageMonitor
{
    public const int HistoryLength = 60;

    public sealed class Sample
    {
        public double Cpu;          // % of the whole machine
        public double MemoryMb;
        public readonly Queue<double> CpuHistory = new();
        public readonly Queue<double> MemHistory = new();
        internal TimeSpan LastCpuTime;
        internal DateTime LastAt;
    }

    private readonly Dictionary<int, Sample> _samples = new();

    public IReadOnlyDictionary<int, Sample> Samples => _samples;

    public void Update(IEnumerable<int> pids)
    {
        var alive = new HashSet<int>();
        var now = DateTime.UtcNow;
        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Refresh();
                alive.Add(pid);
                if (!_samples.TryGetValue(pid, out var s))
                {
                    _samples[pid] = s = new Sample { LastCpuTime = p.TotalProcessorTime, LastAt = now };
                    continue; // need two points for a CPU %
                }
                var cpuTime = p.TotalProcessorTime;
                var elapsed = (now - s.LastAt).TotalMilliseconds;
                if (elapsed > 0)
                    s.Cpu = Math.Clamp((cpuTime - s.LastCpuTime).TotalMilliseconds / (elapsed * Environment.ProcessorCount) * 100, 0, 100);
                s.LastCpuTime = cpuTime;
                s.LastAt = now;
                s.MemoryMb = p.WorkingSet64 / 1048576.0;
                Push(s.CpuHistory, s.Cpu);
                Push(s.MemHistory, s.MemoryMb);
            }
            catch { /* process gone */ }
        }
        foreach (var gone in _samples.Keys.Where(k => !alive.Contains(k)).ToList()) _samples.Remove(gone);
    }

    private static void Push(Queue<double> q, double v)
    {
        q.Enqueue(v);
        while (q.Count > HistoryLength) q.Dequeue();
    }
}
