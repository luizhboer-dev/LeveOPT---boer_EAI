using Leve.Core;
using Microsoft.Win32;

namespace Leve.Windows;

/// <summary>Lê o que abre com o Windows: Run (HKCU, HKLM, HKLM 32 bits) e pastas de Inicialização.</summary>
public static class StartupScanner
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static IReadOnlyList<StartupEntry> Scan()
    {
        var entries = new List<StartupEntry>();
        AddRun(entries, RegistryHive.CurrentUser, RegistryView.Registry64, StartupSource.UserRun);
        AddRun(entries, RegistryHive.LocalMachine, RegistryView.Registry64, StartupSource.MachineRun);
        AddRun(entries, RegistryHive.LocalMachine, RegistryView.Registry32, StartupSource.MachineRun32);
        AddFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupSource.UserFolder);
        AddFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupSource.CommonFolder);
        return entries;
    }

    private static void AddRun(List<StartupEntry> entries, RegistryHive hive, RegistryView view, StartupSource source)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(RunKey, writable: false);
            if (key is null) return;
            foreach (var name in key.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                var command = key.GetValue(name, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;
                entries.Add(new StartupEntry(name, command, source));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Uma fonte ilegível não impede as outras.
        }
    }

    private static void AddFolder(List<StartupEntry> entries, string folder, StartupSource source)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                entries.Add(new StartupEntry(name, file, source));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
        }
    }
}

/// <summary>Lista as tarefas da raiz do Agendador e de \Mozilla (onde ficam os updaters de terceiros).</summary>
public static class TaskScanner
{
    private const int TaskEnumHidden = 1;

    private static readonly string[] Folders = [@"\", @"\Mozilla"];

    public static IReadOnlyList<string> ScanRootTasks()
    {
        var paths = new List<string>();
        dynamic service;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
            service = Activator.CreateInstance(type)!;
            service.Connect();
        }
        catch (Exception)
        {
            return paths; // sem Agendador acessível: simplesmente não oferece updaters
        }

        foreach (var folder in Folders)
        {
            try
            {
                dynamic tasks = service.GetFolder(folder).GetTasks(TaskEnumHidden);
                int count = tasks.Count;
                for (var i = 1; i <= count; i++)
                {
                    dynamic task = tasks.Item(i);
                    string path = task.Path;
                    paths.Add(path);
                }
            }
            catch (Exception)
            {
                // pasta inexistente (ex.: sem Firefox): segue
            }
        }
        return paths;
    }
}
