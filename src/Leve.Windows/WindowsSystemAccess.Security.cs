using Leve.Core;
using Microsoft.Win32;

namespace Leve.Windows;

public sealed partial class WindowsSystemAccess
{
    private const string DefenderPathExclusions = @"SOFTWARE\Microsoft\Windows Defender\Exclusions\Paths";

    /// <summary>Exclusões do Defender: leitura direta do Registro (admin enxerga), escrita só via cmdlet (Tamper Protection bloqueia o Registro).</summary>
    private static StateValue ReadExclusion(string folder)
    {
        using var root = OpenBase(Hive.HKLM);
        using var key = root.OpenSubKey(DefenderPathExclusions, writable: false);
        if (key is null) return StateValue.Flag(false);

        var wanted = folder.TrimEnd('\\');
        var excluded = key.GetValueNames().Any(n => n.TrimEnd('\\').Equals(wanted, StringComparison.OrdinalIgnoreCase));
        return StateValue.Flag(excluded);
    }

    private static void WriteExclusion(string folder, StateValue value)
    {
        if (value.Kind != ValueKind.Flag) throw new NotSupportedException($"{value.Kind} não é estado de exclusão");
        if (folder.Any(char.IsControl)) throw new ArgumentException("caminho com caractere de controle");

        var adding = value.AsFlag();
        if (adding)
        {
            // Só ao ADICIONAR: regra de biblioteca de jogos + caminho real idêntico (sem junção, link, nome curto 8.3 ou ponto final).
            // Remover é sempre permitido, para snapshots antigos continuarem reversíveis.
            if (!DynamicCatalog.IsSafeExclusion(folder)) throw new ArgumentException($"pasta recusada para exclusão: {folder}");
            if (!IsRealPath(folder)) throw new ArgumentException($"a pasta é um link/junção ou não existe: {folder}");
        }

        var cmdlet = adding ? "Add-MpPreference" : "Remove-MpPreference";
        try
        {
            // O caminho NUNCA entra no texto do script: vai por variável de ambiente.
            ProcessRunner.PowerShell($"{cmdlet} -ExclusionPath $env:LEVE_PATH -ErrorAction Stop",
                environment: new Dictionary<string, string> { ["LEVE_PATH"] = folder });
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"o Defender recusou ({ex.Message}). Se outro antivírus estiver ativo, configure a exclusão nele.", ex);
        }
    }
}

public sealed partial class WindowsSystemAccess
{
    /// <summary>Verdadeiro se o caminho resolvido pelo Windows é exatamente o pedido (nada de junção apontando para outro lugar).</summary>
    private static bool IsRealPath(string folder)
    {
        using var handle = Native.CreateFileW(folder, Native.FILE_READ_ATTRIBUTES,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE | Native.FILE_SHARE_DELETE, IntPtr.Zero,
            Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle.IsInvalid) return false;

        var buffer = new System.Text.StringBuilder(1024);
        var length = Native.GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) return false;

        var real = buffer.ToString().TrimEnd('\\');
        return real.Equals(@"\\?\" + folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Encontra as pastas onde os jogos estão instalados (Steam, Epic, Xbox/Game Pass, EA, Ubisoft, GOG, Riot).</summary>
public static class GameLibraryScanner
{
    public static IReadOnlyList<string> Scan()
    {
        var folders = new List<string>();
        Try(() => AddSteam(folders));
        Try(() => AddEpic(folders));
        Try(() => AddUbisoft(folders));
        Try(() => AddFixedLocations(folders));

        var result = new List<string>();
        foreach (var folder in folders)
        {
            try
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Replace('/', '\\')));
                if (Directory.Exists(full)) result.Add(full);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
            {
            }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Try(Action probe)
    {
        // Nenhuma loja com dados estranhos pode derrubar a abertura do Leve.
        try { probe(); }
        catch (Exception)
        {
        }
    }

    private static void AddSteam(List<string> folders)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = root.OpenSubKey(@"Software\Valve\Steam");
        if (key?.GetValue("SteamPath") is not string steamPath) return;

        var libraries = new List<string> { steamPath.Replace('/', '\\') };
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            foreach (var line in File.ReadLines(vdf))
            {
                // "path"		"D:\\SteamLibrary"
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;
                var start = trimmed.IndexOf('"', 6);
                var end = trimmed.LastIndexOf('"');
                if (start < 0 || end <= start) continue;
                libraries.Add(trimmed[(start + 1)..end].Replace(@"\\", @"\"));
            }
        }

        foreach (var library in libraries)
            folders.Add(Path.Combine(library, "steamapps", "common"));
    }

    private static void AddEpic(List<string> folders)
    {
        var manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Epic\EpicGamesLauncher\Data\Manifests");
        if (!Directory.Exists(manifests)) return;

        foreach (var item in Directory.EnumerateFiles(manifests, "*.item"))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(item));
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                if (!doc.RootElement.TryGetProperty("InstallLocation", out var location)
                    || location.ValueKind != System.Text.Json.JsonValueKind.String
                    || location.GetString() is not { } path) continue;

                // Só a pasta-mãe padrão "Epic Games"; jogo instalado em outro lugar não leva a pasta-mãe junto.
                var parent = Path.GetDirectoryName(path.TrimEnd('\\', '/'));
                if (parent is not null && Path.GetFileName(parent).Equals("Epic Games", StringComparison.OrdinalIgnoreCase))
                    folders.Add(parent);
            }
            catch (Exception)
            {
                // manifesto corrompido: pula
            }
        }
    }

    private static void AddUbisoft(List<string> folders)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var key = root.OpenSubKey(@"SOFTWARE\Ubisoft\Launcher");
        if (key?.GetValue("InstallDir") is string dir) folders.Add(Path.Combine(dir, "games"));
    }

    private static void AddFixedLocations(List<string> folders)
    {
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var rootPath = drive.RootDirectory.FullName;
            folders.Add(Path.Combine(rootPath, "XboxGames"));
            folders.Add(Path.Combine(rootPath, "GOG Games"));
            folders.Add(Path.Combine(rootPath, "Riot Games"));
        }
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        folders.Add(Path.Combine(programFiles, "EA Games"));
    }
}

/// <summary>Mede o sistema para o relatório de antes e depois.</summary>
public static class SystemReport
{
    public static MetricsReport Capture(ISystemAccess system, IEnumerable<StartupEntry> startupEntries)
    {
        var memory = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        Native.GlobalMemoryStatusEx(ref memory);

        var processes = System.Diagnostics.Process.GetProcesses();
        var processCount = processes.Length;
        foreach (var p in processes) p.Dispose();

        var startupEnabled = 0;
        foreach (var entry in startupEntries)
        {
            try
            {
                if (!DisableStartupEntry.IsDisabled(system.Read(entry.ApprovalTarget))) startupEnabled++;
            }
            catch (SystemAccessException)
            {
                startupEnabled++; // sem conseguir ler, conta como ativo (conservador)
            }
        }

        const long mb = 1024 * 1024;
        return new MetricsReport(
            DateTimeOffset.Now,
            processCount,
            (long)((memory.ullTotalPhys - memory.ullAvailPhys) / mb),
            (long)((memory.ullTotalPageFile - memory.ullAvailPageFile) / mb),
            RunningServices(),
            startupEnabled);
    }

    private static int RunningServices()
    {
        var scm = Native.OpenSCManagerW(null, null, Native.SC_MANAGER_CONNECT | Native.SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero) return -1;
        try
        {
            uint resume = 0;
            Native.EnumServicesStatusExW(scm, 0, Native.SERVICE_WIN32, Native.SERVICE_ACTIVE, IntPtr.Zero, 0, out var needed, out _, ref resume, null);
            if (needed == 0) return 0;

            for (var attempt = 0; attempt < 3; attempt++)
            {
                needed += 8192; // folga: serviços podem iniciar entre as chamadas
                var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)needed);
                try
                {
                    resume = 0;
                    if (Native.EnumServicesStatusExW(scm, 0, Native.SERVICE_WIN32, Native.SERVICE_ACTIVE, buffer, needed, out var more, out var returned, ref resume, null))
                        return (int)returned;
                    if (System.Runtime.InteropServices.Marshal.GetLastWin32Error() != Native.ERROR_MORE_DATA) return -1;
                    needed = more;
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
                }
            }
            return -1;
        }
        finally
        {
            Native.CloseServiceHandle(scm);
        }
    }
}
