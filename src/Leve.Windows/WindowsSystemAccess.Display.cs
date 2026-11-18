using System.Globalization;
using System.Runtime.InteropServices;
using Leve.Core;
using Microsoft.Win32;

namespace Leve.Windows;

internal static class DisplayNative
{
    public const int ENUM_CURRENT_SETTINGS = -1;
    public const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    public const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;
    public const uint DM_PELSWIDTH = 0x00080000;
    public const uint DM_PELSHEIGHT = 0x00100000;
    public const uint DM_DISPLAYFREQUENCY = 0x00400000;
    public const uint DM_INTERLACED = 0x2;
    public const uint CDS_UPDATEREGISTRY = 0x1;
    public const uint CDS_TEST = 0x2;
    public const int DISP_CHANGE_SUCCESSFUL = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayDevicesW(string? device, uint deviceNumber, ref DISPLAY_DEVICE displayDevice, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplaySettingsW(string deviceName, int modeNumber, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int ChangeDisplaySettingsExW(string deviceName, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetSystemFirmwareTable(uint provider, uint tableId, byte[]? buffer, uint size);

    public static DEVMODE NewDevMode() => new()
    {
        dmDeviceName = string.Empty,
        dmFormName = string.Empty,
        dmSize = (ushort)Marshal.SizeOf<DEVMODE>(),
    };

    public static DISPLAY_DEVICE NewDisplayDevice() => new()
    {
        DeviceName = string.Empty,
        DeviceString = string.Empty,
        DeviceID = string.Empty,
        DeviceKey = string.Empty,
        cb = Marshal.SizeOf<DISPLAY_DEVICE>(),
    };
}

public sealed partial class WindowsSystemAccess
{
    private static bool IsSafeDisplayName(string name) =>
        name.StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase) && name.Length <= 32;

    /// <summary>Taxa atual (Hz) do monitor; Missing se ele não estiver conectado.</summary>
    private static StateValue ReadDisplay(string deviceName)
    {
        if (!IsSafeDisplayName(deviceName)) throw new ArgumentException($"monitor inválido: {deviceName}");
        var mode = DisplayNative.NewDevMode();
        return DisplayNative.EnumDisplaySettingsW(deviceName, DisplayNative.ENUM_CURRENT_SETTINGS, ref mode)
            ? StateValue.DWord(mode.dmDisplayFrequency)
            : StateValue.Missing;
    }

    /// <summary>Troca só a frequência, mantendo a resolução. Testa antes (CDS_TEST) e grava como padrão do Windows.</summary>
    private static void WriteDisplay(string deviceName, StateValue value)
    {
        if (!IsSafeDisplayName(deviceName)) throw new ArgumentException($"monitor inválido: {deviceName}");
        if (value.Kind != ValueKind.DWord) throw new NotSupportedException($"{value.Kind} não é frequência");
        var hz = uint.Parse(value.Data!, CultureInfo.InvariantCulture);
        if (hz is < 24 or > 1000) throw new ArgumentException($"frequência fora do razoável: {hz} Hz");

        var mode = DisplayNative.NewDevMode();
        if (!DisplayNative.EnumDisplaySettingsW(deviceName, DisplayNative.ENUM_CURRENT_SETTINGS, ref mode))
            throw new InvalidOperationException("monitor não está conectado");

        mode.dmDisplayFrequency = hz;
        mode.dmFields = DisplayNative.DM_PELSWIDTH | DisplayNative.DM_PELSHEIGHT | DisplayNative.DM_DISPLAYFREQUENCY;

        var test = DisplayNative.ChangeDisplaySettingsExW(deviceName, ref mode, IntPtr.Zero, DisplayNative.CDS_TEST, IntPtr.Zero);
        if (test != DisplayNative.DISP_CHANGE_SUCCESSFUL) throw new InvalidOperationException($"o driver recusou {hz} Hz (código {test})");

        var result = DisplayNative.ChangeDisplaySettingsExW(deviceName, ref mode, IntPtr.Zero, DisplayNative.CDS_UPDATEREGISTRY, IntPtr.Zero);
        if (result != DisplayNative.DISP_CHANGE_SUCCESSFUL) throw new InvalidOperationException($"não foi possível aplicar {hz} Hz (código {result})");
    }
}

/// <summary>Leituras da v1.2 para a tela > diagnóstico.</summary>
public static class DiagnosticScanner
{
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const uint Rsmb = 0x52534D42; // 'RSMB'

    public static IReadOnlyList<DisplayInfo> Displays()
    {
        var result = new List<DisplayInfo>();
        for (uint i = 0; i < 16; i++)
        {
            var adapter = DisplayNative.NewDisplayDevice();
            if (!DisplayNative.EnumDisplayDevicesW(null, i, ref adapter, 0)) break;
            if ((adapter.StateFlags & DisplayNative.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;

            var current = DisplayNative.NewDevMode();
            if (!DisplayNative.EnumDisplaySettingsW(adapter.DeviceName, DisplayNative.ENUM_CURRENT_SETTINGS, ref current)) continue;
            if (current.dmDisplayFrequency <= 1) continue; // 0/1 = "padrão do hardware": taxa desconhecida

            // Maior frequência não entrelaçada na MESMA resolução e profundidade de cor.
            var max = current.dmDisplayFrequency;
            var mode = DisplayNative.NewDevMode();
            for (var m = 0; DisplayNative.EnumDisplaySettingsW(adapter.DeviceName, m, ref mode); m++)
            {
                if (mode.dmPelsWidth == current.dmPelsWidth && mode.dmPelsHeight == current.dmPelsHeight
                    && mode.dmBitsPerPel == current.dmBitsPerPel && (mode.dmDisplayFlags & DisplayNative.DM_INTERLACED) == 0
                    && mode.dmDisplayFrequency > max && mode.dmDisplayFrequency <= 1000)
                {
                    max = mode.dmDisplayFrequency;
                }
                mode = DisplayNative.NewDevMode();
            }

            var monitor = DisplayNative.NewDisplayDevice();
            var label = DisplayNative.EnumDisplayDevicesW(adapter.DeviceName, 0, ref monitor, 0) && !string.IsNullOrWhiteSpace(monitor.DeviceString)
                ? $"{result.Count + 1} ({monitor.DeviceString})"
                : $"{result.Count + 1}";

            result.Add(new DisplayInfo(adapter.DeviceName, label, (int)current.dmPelsWidth, (int)current.dmPelsHeight,
                (int)current.dmDisplayFrequency, (int)max));
        }
        return result;
    }

    public static IReadOnlyList<MemoryModule> Memory()
    {
        try
        {
            var size = DisplayNative.GetSystemFirmwareTable(Rsmb, 0, null, 0);
            if (size == 0) return [];
            var buffer = new byte[size];
            return DisplayNative.GetSystemFirmwareTable(Rsmb, 0, buffer, size) == size ? Smbios.ParseMemory(buffer) : [];
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
        {
            return [];
        }
    }

    /// <summary>Nome da GPU que desenha a tela principal (evita pegar a integrada ou uma placa já removida).</summary>
    private static string? PrimaryAdapterName()
    {
        for (uint i = 0; i < 16; i++)
        {
            var adapter = DisplayNative.NewDisplayDevice();
            if (!DisplayNative.EnumDisplayDevicesW(null, i, ref adapter, 0)) break;
            if ((adapter.StateFlags & DisplayNative.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0) return adapter.DeviceString;
        }
        return null;
    }

    /// <summary>Data e nome do driver da GPU principal.</summary>
    public static (DateTime? Date, string? Name) GpuDriver()
    {
        var primary = PrimaryAdapterName();
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var cls = root.OpenSubKey(DisplayClass, writable: false);
            if (cls is null) return (null, null);

            foreach (var sub in cls.GetSubKeyNames().Where(s => s.Length == 4 && s.All(char.IsAsciiDigit)))
            {
                using var key = cls.OpenSubKey(sub, writable: false);
                var name = key?.GetValue("DriverDesc") as string;
                if (name is null || name.Contains("Basic", StringComparison.OrdinalIgnoreCase)) continue;
                if (primary is not null && !name.Equals(primary, StringComparison.OrdinalIgnoreCase)) continue;
                if (key?.GetValue("DriverDate") is string text
                    && DateTime.TryParseExact(text, ["M-d-yyyy", "MM-dd-yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    return (date, name);
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return (null, null);
    }

    public static IReadOnlyList<DriveSpace> Drives(IEnumerable<string> gameFolders)
    {
        var gameRoots = gameFolders.Select(f => Path.GetPathRoot(f) ?? string.Empty).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<DriveSpace>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                result.Add(new DriveSpace(drive.Name, drive.AvailableFreeSpace, drive.TotalSize, gameRoots.Contains(drive.Name)));
            }
            catch (IOException)
            {
            }
        }
        return result;
    }
}
