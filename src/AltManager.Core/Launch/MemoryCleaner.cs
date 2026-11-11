using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace AltManager.Core.Launch;

/// <summary>
/// RAM cleaner in the style of Mem Reduct. Trimming the Roblox clients' working sets needs no special
/// rights. A "deep clean" (flush the standby / modified lists and the file cache) is a Windows rule:
/// it needs administrator rights, so the app runs a one-shot elevated copy of itself for it.
/// </summary>
public static class MemoryCleaner
{
    public sealed record MemoryStatus(int LoadPercent, long TotalMb, long AvailableMb);

    public static MemoryStatus Status()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return new(0, 0, 0);
        return new((int)m.dwMemoryLoad, (long)(m.ullTotalPhys / 1048576), (long)(m.ullAvailPhys / 1048576));
    }

    /// <summary>Returns most of a process's memory to Windows; it pages back in as needed.</summary>
    public static bool Trim(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return EmptyWorkingSet(p.Handle);
        }
        catch { return false; }
    }

    public static int TrimAll(IEnumerable<int> pids) => pids.Count(Trim);

    public static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>System-wide clean. Requires administrator rights; returns false without them.</summary>
    public static bool DeepClean()
    {
        if (!IsAdmin()) return false;
        EnablePrivilege("SeProfileSingleProcessPrivilege");
        EnablePrivilege("SeIncreaseQuotaPrivilege");
        var ok = false;
        // Same order Mem Reduct uses: working sets, modified pages, standby list, then file cache.
        foreach (var cmd in new[] { MemoryEmptyWorkingSets, MemoryFlushModifiedList, MemoryPurgeStandbyList })
        {
            var c = cmd;
            ok |= NtSetSystemInformation(SystemMemoryListInformation, ref c, sizeof(int)) == 0;
        }
        try { SetSystemFileCacheSize(new IntPtr(-1), new IntPtr(-1), 0); } catch { }
        return ok;
    }

    /// <summary>Starts an elevated copy of the given exe in "--memclean" mode (shows the UAC prompt).</summary>
    public static bool RunElevatedClean(string exePath)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exePath, "--memclean") { UseShellExecute = true, Verb = "runas" });
            p?.WaitForExit(30000);
            return p is { HasExited: true, ExitCode: 0 };
        }
        catch { return false; } // user said no to UAC
    }

    // ---------------- native ----------------

    private const int SystemMemoryListInformation = 80;
    private const int MemoryEmptyWorkingSets = 2, MemoryFlushModifiedList = 3, MemoryPurgeStandbyList = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr hProcess);
    [DllImport("ntdll.dll")] private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetSystemFileCacheSize(IntPtr min, IntPtr max, int flags);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TOKEN_PRIVILEGES { public int Count; public long Luid; public int Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, int len, IntPtr prev, IntPtr retLen);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    private static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0020 | 0x0008, out var token)) return;
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) return;
            var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = 2 /* SE_PRIVILEGE_ENABLED */ };
            AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally { CloseHandle(token); }
    }
}
