using System.Globalization;

namespace Leve.Core;

/// <summary>Um monitor ativo: taxa atual e a maior taxa que ele aceita NA RESOLUÇÃO ATUAL.</summary>
public sealed record DisplayInfo(string DeviceName, string Description, int Width, int Height, int CurrentHz, int MaxHz)
{
    /// <summary>Margem de 5 Hz: um modo 59,94 aparece como 59 e 60 e não é "taxa errada".</summary>
    public bool BelowMax => MaxHz - CurrentHz >= 5;
}

/// <summary>Um pente de memória lido do SMBIOS (tipo 17).</summary>
public sealed record MemoryModule(int SizeMb, int RatedMts, int ConfiguredMts, MemoryKind Kind);

public enum MemoryKind { Unknown, Ddr3, Ddr4, Ddr5, Lpddr4, Lpddr5 }

public enum DiagnosticLevel { Ok, Info, Warn }

/// <summary>Uma linha da tela > diagnóstico. FixTweakId aponta para o ajuste que corrige (quando o Leve consegue).</summary>
public sealed record DiagnosticItem(string Title, string Detail, DiagnosticLevel Level, string? FixTweakId = null);

public sealed record DriveSpace(string Name, long FreeBytes, long TotalBytes, bool HasGames);

public sealed record DiagnosticInput(
    IReadOnlyList<DisplayInfo> Displays,
    IReadOnlyList<MemoryModule> Memory,
    DateTime? GpuDriverDate,
    string? GpuName,
    IReadOnlyList<DriveSpace> Drives,
    bool IsLaptop,
    DateTime Now);

public static class Diagnostics
{
    public static string RefreshTweakId(DisplayInfo display) =>
        "display.refresh." + new string(display.DeviceName.Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>
    /// Monitor abaixo da taxa nativa (ex.: 60 Hz num monitor de 144 Hz). Fora de perfil de propósito: trocar o modo
    /// de vídeo pede confirmação na tela ("manter / voltar em 15 s"), como o próprio Windows faz.
    /// </summary>
    public static Tweak RefreshTweak(DisplayInfo display) => new()
    {
        Id = RefreshTweakId(display),
        Title = $"Monitor {display.Description}: {display.CurrentHz} Hz → {display.MaxHz} Hz",
        Area = Area.Gaming,
        Description = $"O monitor aceita {display.MaxHz} Hz em {display.Width}×{display.Height}, mas está em {display.CurrentHz} Hz. "
                      + "Não muda o FPS do jogo; muda o quanto dele você VÊ: imagem mais fluida e menos latência.",
        Profiles = Profiles.None,
        Risk = Risk.Low,
        Impact = Impact.Latency,
        Strength = 3,
        Gain = $"fluidez: até {display.MaxHz} quadros/s visíveis em vez de {display.CurrentHz}",
        Note = "aplicado pela tela > diagnóstico, com confirmação; se a imagem não voltar, ela retorna sozinha em 15 s",
        Actions = [new SetValue(Target.Display(display.DeviceName), StateValue.DWord((uint)display.MaxHz))],
    };

    public static IReadOnlyList<DiagnosticItem> Build(DiagnosticInput input)
    {
        var items = new List<DiagnosticItem>();

        // ── Monitores ──────────────────────────────────────────────────────────
        foreach (var d in input.Displays)
        {
            items.Add(d.BelowMax
                ? new DiagnosticItem($"Monitor {d.Description} em {d.CurrentHz} Hz",
                    $"ele aceita {d.MaxHz} Hz nesta resolução ({d.Width}×{d.Height}). É o maior ganho de fluidez possível.",
                    DiagnosticLevel.Warn, RefreshTweakId(d))
                : new DiagnosticItem($"Monitor {d.Description} em {d.CurrentHz} Hz", "já está na taxa máxima.", DiagnosticLevel.Ok));

            if (d.MaxHz >= 100)
            {
                items.Add(new DiagnosticItem($"Limite de FPS para {d.MaxHz} Hz",
                    $"com G-Sync/FreeSync, limite o FPS em {d.MaxHz - 3} (no painel NVIDIA/AMD ou no jogo) e ligue Reflex/Anti-Lag quando o jogo tiver: "
                    + "menos latência e frametime estável. Sem VRR, ignore.",
                    DiagnosticLevel.Info));
            }
        }

        // ── Memória ────────────────────────────────────────────────────────────
        var installed = input.Memory.Where(m => m.SizeMb > 0).ToList();
        if (installed.Count > 0)
        {
            // Alguns firmwares informam MHz (1600 para DDR4-3200): metade da taxa nominal vira MT/s.
            var configured = installed.Min(m =>
                m.ConfiguredMts <= 0 ? m.RatedMts :
                m.RatedMts > 0 && m.ConfiguredMts * 2 <= m.RatedMts ? m.ConfiguredMts * 2 :
                m.ConfiguredMts);
            var kind = installed[0].Kind;
            var totalGb = installed.Sum(m => m.SizeMb) / 1024.0;
            var summary = $"{installed.Count} pente(s), {totalGb.ToString("0.#", CultureInfo.InvariantCulture)} GB {KindName(kind)} a {configured} MT/s";

            if (!input.IsLaptop && IsJedecDefault(kind, configured))
            {
                items.Add(new DiagnosticItem("RAM na velocidade padrão",
                    summary + ". Kits gamer costumam ser bem mais rápidos (DDR4 3200–3600, DDR5 6000+). Se a etiqueta do seu pente "
                    + "indica mais, ligue o XMP (Intel) ou EXPO (AMD) no BIOS: 5–15% de FPS em jogos limitados pela CPU. O Leve não mexe no BIOS.",
                    DiagnosticLevel.Warn));
            }
            else
            {
                items.Add(new DiagnosticItem("Memória", summary + ".", DiagnosticLevel.Ok));
            }

            if (installed.Count == 1 && !input.IsLaptop)
            {
                items.Add(new DiagnosticItem("Memória em canal único",
                    "só um pente: a placa-mãe roda em single channel. Dois pentes iguais (dual channel) dão 10–20% em jogos limitados pela CPU.",
                    DiagnosticLevel.Info));
            }
        }

        // ── Driver de vídeo ────────────────────────────────────────────────────
        if (input.GpuDriverDate is { } driverDate)
        {
            var age = (input.Now - driverDate).TotalDays;
            items.Add(age > 180
                ? new DiagnosticItem($"Driver de vídeo de {driverDate:dd'/'MM'/'yyyy}",
                    $"{input.GpuName ?? "GPU"}: o driver tem {age / 30:0} meses. Drivers novos trazem otimizações por jogo; atualize pelo app da NVIDIA/AMD/Intel.",
                    DiagnosticLevel.Warn)
                : new DiagnosticItem($"Driver de vídeo de {driverDate:dd'/'MM'/'yyyy}", $"{input.GpuName ?? "GPU"}: recente.", DiagnosticLevel.Ok));
        }

        // ── Disco ──────────────────────────────────────────────────────────────
        foreach (var drive in input.Drives.Where(d => d.TotalBytes > 0))
        {
            var freePercent = 100.0 * drive.FreeBytes / drive.TotalBytes;
            if (freePercent < 10)
            {
                items.Add(new DiagnosticItem($"Disco {drive.Name} quase cheio ({freePercent:0}% livre)",
                    (drive.HasGames ? "tem jogos instalados: " : string.Empty)
                    + "SSD muito cheio fica mais lento para gravar e o Windows perde espaço para cache e atualizações. Use > limpeza ou libere espaço.",
                    DiagnosticLevel.Warn));
            }
        }

        return items;
    }

    /// <summary>Velocidades JEDEC "de fábrica" sem XMP/EXPO.</summary>
    public static bool IsJedecDefault(MemoryKind kind, int mts) => kind switch
    {
        MemoryKind.Ddr4 => mts is > 0 and <= 2666,
        MemoryKind.Ddr5 => mts is > 0 and <= 4800,
        _ => false,
    };

    private static string KindName(MemoryKind kind) => kind switch
    {
        MemoryKind.Ddr3 => "DDR3",
        MemoryKind.Ddr4 => "DDR4",
        MemoryKind.Ddr5 => "DDR5",
        MemoryKind.Lpddr4 => "LPDDR4",
        MemoryKind.Lpddr5 => "LPDDR5",
        _ => "RAM",
    };
}

/// <summary>Leitura dos pentes de memória a partir da tabela SMBIOS bruta (GetSystemFirmwareTable 'RSMB').</summary>
public static class Smbios
{
    private const byte MemoryDeviceType = 17;
    private const byte EndOfTable = 127;

    public static IReadOnlyList<MemoryModule> ParseMemory(byte[] raw)
    {
        var modules = new List<MemoryModule>();
        if (raw.Length < 8) return modules;

        // RawSMBIOSData: 4 bytes de versão + DWORD tamanho, depois as estruturas.
        var length = BitConverter.ToInt32(raw, 4);
        var end = Math.Min(raw.Length, 8 + Math.Max(0, length));
        var offset = 8;

        while (offset + 4 <= end)
        {
            var type = raw[offset];
            var formatted = raw[offset + 1];
            if (formatted < 4 || offset + formatted > end) break;

            if (type == MemoryDeviceType && formatted >= 0x17)
                modules.Add(ParseMemoryDevice(raw, offset, formatted));
            if (type == EndOfTable) break;

            // Pula a área formatada e a lista de strings (termina em dois zeros).
            var next = offset + formatted;
            while (next + 1 < end && !(raw[next] == 0 && raw[next + 1] == 0)) next++;
            offset = next + 2;
        }
        return modules;
    }

    private static MemoryModule ParseMemoryDevice(byte[] raw, int offset, int formatted)
    {
        int Word(int at) => at + 2 <= formatted ? BitConverter.ToUInt16(raw, offset + at) : 0;
        int DWord(int at) => at + 4 <= formatted ? (int)BitConverter.ToUInt32(raw, offset + at) : 0;

        // Tamanho: 0 = vazio; 0xFFFF = desconhecido; 0x7FFF = usar o tamanho estendido (em MB).
        var size = Word(0x0C);
        var sizeMb = size switch
        {
            0 or 0xFFFF => 0,
            0x7FFF => DWord(0x1C),
            _ when (size & 0x8000) != 0 => (size & 0x7FFF) / 1024, // valor em KB
            _ => size,
        };

        var kind = raw[offset + 0x12] switch
        {
            0x18 => MemoryKind.Ddr3,
            0x1A => MemoryKind.Ddr4,
            0x22 => MemoryKind.Ddr5,
            0x1E => MemoryKind.Lpddr4,
            0x23 => MemoryKind.Lpddr5,
            _ => MemoryKind.Unknown,
        };

        // 0xFFFF = usar o campo estendido de 32 bits (SMBIOS 3.3+).
        var rated = Word(0x15);
        if (rated == 0xFFFF) rated = DWord(0x54);
        var configured = Word(0x20);
        if (configured == 0xFFFF) configured = DWord(0x58);

        return new MemoryModule(sizeMb, rated, configured, kind);
    }
}
