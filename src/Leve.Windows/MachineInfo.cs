using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Leve.Core;
using Microsoft.Win32;

namespace Leve.Windows;

public static class ElevationGuard
{
    public static string? CurrentSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value;
    }

    /// <summary>SID do dono do explorer.exe nesta sessão, ou seja, quem está sentado na frente do PC.</summary>
    public static string? InteractiveUserSid()
    {
        var session = Process.GetCurrentProcess().SessionId;
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            using (process)
            {
                if (process.SessionId != session) continue;
                var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, process.Id);
                if (handle == IntPtr.Zero) continue;
                try
                {
                    if (!Native.OpenProcessToken(handle, Native.TOKEN_QUERY, out var token)) continue;
                    try
                    {
                        using var identity = new WindowsIdentity(token);
                        return identity.User?.Value;
                    }
                    finally
                    {
                        Native.CloseHandle(token);
                    }
                }
                finally
                {
                    Native.CloseHandle(handle);
                }
            }
        }
        return null;
    }

    /// <summary>Verdadeiro quando outro administrador autorizou o UAC: HKCU seria o perfil dele, não o seu.</summary>
    public static bool IsElevatedAsOtherUser()
    {
        var current = CurrentSid();
        var interactive = InteractiveUserSid();
        return current is not null && interactive is not null && current != interactive;
    }
}

public static class FactsProvider
{
    private static readonly string[] VirtualPrinterPorts = ["PORTPROMPT:", "nul:", "XPSPort:", "SHRFAX:"];

    public static SystemFacts Collect() => new()
    {
        Build = Environment.OSVersion.Version.Build,
        IsLaptop = Safe(HasBattery, fallback: false),
        // Na dúvida, assume que há impressora: o Spooler fica intocado.
        HasPhysicalPrinter = Safe(HasPhysicalPrinter, fallback: true),
        ChromeInstalled = Safe(() => AppPathExists("chrome.exe"), fallback: true),
        ElevatedAsOtherUser = Safe(ElevationGuard.IsElevatedAsOtherUser, fallback: false),
        HvciConfigured = Safe(IsHvciConfigured, fallback: false),
        DefenderActive = Safe(IsDefenderRunning, fallback: false),
        HasHybridGpu = Safe(GpuScanner.HasHybridGpu, fallback: false),
    };

    private static bool IsHvciConfigured()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
        return key?.GetValue("Enabled") is int;
    }

    private static bool IsDefenderRunning()
    {
        const uint ServiceRunning = 4;
        var scm = Native.OpenSCManagerW(null, null, Native.SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return false;
        try
        {
            var service = Native.OpenServiceW(scm, "WinDefend", Native.SERVICE_QUERY_STATUS);
            if (service == IntPtr.Zero) return false;
            try
            {
                return Native.QueryServiceStatus(service, out var status) && status.dwCurrentState == ServiceRunning;
            }
            finally
            {
                Native.CloseServiceHandle(service);
            }
        }
        finally
        {
            Native.CloseServiceHandle(scm);
        }
    }

    private static bool Safe(Func<bool> probe, bool fallback)
    {
        try { return probe(); }
        catch (Exception) { return fallback; }
    }

    private static bool HasBattery()
    {
        if (!Native.GetSystemPowerStatus(out var status)) return false;
        return status.BatteryFlag != 255 && (status.BatteryFlag & 128) == 0;
    }

    private static bool HasPhysicalPrinter()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var printers = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Print\Printers");
        if (printers is null) return false;

        foreach (var name in printers.GetSubKeyNames())
        {
            using var printer = printers.OpenSubKey(name);
            var port = printer?.GetValue("Port") as string ?? string.Empty;
            var isVirtual = VirtualPrinterPorts.Any(p => port.Equals(p, StringComparison.OrdinalIgnoreCase))
                || name.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("OneNote", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Fax", StringComparison.OrdinalIgnoreCase);
            if (!isVirtual) return true;
        }
        return false;
    }

    private static bool AppPathExists(string exe)
    {
        var path = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}";
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(path);
            if (key is not null) return true;
        }
        return false;
    }
}

public readonly record struct MetricsSample(double CpuPercent, ulong UsedBytes, ulong TotalBytes, int Processes);

/// <summary>Amostragem barata para a barra de status: GetSystemTimes + GlobalMemoryStatusEx + contagem de processos.</summary>
public sealed class SystemMetrics
{
    private ulong _lastIdle;
    private ulong _lastTotal;

    public MetricsSample Sample()
    {
        double cpu = 0;
        if (Native.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var total = kernel.Value + user.Value; // kernel já inclui o tempo ocioso
            if (_lastTotal != 0 && total > _lastTotal)
            {
                var dTotal = total - _lastTotal;
                var dIdle = idle.Value - _lastIdle;
                cpu = 100.0 * (dTotal - Math.Min(dIdle, dTotal)) / dTotal;
            }
            _lastIdle = idle.Value;
            _lastTotal = total;
        }

        var memory = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        Native.GlobalMemoryStatusEx(ref memory);

        var processes = Process.GetProcesses();
        var count = processes.Length;
        foreach (var p in processes) p.Dispose();

        return new MetricsSample(Math.Clamp(cpu, 0, 100), memory.ullTotalPhys - memory.ullAvailPhys, memory.ullTotalPhys, count);
    }
}
