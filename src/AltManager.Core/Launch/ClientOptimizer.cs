using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AltManager.Core.Launch;

/// <summary>
/// "Low-resource" mode, so many Roblox clients can run at once without maxing the machine out.
/// Two parts:
///  1. FFlags written to Roblox's own ClientAppSettings.json — cap the FPS and force the lowest
///     graphics quality. These are read when a client starts, so they apply to the NEXT launch.
///  2. Per-process tweaks applied to a running client — below-normal CPU priority and EcoQoS power
///     throttling, so Windows schedules them cheaply and the foreground stays responsive.
/// Everything is best-effort; nothing here cheats or modifies gameplay — only graphics/perf settings.
/// </summary>
public static class ClientOptimizer
{
    private static string VersionsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions");

    /// <summary>Applies the per-process tweaks (priority + EcoQoS) to a launched client.</summary>
    public static void Apply(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* may be denied */ }
            try { EnableEcoQoS(p.Handle); } catch { /* unsupported OS */ }
        }
        catch { /* process already gone */ }
    }

    /// <summary>Writes (or clears) the FPS-cap / low-graphics FFlags for every installed client version.
    /// <paramref name="fpsCap"/> ≤ 0 leaves the FPS uncapped.</summary>
    public static void SyncFFlags(bool lowResource, int fpsCap)
    {
        if (!Directory.Exists(VersionsFolder)) return;

        string? json = null;
        if (lowResource)
        {
            var flags = new Dictionary<string, object>
            {
                // 1 = lowest graphics quality level; biggest single GPU saving.
                ["DFIntDebugFRMQualityLevelOverride"] = 1,
            };
            if (fpsCap > 0) flags["DFIntTaskSchedulerTargetFps"] = fpsCap; // cap the frame rate
            json = JsonSerializer.Serialize(flags, new JsonSerializerOptions { WriteIndented = true });
        }

        foreach (var dir in Directory.GetDirectories(VersionsFolder))
        {
            if (!File.Exists(Path.Combine(dir, "RobloxPlayerBeta.exe"))) continue;
            var file = Path.Combine(dir, "ClientSettings", "ClientAppSettings.json");
            try
            {
                if (json is null)
                {
                    if (File.Exists(file)) File.Delete(file);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllText(file, json);
                }
            }
            catch { /* skip a version we can't write */ }
        }
    }

    // ---- EcoQoS (PROCESS_POWER_THROTTLING_EXECUTION_SPEED): run the process on efficiency cores at
    //      reduced clocks. Available on Windows 11 / recent Windows 10; the call just fails otherwise. ----

    private const int ProcessPowerThrottling = 4;
    private const uint PowerThrottlingCurrentVersion = 1;
    private const uint ExecutionSpeed = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr hProcess, int informationClass, ref ProcessPowerThrottlingState info, int size);

    private static void EnableEcoQoS(IntPtr handle)
    {
        var state = new ProcessPowerThrottlingState
        {
            Version = PowerThrottlingCurrentVersion,
            ControlMask = ExecutionSpeed,
            StateMask = ExecutionSpeed, // 1 = throttle this process
        };
        SetProcessInformation(handle, ProcessPowerThrottling, ref state, Marshal.SizeOf<ProcessPowerThrottlingState>());
    }
}
