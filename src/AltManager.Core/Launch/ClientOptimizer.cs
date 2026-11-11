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

    /// <summary>Runs a client at normal priority without power throttling (undoes Max alts / low-resource).</summary>
    public static void Restore(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            try { p.PriorityClass = ProcessPriorityClass.Normal; } catch { }
            try { SetEcoQoS(p.Handle, false); } catch { }
        }
        catch { }
    }

    /// <summary>
    /// Max alts: the client you're playing on runs at full speed; every other one gets Idle priority and
    /// Windows' efficiency mode, so a dozen background clients barely touch the CPU.
    /// </summary>
    public static void SetBackground(int pid, bool background)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var want = background ? ProcessPriorityClass.Idle : ProcessPriorityClass.Normal;
            try { if (p.PriorityClass != want) p.PriorityClass = want; } catch { }
            try { SetEcoQoS(p.Handle, background); } catch { }
        }
        catch { }
    }

    /// <summary>The flags a graphics configuration asks for (Roblox ignores any it no longer allows).</summary>
    public static Dictionary<string, object> GraphicsFlags(GraphicsSettings g)
    {
        var f = new Dictionary<string, object>();
        if (g.FpsCap > 0) f["DFIntTaskSchedulerTargetFps"] = g.FpsCap;
        if (int.TryParse(g.Quality, out var q) && q is >= 1 and <= 10) f["DFIntDebugFRMQualityLevelOverride"] = q;
        switch (g.AntiAliasing)
        {
            case "Off": f["FIntDebugForceMSAASamples"] = 0; break;
            case "2x": f["FIntDebugForceMSAASamples"] = 2; break;
            case "4x": f["FIntDebugForceMSAASamples"] = 4; break;
            case "8x": f["FIntDebugForceMSAASamples"] = 8; break;
        }
        if (g.Textures is "Low" or "Medium" or "High")
        {
            f["DFFlagTextureQualityOverrideEnabled"] = true;
            f["DFIntTextureQualityOverride"] = g.Textures switch { "Low" => 0, "Medium" => 1, _ => 3 };
        }
        switch (g.GraphicsApi)
        {
            case "Direct3D 11": f["FFlagDebugGraphicsPreferD3D11"] = true; break;
            case "Vulkan": f["FFlagDebugGraphicsPreferVulkan"] = true; break;
            case "OpenGL": f["FFlagDebugGraphicsPreferOpenGL"] = true; break;
        }
        if (g.NoGrass)
        {
            f["FIntFRMMinGrassDistance"] = 0;
            f["FIntFRMMaxGrassDistance"] = 0;
            f["FIntRenderGrassDetailStrands"] = 0;
        }
        if (g.GraySky) f["FFlagDebugSkyGray"] = true;
        if (g.NoShadows) f["FIntRenderShadowIntensity"] = 0;
        return f;
    }

    /// <summary>Writes (or clears) the client FFlags for every installed client version: low-resource mode
    /// (lowest quality + FPS cap) and/or the user's graphics settings. Nothing to write = file removed.</summary>
    public static void SyncFFlags(bool lowResource, int fpsCap, GraphicsSettings? graphics = null)
    {
        if (!Directory.Exists(VersionsFolder)) return;

        var flags = graphics is { Enabled: true } ? GraphicsFlags(graphics) : new Dictionary<string, object>();
        if (lowResource)
        {
            flags["DFIntDebugFRMQualityLevelOverride"] = 1; // lowest quality: biggest single GPU saving
            if (fpsCap > 0) flags["DFIntTaskSchedulerTargetFps"] = fpsCap;
        }
        string? json = flags.Count == 0 ? null : JsonSerializer.Serialize(flags, new JsonSerializerOptions { WriteIndented = true });

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

    private static void EnableEcoQoS(IntPtr handle) => SetEcoQoS(handle, true);

    private static void SetEcoQoS(IntPtr handle, bool on)
    {
        var state = new ProcessPowerThrottlingState
        {
            Version = PowerThrottlingCurrentVersion,
            ControlMask = ExecutionSpeed,
            StateMask = on ? ExecutionSpeed : 0, // set = throttle this process, clear = full speed
        };
        SetProcessInformation(handle, ProcessPowerThrottling, ref state, Marshal.SizeOf<ProcessPowerThrottlingState>());
    }
}
