using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Leve.Windows;

public sealed record CleanupCategory(string Id, string Title, string Description);

public sealed record CleanupResult(long FreedBytes, int DeletedFiles, int SkippedFiles);

/// <summary>
/// Limpeza de disco. Fica FORA do snapshot porque apagar arquivo não tem volta, por isso tem tela própria.
/// Nunca toca em Prefetch nem em cache de shaders (DirectX/NVIDIA/AMD): apagar esses caches causa
/// recompilação e stutter.
///
/// Segurança (o Leve roda como administrador e algumas pastas aceitam escrita de usuário comum):
/// - cada raiz é validada (nada de %TEMP% apontando para C:\ ou para o perfil);
/// - cada arquivo é apagado PELO HANDLE, aberto sem seguir links, e só depois de confirmar com
///   GetFinalPathNameByHandle que ele continua dentro da raiz. Isso fecha a troca de pasta por junção
///   entre a listagem e a exclusão.
/// </summary>
public sealed class DiskCleaner
{
    private sealed record Location(string Path, TimeSpan MinAge, bool IsFile = false);

    private static readonly TimeSpan OneDay = TimeSpan.FromDays(1);

    private readonly Dictionary<string, Location[]> _locations;

    public DiskCleaner()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        Categories =
        [
            new("temp-user", "Temporários da sua conta", @"%LOCALAPPDATA%\Temp: arquivos criados/alterados há mais de 1 dia."),
            new("temp-windows", "Temporários do Windows", @"C:\Windows\Temp: arquivos com mais de 1 dia."),
            new("update-cache", "Downloads do Windows Update", "Instaladores já aplicados (mais de 1 dia). O Windows baixa de novo se precisar."),
            new("delivery-cache", "Cache do Delivery Optimization", "Pedaços de atualização guardados para compartilhar (mais de 1 dia)."),
            new("crash-dumps", "Despejos de travamento e relatórios", "Minidumps, MEMORY.DMP e relatórios de erro do WER."),
            new("recycle-bin", "Lixeira", "Esvazia a Lixeira de todas as unidades."),
        ];

        // A pasta temporária vem SEMPRE do perfil, nunca da variável TEMP/TMP (que pode apontar para qualquer lugar).
        _locations = new Dictionary<string, Location[]>
        {
            ["temp-user"] = [new(Path.Combine(localAppData, "Temp"), OneDay)],
            ["temp-windows"] = [new(Path.Combine(windows, "Temp"), OneDay)],
            ["update-cache"] = [new(Path.Combine(windows, @"SoftwareDistribution\Download"), OneDay)],
            ["delivery-cache"] = [new(Path.Combine(windows, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"), OneDay)],
            ["crash-dumps"] =
            [
                new(Path.Combine(localAppData, "CrashDumps"), TimeSpan.Zero),
                new(Path.Combine(windows, "Minidump"), TimeSpan.Zero),
                new(Path.Combine(windows, "MEMORY.DMP"), TimeSpan.Zero, IsFile: true),
                new(Path.Combine(programData, @"Microsoft\Windows\WER\ReportArchive"), TimeSpan.Zero),
                new(Path.Combine(programData, @"Microsoft\Windows\WER\ReportQueue"), TimeSpan.Zero),
            ],
        };
    }

    public IReadOnlyList<CleanupCategory> Categories { get; }

    public long Measure(string categoryId)
    {
        if (categoryId == "recycle-bin") return RecycleBinSize();
        return Candidates(categoryId).Sum(c => c.Length);
    }

    public CleanupResult Clean(string categoryId)
    {
        if (categoryId == "recycle-bin")
        {
            var size = RecycleBinSize();
            Native.SHEmptyRecycleBinW(IntPtr.Zero, null, Native.SHERB_NOCONFIRMATION | Native.SHERB_NOPROGRESSUI | Native.SHERB_NOSOUND);
            return new CleanupResult(Math.Max(0, size - RecycleBinSize()), 0, 0);
        }

        long freed = 0;
        int deleted = 0, skipped = 0;
        foreach (var candidate in Candidates(categoryId))
        {
            if (SafeDelete(candidate.FullPath, candidate.Root))
            {
                freed += candidate.Length;
                deleted++;
            }
            else
            {
                skipped++; // em uso, protegido ou fora da raiz: fica
            }
        }

        foreach (var location in _locations[categoryId].Where(l => !l.IsFile))
            RemoveEmptyDirectories(location.Path);

        return new CleanupResult(freed, deleted, skipped);
    }

    private sealed record Candidate(string FullPath, string Root, long Length);

    /// <summary>Lista materializada (erros de enumeração não abortam a categoria inteira).</summary>
    private List<Candidate> Candidates(string categoryId)
    {
        var result = new List<Candidate>();
        if (!_locations.TryGetValue(categoryId, out var locations)) return result;
        var nowUtc = DateTime.UtcNow;

        foreach (var location in locations)
        {
            try
            {
                if (location.IsFile)
                {
                    var single = new FileInfo(location.Path);
                    if (single.Exists && IsOldEnough(single, nowUtc, location.MinAge))
                        result.Add(new Candidate(single.FullName, single.DirectoryName!, single.Length));
                    continue;
                }

                if (!Directory.Exists(location.Path) || !IsSafeRoot(location.Path)) continue;
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint, // não desce em junções nem lista links
                };
                foreach (var file in new DirectoryInfo(location.Path).EnumerateFiles("*", options))
                {
                    if (IsOldEnough(file, nowUtc, location.MinAge))
                        result.Add(new Candidate(file.FullName, location.Path, file.Length));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // pasta sumiu ou ficou inacessível no meio: segue com as outras
            }
        }
        return result;
    }

    /// <summary>Idade pela data mais recente entre criação e escrita (instaladores extraem arquivos com data antiga).</summary>
    private static bool IsOldEnough(FileInfo file, DateTime nowUtc, TimeSpan minAge)
    {
        var newest = file.CreationTimeUtc > file.LastWriteTimeUtc ? file.CreationTimeUtc : file.LastWriteTimeUtc;
        return nowUtc - newest >= minAge;
    }

    /// <summary>Recusa raízes perigosas: raiz de unidade, o perfil, a pasta do Windows ou System32 em si.</summary>
    private static bool IsSafeRoot(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var forbidden = new[]
        {
            Path.GetPathRoot(full) ?? string.Empty,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.SystemDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        return forbidden.All(f => !string.Equals(Path.TrimEndingDirectorySeparator(f), full, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Abre o arquivo SEM seguir link/junção, confere o caminho real pelo handle e apaga pelo mesmo handle.
    /// Se qualquer pasta no caminho tiver virado junção, o caminho real cai fora da raiz e nada é apagado.
    /// </summary>
    private static bool SafeDelete(string path, string root)
    {
        var realRoot = FinalPath(root, isDirectory: true);
        if (realRoot is null) return false;

        using var handle = Native.CreateFileW(path, Native.DELETE | Native.FILE_READ_ATTRIBUTES,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE | Native.FILE_SHARE_DELETE, IntPtr.Zero,
            Native.OPEN_EXISTING, Native.FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (handle.IsInvalid) return false;

        var realPath = FinalPath(handle);
        if (realPath is null || !realPath.StartsWith(realRoot + @"\", StringComparison.OrdinalIgnoreCase)) return false;

        var info = new Native.FILE_DISPOSITION_INFO_EX
        {
            Flags = Native.FILE_DISPOSITION_FLAG_DELETE | Native.FILE_DISPOSITION_FLAG_POSIX_SEMANTICS
                    | Native.FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE,
        };
        return Native.SetFileInformationByHandle(handle, Native.FileDispositionInfoEx, ref info,
            (uint)Marshal.SizeOf<Native.FILE_DISPOSITION_INFO_EX>());
    }

    private static string? FinalPath(string path, bool isDirectory)
    {
        using var handle = Native.CreateFileW(path, Native.FILE_READ_ATTRIBUTES,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE | Native.FILE_SHARE_DELETE, IntPtr.Zero,
            Native.OPEN_EXISTING, isDirectory ? Native.FILE_FLAG_BACKUP_SEMANTICS : 0, IntPtr.Zero);
        return handle.IsInvalid ? null : FinalPath(handle);
    }

    private static string? FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(1024);
        var length = Native.GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) return null;
        return Path.TrimEndingDirectorySeparator(buffer.ToString());
    }

    private static void RemoveEmptyDirectories(string root)
    {
        if (!Directory.Exists(root) || !IsSafeRoot(root)) return;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            // Mais profundos primeiro; a raiz nunca é apagada. Directory.Delete não recursivo só apaga pasta vazia.
            foreach (var dir in Directory.EnumerateDirectories(root, "*", options).OrderByDescending(d => d.Length).ToList())
            {
                try
                {
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0 && !Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir, recursive: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static long RecycleBinSize()
    {
        var info = new Native.SHQUERYRBINFO { cbSize = Marshal.SizeOf<Native.SHQUERYRBINFO>() };
        return Native.SHQueryRecycleBinW(null, ref info) == 0 ? info.i64Size : 0;
    }
}
