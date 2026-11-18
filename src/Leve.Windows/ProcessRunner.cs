using System.Diagnostics;

namespace Leve.Windows;

/// <summary>
/// Execução de ferramentas do próprio Windows, usada SÓ em escritas raras (remover/re-registrar app, criar plano de energia).
/// Caminho absoluto em System32, sem shell, com timeout. Leituras de estado nunca passam por aqui.
/// </summary>
internal static class ProcessRunner
{
    public static string System32(string relative) => Path.Combine(Environment.SystemDirectory, relative);

    public static string Powercfg => System32("powercfg.exe");

    public static string Run(string exe, string arguments, int timeoutMs = 60_000, IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
            info.Environment[name] = value;

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"não foi possível iniciar {Path.GetFileName(exe)}");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw new TimeoutException($"{Path.GetFileName(exe)} não respondeu em {timeoutMs / 1000}s");
        }

        process.WaitForExit(); // garante que a saída redirecionada terminou de chegar
        var output = stdout.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            var error = stderr.GetAwaiter().GetResult().Trim();
            var detail = string.IsNullOrEmpty(error) ? output.Trim() : error;
            throw new InvalidOperationException($"{Path.GetFileName(exe)} terminou com código {process.ExitCode}: {FirstLine(detail)}");
        }
        return output;
    }

    /// <summary>
    /// Windows PowerShell 5.1 (o módulo Appx funciona nele de forma nativa). O comando não pode conter aspas duplas.
    /// Dados variáveis (caminhos) devem ir em <paramref name="environment"/> e ser lidos no script como $env:NOME,
    /// nunca concatenados no comando: o PowerShell aceita aspas tipográficas como delimitador.
    /// </summary>
    public static string PowerShell(string command, int timeoutMs = 180_000, IReadOnlyDictionary<string, string>? environment = null)
    {
        if (command.Contains('"')) throw new ArgumentException("comando PowerShell não pode conter aspas duplas", nameof(command));
        return Run(System32(@"WindowsPowerShell\v1.0\powershell.exe"),
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"", timeoutMs, environment);
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        return line.Length > 200 ? line[..200] + "…" : line;
    }
}
