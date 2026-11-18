using Leve.Core;

namespace Leve.Agent;

internal static class Program
{
    public const string MutexName = @"Local\Leve.Agent.v1";
    public const string StopEventName = @"Local\Leve.Agent.Stop";

    private static int Main()
    {
        using var single = new Mutex(initiallyOwned: true, MutexName, out var first);
        if (!first) return 0; // já existe um agente nesta sessão

        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Leve");
        Directory.CreateDirectory(dataDir);
        var config = AgentConfig.Load(Path.Combine(dataDir, "agent.json")) ?? new AgentConfig();

        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);
        stop.Reset();

        using var agent = new GameModeAgent(config, dataDir);
        try
        {
            agent.Run(stop);
            return 0;
        }
        catch (Exception ex)
        {
            agent.Log($"erro inesperado: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            single.ReleaseMutex();
        }
    }
}
