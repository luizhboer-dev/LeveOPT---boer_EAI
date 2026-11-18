using System.Text;
using System.Text.RegularExpressions;
using static Leve.Core.Hive;

namespace Leve.Core;

public enum StartupSource { UserRun, MachineRun, MachineRun32, UserFolder, CommonFolder }

/// <summary>Um item que abre com o Windows, como o Gerenciador de Tarefas mostra.</summary>
public sealed record StartupEntry(string Name, string Command, StartupSource Source)
{
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>Valor em StartupApproved que liga/desliga o item (o mesmo que o Gerenciador de Tarefas usa).</summary>
    public Target ApprovalTarget => Source switch
    {
        StartupSource.UserRun => Target.Reg(HKCU, Approved + @"\Run", Name),
        StartupSource.MachineRun => Target.Reg(HKLM, Approved + @"\Run", Name),
        StartupSource.MachineRun32 => Target.Reg(HKLM, Approved + @"\Run32", Name),
        StartupSource.UserFolder => Target.Reg(HKCU, Approved + @"\StartupFolder", Name),
        _ => Target.Reg(HKLM, Approved + @"\StartupFolder", Name),
    };
}

/// <summary>
/// Desliga um item de inicialização via StartupApproved. O Windows grava 12 bytes: o primeiro byte ímpar
/// (0x03) significa desabilitado; ausente ou par significa habilitado. Se o item já estiver desligado
/// (inclusive pelo Gerenciador de Tarefas, com outra data), nada muda.
/// </summary>
public sealed record DisableStartupEntry(Target Target) : TweakAction(Target)
{
    internal static readonly StateValue DisabledValue =
        new(ValueKind.Binary, Convert.ToBase64String([0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));

    public static bool IsDisabled(StateValue value)
    {
        if (value.Kind != ValueKind.Binary || string.IsNullOrEmpty(value.Data)) return false;
        try
        {
            var bytes = Convert.FromBase64String(value.Data);
            return bytes.Length > 0 && (bytes[0] & 1) == 1;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public override StateValue Desired(StateValue current) => IsDisabled(current) ? current : DisabledValue;

    public override string Describe() => $"{Target} = desabilitado";
}

public enum StartupClass { Protected, CloudSync, Launcher, DevTool, Unknown }

/// <summary>
/// Monta ajustes a partir do que existe NESTE PC: itens de inicialização e tarefas de updaters de terceiros.
/// Regras por nome/comando; tudo que não é reconhecido aparece só para escolha manual.
/// </summary>
public static class DynamicCatalog
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Nunca oferecidos: segurança, anti-cheat, drivers e periféricos.
    private static readonly Regex ProtectedRule = new(
        @"SecurityHealth|Defender|antivir|avast|\bavg\b|kaspersky|bitdefender|\beset\b|norton|mcafee|malwarebytes|sophos|" +
        @"vanguard|vgtray|easyanticheat|battleye|faceit|" +
        @"realtek|rtkaud|\bnvidia|nvcpl|nvbackend|\bamd\b|radeon|igfx|intel(?!li)|synaptics|\belan\b|wacom|ctfmon|" +
        @"logitech|lghub|razer(?!\s*cortex)|icue|corsair|steelseries|hyperx|armoury|asus|msi\s*center|dragon\s*center|gigabyte|asrock|" +
        @"bluetooth|audio|sound|touchpad|hotkey|leveagent|leve\.agent", Options);

    private static readonly Regex CloudSyncRule = new(
        @"onedrive|google\s*drive|googledrivefs|dropbox|icloud|megasync|nextcloud|syncthing|box\s*drive|pcloud", Options);

    private static readonly Regex DevToolRule = new(
        @"docker|jetbrains|toolbox|slack|postman|github\s*desktop|ollama|wsl", Options);

    private static readonly Regex LauncherRule = new(
        @"steam|epicgames|epic\s*games|eadesktop|ea\s*app|\borigin\b|ubisoft|\bupc\.exe|battle\.net|battlenet|galaxyclient|\bgog\b|riotclient|riot\s*client|" +
        @"discord|spotify|teams|skype|\bzoom\b|whatsapp|telegram|" +
        @"microsoftedgeautolaunch|msedge|\bopera\b|brave|chrome|vivaldi|firefox|" +
        @"adobe|creative\s*cloud|acrotray|ccleaner|itunes|cortana|copilot|utorrent|bittorrent|overwolf|medal|razer\s*cortex|" +
        @"lync|webex", Options);
    // Acesso remoto (AnyDesk, TeamViewer, Parsec) fica de fora de propósito: desligar pode cortar quem acessa o PC de longe.

    public static StartupClass Classify(StartupEntry entry)
    {
        var text = entry.Name + " " + ExecutableName(entry.Command);
        if (ProtectedRule.IsMatch(text)) return StartupClass.Protected;
        if (CloudSyncRule.IsMatch(text)) return StartupClass.CloudSync;
        if (DevToolRule.IsMatch(text)) return StartupClass.DevTool;
        if (LauncherRule.IsMatch(text)) return StartupClass.Launcher;
        return StartupClass.Unknown;
    }

    /// <summary>Só o nome do executável: "C:\Users\ASUS\...\app.exe --x" → "app.exe".</summary>
    internal static string ExecutableName(string command)
    {
        var text = command.Trim();
        string path;
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            path = end > 0 ? text[1..end] : text.Trim('"');
        }
        else
        {
            var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            path = exe >= 0 ? text[..(exe + 4)] : text.Split(' ', 2)[0];
        }
        var slash = path.LastIndexOfAny(['\\', '/']);
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    public static (IReadOnlyList<Tweak> Tweaks, int ProtectedCount) FromStartup(IEnumerable<StartupEntry> entries)
    {
        var tweaks = new List<Tweak>();
        var ids = new HashSet<string>();
        var protectedCount = 0;

        foreach (var entry in entries)
        {
            var kind = Classify(entry);
            if (kind == StartupClass.Protected)
            {
                protectedCount++;
                continue;
            }

            var id = $"startup.{entry.Source.ToString().ToLowerInvariant()}.{Slug(entry.Name)}";
            if (!ids.Add(id)) continue;

            tweaks.Add(new Tweak
            {
                Id = id,
                Title = $"{Pretty(entry.Name)} ao iniciar",
                Area = Area.Startup,
                Description = $"Não abre mais com o Windows; você abre quando quiser. Comando: {Shorten(entry.Command, 140)}",
                Risk = kind == StartupClass.Unknown ? Risk.Medium : Risk.Low,
                Profiles = kind switch
                {
                    StartupClass.Launcher => Profiles.All,
                    StartupClass.DevTool => Profiles.Ultra,
                    _ => Profiles.None,
                },
                Restart = Restart.Logoff,
                Note = kind switch
                {
                    StartupClass.CloudSync => "sincronização só roda com o app aberto (por isso não vem marcado)",
                    StartupClass.Unknown => "não reconhecido: confira o comando antes de desligar",
                    _ => null,
                },
                Actions = [new DisableStartupEntry(entry.ApprovalTarget)],
            });
        }

        return (tweaks, protectedCount);
    }

    private static readonly (Regex Rule, string Title)[] UpdaterRules =
    [
        (new Regex(@"^GoogleUpdate|^GoogleUpdater", Options), "Google Update"),
        (new Regex(@"^MicrosoftEdgeUpdate", Options), "Edge Update"),
        (new Regex(@"^Adobe Acrobat Update|^AdobeGCInvoker|^Adobe.*Update", Options), "Adobe Update"),
        (new Regex(@"^OneDrive (Reporting|Standalone Update|Per-Machine Standalone Update)", Options), "OneDrive Update"),
        (new Regex(@"^Opera( GX)? scheduled assistant|^Opera.*Autoupdate", Options), "Opera Update"),
        (new Regex(@"^BraveSoftwareUpdate", Options), "Brave Update"),
        (new Regex(@"^CCleaner", Options), "CCleaner"),
        (new Regex(@"^Firefox (Default Browser Agent|Background Update)", Options), "Firefox Update"),
    ];

    /// <summary>Tarefas da raiz do Agendador que são updaters de terceiros conhecidos. O resto não é oferecido.</summary>
    public static IReadOnlyList<Tweak> FromTasks(IEnumerable<string> taskPaths)
    {
        var tweaks = new List<Tweak>();
        var ids = new HashSet<string>();

        foreach (var path in taskPaths)
        {
            var name = path[(path.LastIndexOf('\\') + 1)..];
            var rule = UpdaterRules.FirstOrDefault(r => r.Rule.IsMatch(name));
            if (rule.Rule is null) continue;

            var id = $"updater.{Slug(name)}";
            if (!ids.Add(id)) continue;

            tweaks.Add(new Tweak
            {
                Id = id,
                Title = $"{rule.Title}: {Shorten(name, 48)}",
                Area = Area.Startup,
                Description = "Tarefa agendada que acorda em segundo plano para buscar atualização. O app continua se atualizando quando você abre.",
                Profiles = Profiles.All,
                Actions = [new SetValue(Target.ScheduledTask(path), StateValue.TaskState(false))],
            });
        }

        return tweaks;
    }

    /// <summary>
    /// Um ajuste opt-in que tira as bibliotecas de jogos da varredura em tempo real do Defender.
    /// Só pastas de jogos detectadas; nunca raiz de unidade, Windows ou Program Files inteiro.
    /// </summary>
    public static Tweak? FromGameLibraries(IEnumerable<string> folders)
    {
        var safe = folders
            .Select(f => f.TrimEnd('\\', '/'))
            .Where(IsSafeExclusion)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (safe.Count == 0) return null;

        return new Tweak
        {
            Id = "security.defender-game-libraries",
            Title = $"Defender ignora {safe.Count} pasta(s) de jogos",
            Area = Area.Security,
            Description = "O antivírus deixa de escanear cada arquivo que o jogo lê: carregamento mais rápido e menos engasgo ao abrir áreas novas e compilar shaders. Pastas: " + string.Join("; ", safe),
            Risk = Risk.Medium,
            Profiles = Profiles.None,
            OptIn = true,
            Note = "arquivos dessas pastas não são escaneados ao abrir; a varredura agendada e o SmartScreen continuam",
            Actions = safe.Select(f => (TweakAction)new SetValue(Target.DefenderExclusion(f), StateValue.Flag(true))).ToList(),
        };
    }

    // Só pastas cujo FINAL é uma biblioteca de jogos conhecida. Qualquer outra coisa é recusada.
    private static readonly string[][] AllowedSuffixes =
    [
        ["steamapps", "common"],
        ["Epic Games"],
        ["XboxGames"],
        ["GOG Games"],
        ["Riot Games"],
        ["EA Games"],
        ["Ubisoft Game Launcher", "games"],
    ];

    // Aspas (inclusive as tipográficas que o PowerShell também aceita), curingas, nomes curtos 8.3 e variáveis.
    private const string ForbiddenChars = "\"'\u2018\u2019\u201A\u201B~*?%$`<>|";

    /// <summary>
    /// Regra da exclusão do Defender: caminho absoluto X:\..., sem "."/"..", sem segmento terminando em ponto ou espaço,
    /// sem caracteres especiais e terminando numa biblioteca de jogos conhecida.
    /// </summary>
    public static bool IsSafeExclusion(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        if (folder.Any(c => char.IsControl(c) || ForbiddenChars.Contains(c))) return false;

        var parts = folder.Replace('/', '\\').TrimEnd('\\').Split('\\');
        if (parts.Length < 2) return false;
        if (parts[0].Length != 2 || !char.IsAsciiLetter(parts[0][0]) || parts[0][1] != ':') return false;
        if (parts.Skip(1).Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.Contains(':'))) return false;
        if (parts[1].Equals("windows", StringComparison.OrdinalIgnoreCase)) return false;

        return AllowedSuffixes.Any(suffix =>
            parts.Length > suffix.Length &&
            suffix.Select((segment, i) => parts[parts.Length - suffix.Length + i].Equals(segment, StringComparison.OrdinalIgnoreCase)).All(x => x));
    }

    private static string Slug(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
            sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        return sb.ToString().Trim('-');
    }

    private static string Pretty(string name) =>
        name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";
}
