namespace Leve.Core;

[Flags]
public enum Profiles
{
    None = 0,
    Ultra = 1,
    Dev = 2,
    All = Ultra | Dev,
}

public enum Risk { Low, Medium, High }

public enum Area { Privacy, Background, Services, Gaming, Visual, Apps, Startup, Power, Security }

public enum Restart { None, AppRestart, Logoff, Reboot }

public enum TweakStatus { Unknown, Off, Partial, On, NotPresent }

/// <summary>Onde o ganho aparece. FPS = média; Lows = 1% low/engasgos; Comfort = uso/sensação, não desempenho.</summary>
[Flags]
public enum Impact
{
    None = 0,
    Fps = 1,
    Lows = 2,
    Latency = 4,
    Ram = 8,
    Loading = 16,
    Network = 32,
    Privacy = 64,
    Comfort = 128,
}

/// <summary>Fatos da máquina usados para esconder ajustes que não se aplicam.</summary>
public sealed record SystemFacts
{
    public int Build { get; init; }
    public bool IsLaptop { get; init; }
    public bool HasPhysicalPrinter { get; init; }
    public bool ChromeInstalled { get; init; }

    /// <summary>O processo foi elevado com outra conta: HKCU apontaria para o usuário errado.</summary>
    public bool ElevatedAsOtherUser { get; init; }

    /// <summary>
    /// O PC tem a chave de integridade de memória (HVCI). Vale também depois de desligada pelo Leve,
    /// para o item continuar visível e reversível na lista.
    /// </summary>
    public bool HvciConfigured { get; init; }

    /// <summary>Duas GPUs (integrada + dedicada), típico de notebook gamer.</summary>
    public bool HasHybridGpu { get; init; }

    /// <summary>O Microsoft Defender é o antivírus ativo (com outro antivírus, exclusões do Defender não têm efeito).</summary>
    public bool DefenderActive { get; init; }
}

/// <summary>Uma mudança desejada sobre um alvo. Recebe o valor atual e devolve o valor desejado.</summary>
public abstract record TweakAction(Target Target)
{
    public abstract StateValue Desired(StateValue current);
    public abstract string Describe();
}

/// <summary>Define o alvo com um valor fixo.</summary>
public sealed record SetValue(Target Target, StateValue Value) : TweakAction(Target)
{
    public override StateValue Desired(StateValue current) => Value;
    public override string Describe() => $"{Target} = {Value}";
}

/// <summary>
/// Edita chaves dentro de um texto "k=v;k2=v2;" preservando as demais.
/// Usado em DirectXUserGlobalSettings, onde o Windows guarda várias opções gráficas no mesmo valor.
/// </summary>
public sealed record SetStringFlags(Target Target, IReadOnlyList<KeyValuePair<string, string>> Flags) : TweakAction(Target)
{
    public override StateValue Desired(StateValue current)
    {
        var text = current.Kind == ValueKind.String ? current.Data ?? string.Empty : string.Empty;
        var segments = text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        foreach (var (key, value) in Flags)
        {
            var index = segments.FindIndex(s => KeyOf(s).Equals(key, StringComparison.OrdinalIgnoreCase));
            var segment = $"{key}={value}";
            if (index >= 0) segments[index] = segment;
            else segments.Add(segment);
        }

        var result = string.Concat(segments.Select(s => s + ";"));
        return result == text ? current : StateValue.Text(result);
    }

    public override string Describe() => $"{Target} ⊇ {string.Concat(Flags.Select(f => $"{f.Key}={f.Value};"))}";

    private static string KeyOf(string segment)
    {
        var i = segment.IndexOf('=');
        return i < 0 ? segment : segment[..i];
    }
}

public sealed record Tweak
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required Area Area { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<TweakAction> Actions { get; init; }
    public Risk Risk { get; init; } = Risk.Low;
    public Profiles Profiles { get; init; } = Profiles.All;
    public Restart Restart { get; init; } = Restart.None;

    /// <summary>Quando falso, o ajuste fica oculto nesta máquina.</summary>
    public Func<SystemFacts, bool>? AppliesTo { get; init; }

    /// <summary>Efeito colateral que o usuário precisa saber, em uma linha.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// Troca segurança por desempenho. Nunca é marcado por perfil nem por "marcar todos":
    /// aparece como caixa separada na confirmação e só entra se o usuário marcar.
    /// </summary>
    public bool OptIn { get; init; }

    /// <summary>Onde o ganho aparece (etiqueta na lista).</summary>
    public Impact Impact { get; init; }

    /// <summary>Intensidade típica: 1 = pequena, 2 = média, 3 = grande.</summary>
    public int Strength { get; init; } = 1;

    /// <summary>Faixa típica observada em testes públicos. É estimativa, não promessa: o número real vem da tela > fps.</summary>
    public string? Gain { get; init; }

    public bool IsApplicable(SystemFacts facts) => AppliesTo?.Invoke(facts) ?? true;

    public bool TouchesUserHive => Actions.Any(a => a.Target.IsPerUser);
}
