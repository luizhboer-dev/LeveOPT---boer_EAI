using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Leve.Core;
using Microsoft.Win32.SafeHandles;
using static Leve.Agent.AgentNative;

namespace Leve.Agent;

/// <summary>
/// Modo Jogo dinâmico.
///
/// Custo em repouso ≈ 0: não existe timer nem polling. A thread principal dorme em MsgWaitForMultipleObjectsEx
/// esperando (1) o evento de parada, (2) o handle do jogo — sinalizado quando ele fecha — e (3) o gancho de troca
/// de janela em primeiro plano (entregue dentro do PeekMessage). Cada troca de foco custa a leitura do caminho de UM processo.
///
/// Segurança do estado alheio:
/// - restaura só o que ainda está como o agente deixou (Chrome/Edge gerenciam o próprio EcoQoS);
/// - mantém do jogo apenas um handle de espera (SYNCHRONIZE), sem acesso de escrita durante a partida;
/// - grava um diário antes de rebaixar, para desfazer na próxima execução se o agente morrer no meio.
/// </summary>
internal sealed class GameModeAgent : IDisposable
{
    private sealed record Demoted(SafeProcessHandle Handle, string Name, int ProcessId, long StartTicks,
        uint OriginalPriority, bool EcoApplied, PROCESS_POWER_THROTTLING_STATE? OriginalThrottling);

    private readonly AgentConfig _config;
    private readonly string _statusPath;
    private readonly string _logPath;
    private readonly string _journalPath;
    private readonly WinEventProc _onForeground; // referência viva: o Windows chama este delegate
    private readonly List<Demoted> _demoted = [];
    private readonly int _session;

    private SafeProcessHandle? _game;      // só SYNCHRONIZE | QUERY_LIMITED
    private uint _gamePid;
    private string? _gameName;
    private uint _gameOriginalPriority;    // 0 = não alteramos a prioridade
    private bool _gameQosApplied;
    private uint _lastForegroundPid;

    public GameModeAgent(AgentConfig config, string dataDirectory)
    {
        _config = config;
        _statusPath = Path.Combine(dataDirectory, "agent-status.json");
        _logPath = Path.Combine(dataDirectory, "agent.log");
        _journalPath = Path.Combine(dataDirectory, "agent-demoted.json");
        using (var self = Process.GetCurrentProcess()) _session = self.SessionId;

        // Exceção atravessando um callback nativo derruba o processo sem passar por finally: nunca deixar escapar.
        _onForeground = (_, _, hwnd, _, _, _, _) =>
        {
            try
            {
                OnForeground(hwnd);
            }
            catch (Exception ex)
            {
                Log($"erro ao tratar troca de janela: {ex.GetType().Name}: {ex.Message}");
            }
        };
    }

    public void Run(EventWaitHandle stop)
    {
        LowerOwnProfile();
        RecoverFromJournal();
        Log($"agente iniciado · {_config.GameFolders.Count} bibliotecas de jogos");
        WriteStatus();

        var hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _onForeground, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (hook == IntPtr.Zero)
        {
            Log("não foi possível registrar o gancho de janelas; encerrando");
            return;
        }

        try
        {
            OnForeground(GetForegroundWindow()); // um jogo pode já estar aberto
            TrimOwnMemory();

            var stopHandle = stop.SafeWaitHandle.DangerousGetHandle();
            while (true)
            {
                var handles = _game is null ? new[] { stopHandle } : new[] { stopHandle, _game.DangerousGetHandle() };
                var result = MsgWaitForMultipleObjectsEx((uint)handles.Length, handles, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);

                if (result == WAIT_OBJECT_0) break; // parada pedida pelo app

                if (_game is not null && result == WAIT_OBJECT_0 + 1)
                {
                    ExitGameMode("jogo fechado");
                    OnForeground(GetForegroundWindow()); // o próximo jogo pode já estar em primeiro plano
                    continue;
                }

                if (result != WAIT_OBJECT_0 + (uint)handles.Length)
                {
                    Log($"retorno inesperado da espera ({result:X}); encerrando para nunca girar em falso");
                    break;
                }

                // Os callbacks WINEVENT_OUTOFCONTEXT são entregues dentro do PeekMessage.
                while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
            }
        }
        finally
        {
            UnhookWinEvent(hook);
            ExitGameMode("agente encerrado");
            Log("agente parado");
        }
    }

    // ── Detecção ─────────────────────────────────────────────────────────────

    private void OnForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == _lastForegroundPid) return;
        _lastForegroundPid = pid;
        if (pid == _gamePid) return;

        // Sem cache: o Windows reaproveita PIDs, e ler o caminho de um processo custa microssegundos.
        var path = ImagePath(pid);
        if (path is null || !GameRules.IsGame(path, _config)) return;

        // Outro jogo ganhou o foco (ex.: o launcher era quem estava sendo acompanhado): troca.
        if (_game is not null) ExitGameMode("troca de jogo");
        EnterGameMode(pid, path);
    }

    private static string? ImagePath(uint pid)
    {
        using var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle.IsInvalid) return null;
        var buffer = new StringBuilder(1024);
        var size = (uint)buffer.Capacity;
        return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString(0, (int)size) : null;
    }

    // ── Entrar / sair ────────────────────────────────────────────────────────

    private void EnterGameMode(uint pid, string path)
    {
        var wait = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (wait.IsInvalid)
        {
            wait.Dispose();
            Log($"jogo detectado mas inacessível: {GameRules.FileName(path)}");
            return;
        }

        _game = wait;
        _gamePid = pid;
        _gameName = GameRules.FileName(path);

        // Ajuste do jogo com um handle de curta duração: acima do normal (nunca alta/tempo real) e sem EcoQoS.
        using (var set = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_INFORMATION, false, pid))
        {
            if (!set.IsInvalid)
            {
                var original = GetPriorityClass(set);
                _gameOriginalPriority = original == NORMAL_PRIORITY_CLASS && SetPriorityClass(set, ABOVE_NORMAL_PRIORITY_CLASS) ? original : 0;
                var highQos = Throttling(control: true, eco: false);
                _gameQosApplied = SetProcessInformation(set, ProcessPowerThrottling, ref highQos, ThrottlingSize);
            }
        }

        DemoteBackground(pid);
        SaveJournal();

        Log($"modo jogo: {_gameName} · {_demoted.Count} apps rebaixados ({string.Join(", ", _demoted.Select(d => d.Name).Distinct())})");
        WriteStatus();
        TrimOwnMemory();
    }

    private void DemoteBackground(uint gamePid)
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if ((uint)process.Id == gamePid || process.SessionId != _session) continue;
                    if (DemoteRules.TierFor(process.ProcessName) != DemoteTier.Heavy) continue;

                    var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_INFORMATION, false, (uint)process.Id);
                    if (handle.IsInvalid)
                    {
                        handle.Dispose();
                        continue;
                    }

                    var priority = GetPriorityClass(handle);
                    // Só rebaixa quem está em normal ou acima; quem já está baixo (ex.: abas de fundo do Chrome) fica como está.
                    if (priority is 0 or IDLE_PRIORITY_CLASS or BELOW_NORMAL_PRIORITY_CLASS || !SetPriorityClass(handle, BELOW_NORMAL_PRIORITY_CLASS))
                    {
                        handle.Dispose();
                        continue;
                    }

                    var current = Throttling(false, false);
                    PROCESS_POWER_THROTTLING_STATE? before =
                        GetProcessInformation(handle, ProcessPowerThrottling, ref current, ThrottlingSize) ? current : null;
                    var ecoState = Throttling(control: true, eco: true);
                    var eco = SetProcessInformation(handle, ProcessPowerThrottling, ref ecoState, ThrottlingSize);

                    _demoted.Add(new Demoted(handle, process.ProcessName, process.Id, StartTicks(process), priority, eco, before));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // processo terminou ou ficou inacessível durante a listagem
                }
            }
        }
    }

    private static long StartTicks(Process process)
    {
        try { return process.StartTime.ToUniversalTime().Ticks; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return 0; }
    }

    private void ExitGameMode(string reason)
    {
        if (_game is null) return;

        var restored = 0;
        foreach (var d in _demoted)
        {
            if (Restore(d.Handle, d.OriginalPriority, d.EcoApplied, d.OriginalThrottling)) restored++;
            d.Handle.Dispose();
        }
        var count = _demoted.Count;
        _demoted.Clear();
        DeleteJournal();

        using (var set = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_INFORMATION, false, _gamePid))
        {
            if (!set.IsInvalid)
            {
                // Só desfaz o que nós mesmos fizemos, e só se o jogo ainda estiver como deixamos.
                if (_gameOriginalPriority != 0 && GetPriorityClass(set) == ABOVE_NORMAL_PRIORITY_CLASS)
                    SetPriorityClass(set, _gameOriginalPriority);
                if (_gameQosApplied)
                {
                    var defaults = Throttling(false, false);
                    SetProcessInformation(set, ProcessPowerThrottling, ref defaults, ThrottlingSize);
                }
            }
        }

        _game.Dispose();
        _game = null;
        Log($"fim do modo jogo ({reason}): {_gameName} · {restored}/{count} apps restaurados");
        _gameName = null;
        _gamePid = 0;
        _gameOriginalPriority = 0;
        _gameQosApplied = false;
        _lastForegroundPid = 0;
        WriteStatus();
        TrimOwnMemory();
    }

    /// <summary>Restaura prioridade/EcoQoS só se o processo ainda estiver exatamente como o agente deixou.</summary>
    private static bool Restore(SafeProcessHandle handle, uint originalPriority, bool ecoApplied, PROCESS_POWER_THROTTLING_STATE? originalThrottling)
    {
        var touched = false;
        if (GetPriorityClass(handle) == BELOW_NORMAL_PRIORITY_CLASS)
            touched = SetPriorityClass(handle, originalPriority);

        if (ecoApplied)
        {
            var current = Throttling(false, false);
            var readable = GetProcessInformation(handle, ProcessPowerThrottling, ref current, ThrottlingSize);
            var stillOurs = readable && current.ControlMask == PROCESS_POWER_THROTTLING_EXECUTION_SPEED
                                     && current.StateMask == PROCESS_POWER_THROTTLING_EXECUTION_SPEED;
            // Sem conseguir ler o estado atual, só restaura se tivermos o estado original exato.
            if (stillOurs || (!readable && originalThrottling is not null))
            {
                var restore = originalThrottling ?? Throttling(false, false);
                touched |= SetProcessInformation(handle, ProcessPowerThrottling, ref restore, ThrottlingSize);
            }
        }
        return touched;
    }

    // ── Diário (recuperação se o agente morrer no meio da partida) ───────────

    private void SaveJournal()
    {
        try
        {
            var entries = _demoted.Select(d => new DemotionJournalEntry(d.ProcessId, d.StartTicks, d.OriginalPriority, d.EcoApplied,
                d.OriginalThrottling?.ControlMask, d.OriginalThrottling?.StateMask)).ToList();
            AtomicFile.WriteJson(_journalPath, entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"não foi possível gravar o diário: {ex.Message}");
        }
    }

    private void DeleteJournal()
    {
        try { File.Delete(_journalPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void RecoverFromJournal()
    {
        List<DemotionJournalEntry>? entries;
        try
        {
            entries = AtomicFile.ReadJson<List<DemotionJournalEntry>>(_journalPath);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            entries = null;
        }
        if (entries is null) return;

        var restored = 0;
        foreach (var entry in entries)
        {
            try
            {
                using var process = Process.GetProcessById(entry.ProcessId);
                if (entry.StartTimeTicks == 0 || StartTicks(process) != entry.StartTimeTicks) continue; // PID reaproveitado: outro processo
                using var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_INFORMATION, false, (uint)entry.ProcessId);
                if (handle.IsInvalid) continue;
                PROCESS_POWER_THROTTLING_STATE? original = entry.OriginalControlMask is { } control && entry.OriginalStateMask is { } state
                    ? new PROCESS_POWER_THROTTLING_STATE { Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION, ControlMask = control, StateMask = state }
                    : null;
                if (Restore(handle, entry.OriginalPriority, entry.EcoApplied, original)) restored++;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // processo já fechou: nada a desfazer
            }
        }
        DeleteJournal();
        Log($"recuperação: o agente anterior parou no meio de um jogo; {restored} apps restaurados");
    }

    // ── O agente cuida da própria pegada ─────────────────────────────────────

    private static void LowerOwnProfile()
    {
        // Prioridade normal (detectar o jogo rápido mesmo com a CPU cheia) + EcoQoS (núcleos de eficiência, clock baixo).
        var eco = Throttling(control: true, eco: true);
        using var handle = new SafeProcessHandle(GetCurrentProcess(), ownsHandle: false);
        SetProcessInformation(handle, ProcessPowerThrottling, ref eco, ThrottlingSize);
    }

    private static void TrimOwnMemory()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        EmptyWorkingSet(GetCurrentProcess());
    }

    private void WriteStatus()
    {
        try
        {
            new AgentStatus(_game is not null, _gameName, _demoted.Count, DateTimeOffset.Now).Save(_statusPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Log(string text)
    {
        try
        {
            var info = new FileInfo(_logPath);
            if (info.Exists && info.Length > 512 * 1024) File.Delete(_logPath); // log nunca cresce sem limite
            File.AppendAllText(_logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        ExitGameMode("descarte");
    }
}
