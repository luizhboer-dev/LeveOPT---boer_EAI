using System.IO;
using System.Windows;
using System.Windows.Threading;
using Leve.App.ViewModels;
using Leve.Core;
using Leve.Windows;

namespace Leve.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private bool _ownsMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, @"Global\Leve.App.v1", out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show("O Leve já está aberto.", "Leve", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Leve");
        Directory.CreateDirectory(dataDir);

        DispatcherUnhandledException += (_, args) => OnCrash(dataDir, args);

        var facts = FactsProvider.Collect();
        var store = new JsonSnapshotStore(Path.Combine(dataDir, "snapshots"));
        var access = new WindowsSystemAccess();
        var engine = new TweakEngine(access, store, ElevationGuard.CurrentSid());

        // Catálogo dinâmico: o que abre com o Windows e os updaters de terceiros DESTE PC.
        var ((startupTweaks, protectedCount), updaterTweaks, libraryFolders, adapters) = await Task.Run(() =>
            (DynamicCatalog.FromStartup(StartupScanner.Scan()),
             DynamicCatalog.FromTasks(TaskScanner.ScanRootTasks()),
             GameLibraryScanner.Scan(),
             NetworkAdapterScanner.Scan()));
        var gameLibraries = DynamicCatalog.FromGameLibraries(libraryFolders);
        var gameRules = new AgentConfig { GameFolders = libraryFolders.ToList() };

        // GPU por jogo só interessa com duas GPUs; a busca de executáveis é limitada e roda fora da UI.
        var gpuPerGame = facts.HasHybridGpu
            ? HardwareCatalog.GpuPerGame(await Task.Run(() => GameExecutableScanner.Scan(libraryFolders, gameRules)))
            : null;
        var bootLog = new List<LogLine>
        {
            LogLine.Info($"inicialização: {startupTweaks.Count} itens oferecidos, {protectedCount} protegidos (antivírus, drivers, anti-cheat, periféricos) · {updaterTweaks.Count} updaters"),
        };

        var catalog = Catalog.All.Concat(startupTweaks).Concat(updaterTweaks).Concat(HardwareCatalog.NetworkPowerSaving(adapters));
        if (gpuPerGame is not null) catalog = catalog.Append(gpuPerGame);
        // Exclusão do Defender só faz sentido com o Defender ativo (com outro antivírus, configure nele).
        if (gameLibraries is not null && facts.DefenderActive) catalog = catalog.Append(gameLibraries);

        // Modo Jogo dinâmico: o agente lê as bibliotecas do agent.json; se elas mudarem e ele estiver rodando, reinicia.
        var agentController = new AgentController(dataDir);
        AgentHooks? agentHooks = null;
        if (facts.ElevatedAsOtherUser)
        {
            bootLog.Add(LogLine.Warn("Leve elevado com outra conta: o Modo Jogo dinâmico fica indisponível nesta sessão"));
        }
        else if (AgentController.IsInstalled)
        {
            var configChanged = false;
            try
            {
                configChanged = agentController.WriteConfig(libraryFolders);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                bootLog.Add(LogLine.Warn($"não foi possível gravar agent.json: {ex.Message}"));
            }
            catalog = catalog.Append(AgentCatalog.Create(AgentController.ExePath));
            agentHooks = new AgentHooks(
                status =>
                {
                    if (status == TweakStatus.On)
                    {
                        if (configChanged && AgentController.IsRunning()) _ = AgentController.RestartAsync();
                        else AgentController.Start();
                        configChanged = false;
                    }
                    else
                    {
                        _ = Task.Run(AgentController.Stop); // pode esperar o agente terminar de iniciar
                    }
                },
                agentController.StatusText);
            bootLog.Add(LogLine.Info($"modo jogo: {libraryFolders.Count} bibliotecas de jogos monitoradas pelo agente"));
        }
        else
        {
            bootLog.Add(LogLine.Warn("Leve.Agent.exe não está ao lado do Leve.exe: o Modo Jogo dinâmico fica indisponível"));
        }

        var viewModel = new MainViewModel(engine, store, facts, catalog, new DiskCleaner(),
            () => SystemReport.Capture(access, StartupScanner.Scan()),
            new BaselineStore(Path.Combine(dataDir, "baseline.json")),
            dataDir, bootLog, agentHooks,
            new FpsTools(
                PresentMonRunner.Find,
                path => (PresentMonRunner.IsTrusted(path, out var reason), reason),
                PresentMonRunner.Capture,
                () => AgentStatus.Load(Path.Combine(dataDir, "agent-status.json")) is { InGame: true, Game: { } current } ? current : null,
                new CaptureStore(Path.Combine(dataDir, "fps.json")),
                PresentMonRunner.DownloadPage));

        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();

        await viewModel.InitializeAsync();
    }

    private static void OnCrash(string dataDir, DispatcherUnhandledExceptionEventArgs args)
    {
        try
        {
            File.AppendAllText(Path.Combine(dataDir, "crash.log"), $"{DateTime.Now:O}{Environment.NewLine}{args.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        MessageBox.Show(args.Exception.Message, "Leve — erro inesperado", MessageBoxButton.OK, MessageBoxImage.Error);
        args.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
