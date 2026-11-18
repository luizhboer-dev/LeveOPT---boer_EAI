using System.Globalization;
using System.Text.Json.Serialization;

namespace Leve.Core;

public enum Hive { HKLM, HKCU }

/// <summary>Package = app AppX do usuário atual. Power = estado de energia (plano ativo, hibernação).</summary>
/// <summary>Display = taxa de atualização (Hz) de um monitor, ex.: \\.\DISPLAY1.</summary>
public enum TargetType { Registry, Service, ScheduledTask, Package, Power, DefenderExclusion, Display }

public enum ServiceStart { Boot, System, Automatic, AutomaticDelayed, Manual, Disabled }

/// <summary>
/// Absent: valor de Registro inexistente (reverter = apagar).
/// Missing: serviço ou tarefa que não existe nesta máquina (ignorado, nunca escrito).
/// Package: "Removed" ou "Installed:&lt;PackageFamilyName&gt;" (o nome da família permite re-registrar ao desfazer).
/// Flag: "On" / "Off".
/// </summary>
public enum ValueKind { Absent, Missing, DWord, QWord, String, ExpandString, MultiString, Binary, Service, TaskState, Package, Flag }

/// <summary>Algo que o Leve lê e escreve: um valor de Registro, o tipo de início de um serviço ou o estado de uma tarefa.</summary>
public sealed record Target(TargetType Type, Hive Hive, string Path, string Name)
{
    public static Target Reg(Hive hive, string path, string name) => new(TargetType.Registry, hive, path, name);
    public static Target Svc(string name) => new(TargetType.Service, Hive.HKLM, string.Empty, name);
    public static Target ScheduledTask(string fullPath) => new(TargetType.ScheduledTask, Hive.HKLM, fullPath, string.Empty);
    public static Target Package(string packageName) => new(TargetType.Package, Hive.HKCU, string.Empty, packageName);
    public static Target Power(string setting) => new(TargetType.Power, Hive.HKLM, string.Empty, setting);
    public static Target DefenderExclusion(string folder) => new(TargetType.DefenderExclusion, Hive.HKLM, folder, string.Empty);
    public static Target Display(string deviceName) => new(TargetType.Display, Hive.HKLM, string.Empty, deviceName);

    /// <summary>Chave de identidade (case-insensitive) usada para deduplicar alvos dentro de um lote.</summary>
    [JsonIgnore]
    public string Key => (Type switch
    {
        TargetType.Registry => $@"reg:{Hive}\{Path}\{Name}",
        TargetType.Service => $"svc:{Name}",
        TargetType.Package => $"pkg:{Name}",
        TargetType.Power => $"power:{Name}",
        TargetType.DefenderExclusion => $"excl:{Path.TrimEnd('\\')}",
        TargetType.Display => $"display:{Name}",
        _ => $"task:{Path}",
    }).ToLowerInvariant();

    [JsonIgnore]
    public bool IsPerUser => (Type == TargetType.Registry && Hive == Hive.HKCU) || Type == TargetType.Package;

    public override string ToString() => Type switch
    {
        TargetType.Registry => $@"{Hive}\{Path}\{Name}",
        TargetType.Service => $"serviço {Name}",
        TargetType.Package => $"app {Name}",
        TargetType.Power => $"energia: {Name}",
        TargetType.DefenderExclusion => $"Defender ignora {Path}",
        TargetType.Display => $"monitor {Name} (Hz)",
        _ => $"tarefa {Path}",
    };
}

/// <summary>Valor tipado e serializável. A igualdade compara tipo + dado, então DWORD 0 ≠ texto "0".</summary>
public sealed record StateValue(ValueKind Kind, string? Data)
{
    public static readonly StateValue Absent = new(ValueKind.Absent, null);
    public static readonly StateValue Missing = new(ValueKind.Missing, null);

    public static StateValue DWord(uint value) => new(ValueKind.DWord, value.ToString(CultureInfo.InvariantCulture));
    public static StateValue QWord(ulong value) => new(ValueKind.QWord, value.ToString(CultureInfo.InvariantCulture));
    public static StateValue Text(string value) => new(ValueKind.String, value);
    public static StateValue ServiceState(ServiceStart start) => new(ValueKind.Service, start.ToString());
    public static StateValue TaskState(bool enabled) => new(ValueKind.TaskState, enabled ? "Enabled" : "Disabled");
    public static StateValue Flag(bool on) => new(ValueKind.Flag, on ? "On" : "Off");
    public static readonly StateValue PackageRemoved = new(ValueKind.Package, "Removed");
    public static StateValue PackageInstalled(string familyName) => new(ValueKind.Package, "Installed:" + familyName);

    public ServiceStart AsService() => Enum.Parse<ServiceStart>(Data ?? throw new InvalidOperationException("serviço sem valor"));
    public bool AsTaskEnabled() => Data == "Enabled";
    public bool AsFlag() => Data == "On";

    /// <summary>Nome da família do pacote quando instalado; null quando removido.</summary>
    [JsonIgnore]
    public string? PackageFamily => Kind == ValueKind.Package && Data is { } d && d.StartsWith("Installed:", StringComparison.Ordinal)
        ? d["Installed:".Length..]
        : null;

    public override string ToString() => Kind switch
    {
        ValueKind.Absent => "(ausente)",
        ValueKind.Missing => "(não existe)",
        ValueKind.String or ValueKind.ExpandString => $"\"{Data}\"",
        ValueKind.MultiString => $"[{Data?.Replace('\0', ',')}]",
        ValueKind.Binary => $"binário({(Data?.Length ?? 0) * 3 / 4} bytes)",
        ValueKind.Package => PackageFamily is null ? "removido" : "instalado",
        _ => Data ?? string.Empty,
    };
}

public sealed class SystemAccessException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Fronteira com o sistema operacional. A implementação Windows fica em Leve.Windows; os testes usam uma falsa.</summary>
public interface ISystemAccess
{
    /// <summary>Lê o estado atual. Lança <see cref="SystemAccessException"/> se não conseguir ler — nunca devolve "ausente" por erro.</summary>
    StateValue Read(Target target);

    /// <summary>Escreve o valor. <see cref="ValueKind.Absent"/> apaga o valor de Registro.</summary>
    void Write(Target target, StateValue value);
}

public enum LogKind { Info, Ok, Warn, Error }

public sealed record LogLine(DateTime Time, LogKind Kind, string Text)
{
    public static LogLine Info(string text) => new(DateTime.Now, LogKind.Info, text);
    public static LogLine Ok(string text) => new(DateTime.Now, LogKind.Ok, text);
    public static LogLine Warn(string text) => new(DateTime.Now, LogKind.Warn, text);
    public static LogLine Error(string text) => new(DateTime.Now, LogKind.Error, text);
}
