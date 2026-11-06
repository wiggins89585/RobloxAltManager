using System.Diagnostics;
using Microsoft.Win32;

namespace AltManager.Core.Launch;

/// <summary>
/// Gives a launched Roblox client more of the machine: raises its CPU priority and tells Windows to
/// run it on the high-performance GPU. Both are best-effort and safe no-ops where they don't apply
/// (e.g. a single-GPU desktop, or a process we can't open). Nothing here modifies Roblox itself.
/// </summary>
public static class ClientOptimizer
{
    public static void Apply(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            try { p.PriorityClass = ProcessPriorityClass.High; } catch { /* may be denied */ }
            try { PreferHighPerformanceGpu(p.MainModule?.FileName); } catch { /* MainModule can be denied */ }
        }
        catch { /* process already gone */ }
    }

    /// <summary>Sets the per-app GPU preference to "High performance" for this executable. On a laptop
    /// with switchable graphics this moves Roblox onto the dedicated GPU; elsewhere it does nothing.</summary>
    private static void PreferHighPerformanceGpu(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return;
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
        if (key is null) return;
        // GpuPreference=2 is "High performance"; leave any explicit user choice alone.
        if (key.GetValue(exePath) is not string existing || !existing.Contains("GpuPreference=2"))
            key.SetValue(exePath, "GpuPreference=2;");
    }
}
