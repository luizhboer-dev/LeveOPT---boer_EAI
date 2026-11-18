using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Leve.Core;
using Microsoft.Win32;

namespace Leve.Windows;

/// <summary>Placas de rede físicas (Ethernet e Wi-Fi) e as palavras-chave de economia que o driver de cada uma expõe.</summary>
public static class NetworkAdapterScanner
{
    private const int IfTypeEthernet = 6;
    private const int IfTypeWifi = 71;
    private const int NcfPhysical = 0x4;

    public static IReadOnlyList<NetworkAdapterInfo> Scan()
    {
        var result = new List<NetworkAdapterInfo>();
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var cls = root.OpenSubKey(HardwareCatalog.NetworkClass, writable: false);
            if (cls is null) return result;

            foreach (var sub in cls.GetSubKeyNames())
            {
                if (sub.Length != 4 || !sub.All(char.IsAsciiDigit)) continue; // pula "Properties"
                try
                {
                    using var adapter = cls.OpenSubKey(sub, writable: false);
                    if (adapter is null) continue;
                    var ifType = ToInt(adapter.GetValue("*IfType"));
                    var characteristics = ToInt(adapter.GetValue("Characteristics"));
                    if (ifType is not (IfTypeEthernet or IfTypeWifi) || (characteristics & NcfPhysical) == 0) continue;

                    var keywords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var keyword in HardwareCatalog.PowerSavingKeywords)
                    {
                        if (adapter.GetValue(keyword) is { } value) keywords[keyword] = value.ToString() ?? string.Empty;
                    }
                    var description = adapter.GetValue("DriverDesc") as string ?? $"placa {sub}";
                    result.Add(new NetworkAdapterInfo(sub, description, keywords));
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    // subchave protegida: segue
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return result;
    }

    private static int ToInt(object? value) => value switch
    {
        int i => i,
        string s when int.TryParse(s, out var parsed) => parsed,
        _ => 0,
    };
}

public static class GpuScanner
{
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>Duas ou mais GPUs reais (ignora o adaptador básico da Microsoft e displays virtuais/remotos).</summary>
    public static bool HasHybridGpu()
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var cls = root.OpenSubKey(DisplayClass, writable: false);
            if (cls is null) return false;

            var real = 0;
            foreach (var sub in cls.GetSubKeyNames().Where(s => s.Length == 4 && s.All(char.IsAsciiDigit)))
            {
                using var adapter = cls.OpenSubKey(sub, writable: false);
                var desc = adapter?.GetValue("DriverDesc") as string ?? string.Empty;
                if (desc.Length == 0) continue;
                if (desc.Contains("Basic", StringComparison.OrdinalIgnoreCase) || desc.Contains("Remote", StringComparison.OrdinalIgnoreCase)
                    || desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || desc.Contains("Parsec", StringComparison.OrdinalIgnoreCase)
                    || desc.Contains("Indirect", StringComparison.OrdinalIgnoreCase)) continue;
                real++;
            }
            return real >= 2;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}

/// <summary>Executáveis de jogo dentro das bibliotecas, com busca limitada (profundidade e quantidade) para não pesar na abertura.</summary>
public static class GameExecutableScanner
{
    private const int MaxDepth = 4;
    private const int MaxFiles = 3000;
    private const long MinSize = 1024 * 1024; // utilitários minúsculos não são o jogo

    public static IReadOnlyList<string> Scan(IEnumerable<string> libraries, AgentConfig rules)
    {
        var result = new List<string>();
        var visited = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = MaxDepth,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };

        foreach (var library in libraries)
        {
            try
            {
                foreach (var file in new DirectoryInfo(library).EnumerateFiles("*.exe", options))
                {
                    if (++visited > MaxFiles) return result;
                    if (file.Length >= MinSize && GameRules.IsGame(file.FullName, rules)) result.Add(file.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
        }
        return result;
    }
}

/// <summary>
/// PresentMon (Intel, open source). O Leve roda como administrador, então:
/// - copia o PresentMon para uma pasta só de administradores (%ProgramData%\Leve\tools\&lt;guid&gt;), onde ninguém planta DLL;
/// - mantém a cópia aberta sem permitir escrita e verifica a assinatura PELO HANDLE (sem troca entre verificar e executar);
/// - exige signatário exatamente "Intel Corporation" e produto PresentMon;
/// - grava o CSV nessa mesma pasta protegida.
/// </summary>
public static class PresentMonRunner
{
    public const string DownloadPage = "https://github.com/GameTechDev/PresentMon/releases";

    /// <summary>O PresentMon de console mais novo ao lado do Leve.exe (PresentMon-2.x.y-x64.exe).</summary>
    public static string? Find() =>
        Directory.EnumerateFiles(AppContext.BaseDirectory, "PresentMon-*-x64.exe")
            .Select(f => (Path: f, Version: ParseVersion(Path.GetFileName(f))))
            .Where(f => f.Version is not null)
            .OrderByDescending(f => f.Version)
            .Select(f => f.Path)
            .FirstOrDefault();

    private static Version? ParseVersion(string fileName)
    {
        // "PresentMon-2.3.1-x64.exe" → 2.3.1
        var middle = fileName["PresentMon-".Length..^"-x64.exe".Length];
        return Version.TryParse(middle, out var version) ? version : null;
    }

    public static bool IsTrusted(string path, out string reason)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Verify(stream, path, out reason);
    }

    private static bool Verify(FileStream stream, string path, out string reason)
    {
        if (!Authenticode.IsValid(path, stream.SafeFileHandle.DangerousGetHandle()))
        {
            reason = "assinatura digital ausente ou inválida";
            return false;
        }
        var signer = Authenticode.SignerName(path);
        if (signer != "Intel Corporation")
        {
            reason = $"assinado por \"{signer ?? "?"}\", esperado Intel Corporation";
            return false;
        }
        var info = FileVersionInfo.GetVersionInfo(path);
        if (!(info.ProductName ?? string.Empty).Contains("PresentMon", StringComparison.OrdinalIgnoreCase)
            && !(info.OriginalFilename ?? string.Empty).Contains("PresentMon", StringComparison.OrdinalIgnoreCase))
        {
            reason = "o arquivo é assinado pela Intel mas não é o PresentMon";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    public static FrameStats? Capture(string presentMon, string processName, int seconds)
    {
        if (processName.Any(c => char.IsControl(c) || c is '"' or '\\' or '/'))
            throw new ArgumentException("nome de processo inválido");

        var work = Path.Combine(ProtectedToolsRoot(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work); // herda a ACL só-administradores da pasta-mãe
        try
        {
            var copy = Path.Combine(work, "PresentMon.exe");
            File.Copy(presentMon, copy);
            using var locked = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read); // ninguém altera até o fim
            if (!Verify(locked, copy, out var reason)) throw new InvalidOperationException($"PresentMon recusado: {reason}");

            var csv = Path.Combine(work, "capture.csv");
            var args = $"--process_name \"{processName}\" --output_file \"{csv}\" --timed {seconds} --terminate_after_timed "
                       + "--terminate_on_proc_exit --no_console_stats --stop_existing_session --session_name LeveCapture";

            using var gpu = GpuTemperature.TryOpen();
            var temps = new List<double>();
            using var sampling = new CancellationTokenSource();
            // Só durante a captura que o usuário pediu: uma leitura do driver a cada 2 s.
            var sampler = gpu is null ? Task.CompletedTask : Task.Run(async () =>
            {
                while (!sampling.IsCancellationRequested)
                {
                    if (gpu.Read() is { } t) lock (temps) temps.Add(t);
                    try { await Task.Delay(2000, sampling.Token); } catch (OperationCanceledException) { }
                }
            });

            try
            {
                ProcessRunner.Run(copy, args, (seconds + 20) * 1000);
            }
            catch (InvalidOperationException)
            {
                // código de saída ≠ 0: vale o que estiver no CSV
            }
            finally
            {
                sampling.Cancel();
                sampler.GetAwaiter().GetResult();
            }

            if (!File.Exists(csv)) throw new InvalidOperationException("o PresentMon não gerou dados: o jogo estava aberto e com o nome certo?");
            var (frames, _, _) = PresentMonCsv.Parse(File.ReadLines(csv));
            List<double> gpuTemps;
            lock (temps) gpuTemps = temps.ToList();
            return FrameStats.Compute(frames, gpuTemps, cpuTemps: null);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>%ProgramData%\Leve\tools com dono Administradores e ACL só SYSTEM + Administradores, sem herança.</summary>
    private static string ProtectedToolsRoot()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Leve", "tools");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        var security = new DirectorySecurity();
        security.SetOwner(admins);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { system, admins })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        var info = Directory.CreateDirectory(root);
        info.SetAccessControl(security); // também corrige uma pasta pré-criada por outra conta
        return root;
    }
}

/// <summary>Temperatura da GPU NVIDIA pelo NVML do próprio driver (System32\nvml.dll). AMD/Intel: sem leitura (mostra "—").</summary>
internal sealed class GpuTemperature : IDisposable
{
    private delegate int InitDelegate();
    private delegate int ShutdownDelegate();
    private delegate int HandleByIndexDelegate(uint index, out IntPtr device);
    private delegate int TemperatureDelegate(IntPtr device, int sensor, out uint celsius);

    private readonly IntPtr _library;
    private readonly ShutdownDelegate _shutdown;
    private readonly TemperatureDelegate _temperature;
    private readonly IntPtr _device;

    private GpuTemperature(IntPtr library, ShutdownDelegate shutdown, TemperatureDelegate temperature, IntPtr device)
    {
        _library = library;
        _shutdown = shutdown;
        _temperature = temperature;
        _device = device;
    }

    public static GpuTemperature? TryOpen()
    {
        var path = Path.Combine(Environment.SystemDirectory, "nvml.dll"); // caminho absoluto: nada de DLL plantada
        if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out var library)) return null;
        try
        {
            var init = Marshal.GetDelegateForFunctionPointer<InitDelegate>(NativeLibrary.GetExport(library, "nvmlInit_v2"));
            var shutdown = Marshal.GetDelegateForFunctionPointer<ShutdownDelegate>(NativeLibrary.GetExport(library, "nvmlShutdown"));
            var byIndex = Marshal.GetDelegateForFunctionPointer<HandleByIndexDelegate>(NativeLibrary.GetExport(library, "nvmlDeviceGetHandleByIndex_v2"));
            var temperature = Marshal.GetDelegateForFunctionPointer<TemperatureDelegate>(NativeLibrary.GetExport(library, "nvmlDeviceGetTemperature"));
            if (init() != 0)
            {
                NativeLibrary.Free(library);
                return null;
            }
            if (byIndex(0, out var device) != 0)
            {
                shutdown();
                NativeLibrary.Free(library);
                return null;
            }
            return new GpuTemperature(library, shutdown, temperature, device);
        }
        catch (EntryPointNotFoundException)
        {
            NativeLibrary.Free(library);
            return null;
        }
    }

    public double? Read() => _temperature(_device, 0, out var celsius) == 0 ? celsius : null;

    public void Dispose()
    {
        _shutdown();
        NativeLibrary.Free(_library);
    }
}

/// <summary>Verificação Authenticode (WinVerifyTrust), sem rede: só o que está no arquivo e no cache do Windows.</summary>
internal static class Authenticode
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    /// <param name="fileHandle">Handle aberto do arquivo: a verificação lê exatamente o que será executado.</param>
    public static bool IsValid(string path, IntPtr fileHandle)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
            hFile = fileHandle,
        };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePointer, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = filePointer,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL,
            };
            var action = GenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    /// <summary>Nome comum (CN) exato do signatário, ex.: "Intel Corporation".</summary>
    public static string? SignerName(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // leitura do signatário de um arquivo assinado
            using var signed = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using var certificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(signed);
            return certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, forIssuer: false);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}
