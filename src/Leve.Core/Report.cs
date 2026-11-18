using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Leve.Core;

/// <summary>Fotografia do sistema para o relatório de antes e depois. Em todas as métricas, menor é melhor.</summary>
public sealed record MetricsReport(
    DateTimeOffset At,
    int Processes,
    long RamUsedMb,
    long CommitMb,
    int ServicesRunning,
    int StartupEnabled);

public enum Trend { Better, Same, Worse }

public sealed record ReportRow(string Label, string Before, string After, string Delta, Trend Trend);

public static class MetricsComparison
{
    public static IReadOnlyList<ReportRow> Compare(MetricsReport before, MetricsReport after) =>
    [
        Row("processos", before.Processes, after.Processes, n => n.ToString(CultureInfo.InvariantCulture)),
        Row("RAM em uso", before.RamUsedMb, after.RamUsedMb, Mb),
        Row("memória comprometida", before.CommitMb, after.CommitMb, Mb),
        Row("serviços rodando", before.ServicesRunning, after.ServicesRunning, n => n.ToString(CultureInfo.InvariantCulture)),
        Row("apps na inicialização", before.StartupEnabled, after.StartupEnabled, n => n.ToString(CultureInfo.InvariantCulture)),
    ];

    public static string ToText(MetricsReport before, MetricsReport after)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Leve — relatório  (antes: {before.At.LocalDateTime:dd/MM HH:mm} · agora: {after.At.LocalDateTime:dd/MM HH:mm})");
        foreach (var row in Compare(before, after))
            sb.AppendLine($"{row.Label,-24}{row.Before,12}{row.After,12}{row.Delta,12}");
        return sb.ToString().TrimEnd();
    }

    private static ReportRow Row(string label, long before, long after, Func<long, string> format)
    {
        // -1 = métrica indisponível naquela medição: não inventa diferença.
        if (before < 0 || after < 0)
            return new ReportRow(label, before < 0 ? "?" : format(before), after < 0 ? "?" : format(after), "?", Trend.Same);

        var delta = after - before;
        var sign = delta > 0 ? "+" : delta < 0 ? "−" : "±";
        return new ReportRow(label, format(before), format(after), sign + format(Math.Abs(delta)),
            delta < 0 ? Trend.Better : delta > 0 ? Trend.Worse : Trend.Same);
    }

    private static string Mb(long mb) => mb >= 1024
        ? (mb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
        : mb.ToString(CultureInfo.InvariantCulture) + " MB";
}

/// <summary>A referência "antes" fica em %LOCALAPPDATA%\Leve\baseline.json e só muda quando o usuário pede.</summary>
public sealed class BaselineStore(string path)
{
    public MetricsReport? Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<MetricsReport>(File.ReadAllText(path), JsonSnapshotStore.Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(MetricsReport report)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(report, JsonSnapshotStore.Json));
        File.Move(temp, path, overwrite: true);
    }
}
