using System.Globalization;
using System.Text.Json;

namespace Leve.Core;

/// <summary>Resumo de uma captura do PresentMon. Temperaturas são null quando o hardware não informa.</summary>
public sealed record FrameStats(
    int Frames,
    double AvgFps,
    double OnePercentLowFps,
    double P99FrameTimeMs,
    IReadOnlyList<double> SegmentFps,
    double? GpuTempMax,
    double? CpuTempMax)
{
    public static FrameStats? Compute(IReadOnlyList<double> frameTimesMs, IReadOnlyList<double>? gpuTemps = null,
        IReadOnlyList<double>? cpuTemps = null, int segments = 6)
    {
        // Descarta lixo: frames de 0 ms ou travadas acima de 1 s (tela de carregamento) distorcem a média.
        var frames = frameTimesMs.Where(f => f > 0 && f < 1000).ToList();
        if (frames.Count < segments * 10) return null; // amostra pequena demais para concluir algo

        var avgFps = 1000.0 / frames.Average();
        var sorted = frames.OrderBy(f => f).ToList();
        var p99 = sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(sorted.Count * 0.99) - 1)];

        var size = frames.Count / segments;
        var segmentFps = Enumerable.Range(0, segments)
            .Select(i => 1000.0 / frames.Skip(i * size).Take(i == segments - 1 ? frames.Count - i * size : size).Average())
            .ToList();

        return new FrameStats(frames.Count, avgFps, 1000.0 / p99, p99, segmentFps, Max(gpuTemps), Max(cpuTemps));
    }

    private static double? Max(IReadOnlyList<double>? values)
    {
        var valid = values?.Where(v => v > 0 && v < 150).ToList();
        return valid is { Count: > 0 } ? valid.Max() : null;
    }
}

public enum CaptureLabel { A, B }

public sealed record CaptureRecord(CaptureLabel Label, string Game, DateTimeOffset At, int Seconds, FrameStats Stats);

public enum FpsVerdict { Better, Worse, Inconclusive }

public static class FpsComparison
{
    /// <summary>
    /// B (com otimização) contra A (sem). Só declara melhora/piora se a diferença passar de 3% E do ruído medido
    /// (variação entre os trechos de cada captura). O 1% low não pode cair junto.
    /// </summary>
    public static (FpsVerdict Verdict, string Text) Compare(FrameStats a, FrameStats b)
    {
        var deltaAvg = (b.AvgFps - a.AvgFps) / a.AvgFps;
        var deltaLow = (b.OnePercentLowFps - a.OnePercentLowFps) / a.OnePercentLowFps;
        var noise = 2 * Math.Sqrt(Variance(a.SegmentFps) / a.SegmentFps.Count + Variance(b.SegmentFps) / b.SegmentFps.Count) / a.AvgFps;
        var threshold = Math.Max(0.03, noise);

        var verdict =
            deltaAvg > threshold && deltaLow > -threshold ? FpsVerdict.Better :
            deltaAvg < -threshold || deltaLow < -threshold ? FpsVerdict.Worse :
            FpsVerdict.Inconclusive;

        var text = verdict switch
        {
            FpsVerdict.Better => "melhorou",
            FpsVerdict.Worse => "piorou",
            _ => "inconclusivo",
        };
        return (verdict, $"{text} · média {Signed(deltaAvg)} · 1% low {Signed(deltaLow)} · ruído ±{(threshold * 100).ToString("0.0", CultureInfo.InvariantCulture)}%");
    }

    private static double Variance(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return 0;
        var mean = values.Average();
        return values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
    }

    private static string Signed(double ratio) =>
        (ratio >= 0 ? "+" : "−") + Math.Abs(ratio * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
}

/// <summary>Leitura do CSV do PresentMon 1.x e 2.x (nomes de coluna variam entre versões).</summary>
public static class PresentMonCsv
{
    private static readonly string[] FrameTimeColumns = ["MsBetweenPresents", "msBetweenPresents", "FrameTime"];
    private static readonly string[] GpuTempColumns = ["GPUTemperature", "GpuTemperature"];
    private static readonly string[] CpuTempColumns = ["CPUTemperature", "CpuTemperature"];

    public static (List<double> FrameTimes, List<double> GpuTemps, List<double> CpuTemps) Parse(IEnumerable<string> lines)
    {
        var frameTimes = new List<double>();
        var gpu = new List<double>();
        var cpu = new List<double>();
        int frameIndex = -1, gpuIndex = -1, cpuIndex = -1;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cells = line.Split(',');

            if (frameIndex < 0)
            {
                frameIndex = IndexOf(cells, FrameTimeColumns);
                gpuIndex = IndexOf(cells, GpuTempColumns);
                cpuIndex = IndexOf(cells, CpuTempColumns);
                continue; // linha de cabeçalho (ou lixo antes dele)
            }

            if (TryCell(cells, frameIndex, out var ft)) frameTimes.Add(ft);
            if (TryCell(cells, gpuIndex, out var g)) gpu.Add(g);
            if (TryCell(cells, cpuIndex, out var c)) cpu.Add(c);
        }
        return (frameTimes, gpu, cpu);
    }

    private static int IndexOf(string[] header, string[] names)
    {
        for (var i = 0; i < header.Length; i++)
            if (names.Contains(header[i].Trim(), StringComparer.Ordinal)) return i;
        return -1;
    }

    private static bool TryCell(string[] cells, int index, out double value)
    {
        value = 0;
        return index >= 0 && index < cells.Length
            && double.TryParse(cells[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>Capturas guardadas em %LOCALAPPDATA%\Leve\fps.json (as 50 mais recentes).</summary>
public sealed class CaptureStore(string path)
{
    public List<CaptureRecord> Load()
    {
        try
        {
            return AtomicFile.ReadJson<List<CaptureRecord>>(path) ?? new List<CaptureRecord>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void Add(CaptureRecord record)
    {
        var all = Load();
        all.Add(record);
        AtomicFile.WriteJson(path, all.OrderByDescending(c => c.At).Take(50).ToList());
    }
}
