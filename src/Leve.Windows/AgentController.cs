using System.Diagnostics;
using Leve.Core;

namespace Leve.Windows;

/// <summary>Liga, desliga e consulta o agente do Modo Jogo (Leve.Agent.exe, ao lado do Leve.exe).</summary>
public sealed class AgentController(string dataDirectory)
{
    private const string MutexName = @"Local\Leve.Agent.v1";
    private const string StopEventName = @"Local\Leve.Agent.Stop";

    private readonly string _configPath = Path.Combine(dataDirectory, "agent.json");
    private readonly string _statusPath = Path.Combine(dataDirectory, "agent-status.json");

    public static string ExePath => Path.Combine(AppContext.BaseDirectory, "Leve.Agent.exe");

    public static bool IsInstalled => File.Exists(ExePath);

    public static bool IsRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting(MutexName, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // existe, mas pertence a outra conta
        }
    }

    /// <summary>Grava as bibliotecas de jogos, preservando listas manuais do usuário. Devolve verdadeiro se mudou.</summary>
    public bool WriteConfig(IReadOnlyList<string> gameFolders)
    {
        var existing = AgentConfig.Load(_configPath);
        var updated = new AgentConfig
        {
            GameFolders = gameFolders.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(),
            ExtraGames = existing?.ExtraGames ?? new List<string>(),
            IgnoredGames = existing?.IgnoredGames ?? new List<string>(),
        };
        var changed = existing is null || !existing.GameFolders.SequenceEqual(updated.GameFolders, StringComparer.OrdinalIgnoreCase);
        if (changed) updated.Save(_configPath);
        return changed;
    }

    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelLua = 0;
    private const int TaskCreateOrUpdate = 6;
    private const int TaskActionExec = 0;
    private const int TaskInstancesIgnoreNew = 2;

    /// <summary>
    /// Inicia o agente SEM elevação. O Leve roda como administrador, então registra uma tarefa sob demanda
    /// (\Leve\Agent, sem gatilhos) com o token interativo do usuário e nível LUA, e a executa: o agente nasce
    /// na sessão do usuário, como processo comum. No login, quem inicia é o valor em HKCU\...\Run.
    /// </summary>
    public static void Start()
    {
        if (!IsInstalled || IsRunning()) return;

        // Arquivo vindo de zip baixado carrega a "marca da web": tira para o Windows não perguntar a cada login.
        try { File.Delete(ExePath + ":Zone.Identifier"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { }

        var user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();

        dynamic folder;
        try
        {
            folder = service.GetFolder(@"\Leve");
        }
        catch (Exception)
        {
            folder = service.GetFolder(@"\").CreateFolder("Leve");
        }

        dynamic definition = service.NewTask(0);
        definition.RegistrationInfo.Description = "Leve: inicia o agente do Modo Jogo sem elevação (sob demanda).";
        definition.Principal.LogonType = TaskLogonInteractiveToken;
        definition.Principal.RunLevel = TaskRunLevelLua;
        definition.Settings.ExecutionTimeLimit = "PT0S";
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.MultipleInstances = TaskInstancesIgnoreNew;
        dynamic action = definition.Actions.Create(TaskActionExec);
        action.Path = ExePath;

        dynamic task = folder.RegisterTaskDefinition("Agent", definition, TaskCreateOrUpdate, user, null, TaskLogonInteractiveToken, null);
        task.Run(null);
    }

    /// <summary>Pede ao agente para sair. Se ele acabou de iniciar e ainda não criou o evento, tenta por até 2 s.</summary>
    public static void Stop()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (EventWaitHandle.TryOpenExisting(StopEventName, out var stop))
            {
                using (stop) stop.Set();
                return;
            }
            if (!IsRunning()) return;
            Thread.Sleep(100);
        }
    }

    /// <summary>Para e inicia de novo (para recarregar o agent.json). Espera o anterior sair, até 3 s.</summary>
    public static async Task RestartAsync()
    {
        Stop();
        for (var i = 0; i < 30 && IsRunning(); i++) await Task.Delay(100);
        Start();
    }

    public string StatusText()
    {
        if (!IsInstalled) return "agente: não instalado";
        if (!IsRunning()) return "agente: desligado";
        var status = AgentStatus.Load(_statusPath);
        return status is { InGame: true }
            ? $"agente: em jogo · {status.Game} · {status.Demoted} rebaixados"
            : "agente: aguardando jogo";
    }
}
