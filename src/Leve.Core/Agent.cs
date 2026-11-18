using System.Text.Json;
using static Leve.Core.Hive;

namespace Leve.Core;

/// <summary>Configuração do Modo Jogo dinâmico, escrita pelo app em %LOCALAPPDATA%\Leve\agent.json.</summary>
public sealed record AgentConfig
{
    /// <summary>Bibliotecas de jogos (Steam, Epic, Xbox…): executável dentro delas = jogo.</summary>
    public List<string> GameFolders { get; init; } = [];

    /// <summary>Executáveis que o usuário quer tratar como jogo mesmo fora das bibliotecas (ex.: "minecraft.exe").</summary>
    public List<string> ExtraGames { get; init; } = [];

    /// <summary>Executáveis que nunca devem ativar o Modo Jogo.</summary>
    public List<string> IgnoredGames { get; init; } = [];

    public static AgentConfig? Load(string path)
    {
        try
        {
            var loaded = File.Exists(path) ? JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), JsonSnapshotStore.Json) : null;
            // JSON editado à mão pode trazer null nas listas: normaliza para nunca quebrar o agente.
            return loaded is null ? null : new AgentConfig
            {
                GameFolders = loaded.GameFolders?.Where(f => !string.IsNullOrWhiteSpace(f)).ToList() ?? new List<string>(),
                ExtraGames = loaded.ExtraGames?.Where(f => !string.IsNullOrWhiteSpace(f)).ToList() ?? new List<string>(),
                IgnoredGames = loaded.IgnoredGames?.Where(f => !string.IsNullOrWhiteSpace(f)).ToList() ?? new List<string>(),
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string path) => AtomicFile.WriteJson(path, this);
}

/// <summary>Escrita atômica com novas tentativas e leitura que não bloqueia quem está escrevendo.</summary>
public static class AtomicFile
{
    public static void WriteJson<T>(string path, T value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonSnapshotStore.Json));
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50); // o leitor do outro processo está com o arquivo aberto por um instante
            }
        }
    }

    public static T? ReadJson<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(stream, JsonSnapshotStore.Json);
    }
}

/// <summary>Estado publicado pelo agente a cada transição (nunca periodicamente).</summary>
public sealed record AgentStatus(bool InGame, string? Game, int Demoted, DateTimeOffset Since)
{
    public static AgentStatus? Load(string path)
    {
        try
        {
            return AtomicFile.ReadJson<AgentStatus>(path);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string path) => AtomicFile.WriteJson(path, this);
}

/// <summary>
/// Diário do que o agente rebaixou. Gravado ao entrar no modo jogo e apagado ao sair: se o agente morrer
/// no meio da partida, a próxima execução desfaz o rebaixamento dos processos que ainda forem os mesmos.
/// </summary>
public sealed record DemotionJournalEntry(int ProcessId, long StartTimeTicks, uint OriginalPriority, bool EcoApplied,
    uint? OriginalControlMask, uint? OriginalStateMask);

public static class GameRules
{
    // Launchers, ajudantes, instaladores e anti-cheats moram dentro das bibliotecas mas não são o jogo.
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam.exe", "steamwebhelper.exe", "steamservice.exe", "steamerrorreporter.exe", "gameoverlayui.exe",
        "epicgameslauncher.exe", "epicwebhelper.exe", "eadesktop.exe", "ealauncher.exe", "eabackgroundservice.exe",
        "upc.exe", "ubisoftconnect.exe", "uplaywebcore.exe", "battle.net.exe", "agent.exe",
        "riotclientservices.exe", "riotclientux.exe", "riotclientcrashhandler.exe", "galaxyclient.exe",
        "xboxpcapp.exe", "gamingservices.exe", "gamelaunchhelper.exe",
        "unitycrashhandler64.exe", "unitycrashhandler32.exe", "crashreportclient.exe", "crashpad_handler.exe",
        "easyanticheat.exe", "easyanticheat_eos.exe", "easyanticheat_eos_setup.exe", "beservice.exe", "vgtray.exe",
        "explorer.exe", "riot client.exe", "vrmonitor.exe", "vrserver.exe", "vrcompositor.exe", "vrdashboard.exe",
        "wallpaper32.exe", "wallpaper64.exe", "ui32.exe", "obs64.exe", "blender.exe", "losslessscaling.exe",
    };

    private static readonly string[] NotGamePrefixes = ["unins", "setup", "vc_redist", "vcredist", "dxsetup", "dotnet", "oalinst", "ue4prereq", "uninstall", "leagueclient"];

    // Launchers de editora (REDprelauncher, 2K/Bethesda "...Launcher.exe") abrem antes do jogo e fecham.
    private static readonly string[] NotGameContains = ["launcher", "crashhandler", "crashreport"];

    public static string FileName(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];

    public static bool IsGame(string exePath, AgentConfig config)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        var name = FileName(exePath);

        if (config.IgnoredGames.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        if (config.ExtraGames.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        if (NotGames.Contains(name)) return false;
        if (NotGamePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return false;
        if (NotGameContains.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase))) return false;

        return config.GameFolders.Any(folder =>
            exePath.StartsWith(folder.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Heavy = abaixo do normal + EcoQoS. Comunicação e música (Discord, Teams, Spotify…) ficam INTOCADAS de propósito:
/// com o jogo saturando a CPU, rebaixá-las faria a voz picotar. Subir o jogo já dá a ele a precedência.
/// </summary>
public enum DemoteTier { None, Heavy }

public static class DemoteRules
{
    private static readonly HashSet<string> Heavy = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi",
        "steamwebhelper", "epicwebhelper", "eadesktop", "upc", "battle.net", "galaxyclient", "riotclientux",
        "onedrive", "googledrivefs", "dropbox",
        "code", "obsidian", "notion", "searchapp", "phoneexperiencehost", "widgets", "widgetservice",
        "adobe desktop service", "creative cloud", "ccxprocess",
    };

    /// <param name="processName">Nome sem ".exe", como Process.ProcessName devolve.</param>
    public static DemoteTier TierFor(string processName)
    {
        return Heavy.Contains(processName) ? DemoteTier.Heavy : DemoteTier.None;
    }
}

public static class AgentCatalog
{
    public const string TweakId = "agent.game-mode";
    public const string RunValueName = "LeveAgent";
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Ligar o agente = um valor em HKCU\...\Run. É um ajuste comum: entra no snapshot e tem desfazer.</summary>
    public static Tweak Create(string agentExePath) => new()
    {
        Id = TweakId,
        Title = "Modo Jogo dinâmico (agente)",
        Area = Area.Gaming,
        Description = "Enquanto um jogo está em primeiro plano: navegadores, launchers, sincronização e editores vão para abaixo do normal + EcoQoS; "
                      + "o jogo sobe para acima do normal. Discord, Teams e Spotify ficam intocados (voz e música não picotam). Tudo volta quando o jogo fecha. "
                      + "O agente usa ~0% de CPU (só acorda em troca de janela e ao fechar o jogo) e cerca de 20 MB de RAM.",
        Profiles = Profiles.All,
        Restart = Restart.None,
        Impact = Impact.Lows,
        Strength = 2,
        Gain = "0–3% no FPS médio; melhora o 1% low quando navegador/sync estão ativos",
        Note = "sem administrador; só mexe nos processos da sua conta",
        Actions = [new SetValue(Target.Reg(HKCU, RunKey, RunValueName), StateValue.Text($"\"{agentExePath}\""))],
    };
}
