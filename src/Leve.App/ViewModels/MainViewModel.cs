using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Data;
using System.Windows.Threading;
using Leve.Core;
using Leve.Windows;

namespace Leve.App.ViewModels;

public enum ProfileKind { Ultra, Dev, Manual }

/// <summary>Ganchos do agente do Modo Jogo: sincronizar com o estado do ajuste e mostrar o status.</summary>
public sealed record AgentHooks(Action<TweakStatus> Sync, Func<string> StatusText);

/// <summary>Diagnóstico da v1.2: coleta (monitores, RAM, driver, discos) e o ajuste que corrige a taxa do monitor.</summary>
public sealed record DiagnosticTools(Func<DiagnosticInput> Collect, string TrialMarkerPath, Action<string, uint> SetDisplayHz);

/// <summary>Medição de FPS com o PresentMon (v1.1).</summary>
public sealed record FpsTools(
    Func<string?> FindPresentMon,
    Func<string, (bool Ok, string Reason)> CheckTrust,
    Func<string, string, int, FrameStats?> Capture,
    Func<string?> CurrentGame,
    CaptureStore Store,
    string DownloadPage);

public sealed class MainViewModel : ObservableObject
{
    private readonly TweakEngine _engine;
    private readonly ISnapshotStore _store;
    private readonly string _logPath;
    private readonly SystemMetrics _metrics = new();
    private readonly DispatcherTimer _timer;
    private readonly DiskCleaner _cleaner;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private bool _measurePending;
    private readonly AgentHooks? _agent;
    private readonly FpsTools? _fps;
    private readonly DiagnosticTools? _diagnostics;
    private readonly Dictionary<string, Tweak> _diagnosticFixes = new();
    private Snapshot? _displayTrial;
    private DispatcherTimer? _displayTimer;
    private int _displayCountdown;
    private string _keepDisplayText = string.Empty;
    private bool _showKeepDisplay;
    private string _fpsGame = string.Empty;
    private string _fpsSeconds = "60";
    private string _fpsStatus = string.Empty;
    private string _fpsLineA = "A  —";
    private string _fpsLineB = "B  —";
    private string _fpsVerdict = string.Empty;
    private string _agentText = string.Empty;
    private readonly Func<MetricsReport> _captureReport;
    private readonly BaselineStore _baselines;
    private MetricsReport? _lastReport;
    private string _reportInfo = "medindo…";
    private int _planVersion;
    private bool _planStale;

    private Plan? _pendingPlan;
    private ProfileKind _profile = ProfileKind.Manual;
    private Area? _area;
    private string _areaSlug = "todos";
    private string _filter = string.Empty;
    private TweakItemVm? _selectedItem;
    private NavKind _navKind = NavKind.Tweaks;
    private bool _showConfirm;
    private bool _isBusy;
    private string _confirmTitle = string.Empty;
    private string _confirmBody = string.Empty;
    private string _confirmFooter = string.Empty;
    private string _cpu = "--";
    private string _ram = "--";
    private string _procs = "--";

    public MainViewModel(TweakEngine engine, ISnapshotStore store, SystemFacts facts, IEnumerable<Tweak> catalog,
        DiskCleaner cleaner, Func<MetricsReport> captureReport, BaselineStore baselines, string dataDirectory,
        IEnumerable<LogLine>? bootLog = null, AgentHooks? agent = null, FpsTools? fps = null, DiagnosticTools? diagnostics = null)
    {
        _diagnostics = diagnostics;
        _agent = agent;
        _fps = fps;
        _engine = engine;
        _store = store;
        _cleaner = cleaner;
        _captureReport = captureReport;
        _baselines = baselines;
        _logPath = Path.Combine(dataDirectory, "leve.log");

        var hidden = 0;
        foreach (var tweak in catalog)
        {
            if (!tweak.IsApplicable(facts))
            {
                hidden++;
                continue;
            }
            var item = new TweakItemVm(tweak, facts.ElevatedAsOtherUser && tweak.TouchesUserHive);
            item.CheckedChanged = OnCheckedChanged;
            Items.Add(item);
        }

        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = o => o is TweakItemVm i && Matches(i);

        Nav.Add(new NavItemVm(this, "todos", null, NavKind.Tweaks, Items.Count));
        foreach (var area in Enum.GetValues<Area>())
        {
            var count = Items.Count(i => i.Tweak.Area == area);
            if (count > 0) Nav.Add(new NavItemVm(this, Labels.Area(area), area, NavKind.Tweaks, count));
        }
        Nav.Add(new NavItemVm(this, "diagnóstico", null, NavKind.Diagnostics, 0));
        Nav.Add(new NavItemVm(this, "relatório", null, NavKind.Report, 0));
        Nav.Add(new NavItemVm(this, "fps", null, NavKind.Fps, 0));
        Nav.Add(new NavItemVm(this, "limpeza", null, NavKind.Cleanup, 0));
        Nav.Add(new NavItemVm(this, "snapshots", null, NavKind.Snapshots, 0));

        foreach (var category in cleaner.Categories)
        {
            var cleanupItem = new CleanupItemVm(category);
            cleanupItem.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CleanupItemVm.IsChecked)) OnPropertyChanged(nameof(Header));
            };
            CleanupItems.Add(cleanupItem);
        }

        ApplyCommand = new RelayCommand(_ => OpenConfirm());
        UltraNowCommand = new RelayCommand(_ =>
        {
            Profile = ProfileKind.Ultra;
            foreach (var item in Items.Where(i => i.Tweak.OptIn)) item.IsChecked = false; // opt-in sempre começa desmarcado
            OpenConfirm();
        });
        ConfirmApplyCommand = new RelayCommand(_ => ConfirmApply());
        CancelConfirmCommand = new RelayCommand(_ => { ShowConfirm = false; _pendingPlan = null; });
        CheckAllCommand = new RelayCommand(_ => SetChecks(visibleOnly: true, value: true));
        UncheckAllCommand = new RelayCommand(_ => SetChecks(visibleOnly: true, value: false));
        UndoLastCommand = new RelayCommand(_ => UndoLast());
        UndoSelectedCommand = new RelayCommand(_ => UndoSelected());
        RevertSnapshotCommand = new RelayCommand(p => { if (p is SnapshotVm s) _ = RevertAsync(s.Snapshot, null); });
        RefreshCommand = new RelayCommand(async _ =>
        {
            if (IsBusy) return;
            if (ShowCleanup) await MeasureCleanupAsync();
            else if (ShowReport) await MeasureReportAsync();
            else if (ShowDiagnostics) await RunDiagnosticsAsync();
            else await RefreshStatusesAsync();
        });
        CleanCommand = new RelayCommand(_ => _ = CleanAsync());
        MeasureReportCommand = new RelayCommand(_ => _ = MeasureReportAsync());
        SetBaselineCommand = new RelayCommand(_ => SetBaseline());
        CopyReportCommand = new RelayCommand(_ => CopyReport());
        OpenDevDriveCommand = new RelayCommand(_ => OpenDevDrive());
        FixDiagnosticCommand = new RelayCommand(p => { if (p is DiagnosticItemVm d) _ = FixDiagnosticAsync(d); });
        KeepDisplayCommand = new RelayCommand(_ => KeepDisplay());
        RevertDisplayCommand = new RelayCommand(_ => _ = RevertDisplayAsync("você pediu para voltar"));
        CaptureACommand = new RelayCommand(_ => _ = CaptureAsync(CaptureLabel.A));
        CaptureBCommand = new RelayCommand(_ => _ = CaptureAsync(CaptureLabel.B));
        OpenPresentMonPageCommand = new RelayCommand(_ => OpenUrl(_fps?.DownloadPage));

        Nav[0].IsSelected = true;

        foreach (var line in bootLog ?? Enumerable.Empty<LogLine>()) AddLog(line);
        if (hidden > 0) AddLog(LogLine.Info($"{hidden} ajustes ocultos: não se aplicam a este PC"));
        if (facts.ElevatedAsOtherUser)
            AddLog(LogLine.Warn("Leve elevado com OUTRA conta: ajustes de usuário (HKCU) bloqueados. Faça o UAC com a sua conta."));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += (_, _) => SampleMetrics();
        _timer.Start();
        SampleMetrics();
    }

    // ── Coleções ─────────────────────────────────────────────────────────────

    public ObservableCollection<TweakItemVm> Items { get; } = [];
    public ICollectionView ItemsView { get; }
    public ObservableCollection<NavItemVm> Nav { get; } = [];
    public ObservableCollection<LogLine> Log { get; } = [];
    public ObservableCollection<SnapshotVm> Snapshots { get; } = [];

    // ── Comandos ─────────────────────────────────────────────────────────────

    public RelayCommand ApplyCommand { get; }
    public RelayCommand UltraNowCommand { get; }
    public RelayCommand ConfirmApplyCommand { get; }
    public RelayCommand CancelConfirmCommand { get; }
    public RelayCommand CheckAllCommand { get; }
    public RelayCommand UncheckAllCommand { get; }
    public RelayCommand UndoLastCommand { get; }
    public RelayCommand UndoSelectedCommand { get; }
    public RelayCommand RevertSnapshotCommand { get; }
    public RelayCommand RefreshCommand { get; }

    // ── Estado da tela ───────────────────────────────────────────────────────

    public ProfileKind Profile
    {
        get => _profile;
        set
        {
            if (!Set(ref _profile, value)) return;
            foreach (var item in Items)
            {
                item.IsChecked = value switch
                {
                    ProfileKind.Ultra => item.Tweak.Profiles.HasFlag(Profiles.Ultra),
                    ProfileKind.Dev => item.Tweak.Profiles.HasFlag(Profiles.Dev),
                    _ => false,
                };
            }
            OnPropertyChanged(nameof(ProfileLabel));
            OnPropertyChanged(nameof(IsUltra));
            OnPropertyChanged(nameof(IsDev));
            OnPropertyChanged(nameof(IsManual));
        }
    }

    public bool IsUltra { get => Profile == ProfileKind.Ultra; set { if (value) Profile = ProfileKind.Ultra; } }
    public bool IsDev { get => Profile == ProfileKind.Dev; set { if (value) Profile = ProfileKind.Dev; } }
    public bool IsManual { get => Profile == ProfileKind.Manual; set { if (value) Profile = ProfileKind.Manual; } }

    public string ProfileLabel => Profile switch
    {
        ProfileKind.Ultra => "Game Ultra",
        ProfileKind.Dev => "Gamer + Dev",
        _ => "Manual",
    };

    public int UltraCount => Items.Count(i => i.Tweak.Profiles.HasFlag(Profiles.Ultra));
    public int DevCount => Items.Count(i => i.Tweak.Profiles.HasFlag(Profiles.Dev));
    public int CheckedCount => Items.Count(i => i.IsChecked);
    public string ApplyLabel => $"enter  aplicar marcados ({CheckedCount})";

    public string FilterText
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value ?? string.Empty))
            {
                ItemsView.Refresh();
                OnPropertyChanged(nameof(Header));
            }
        }
    }

    public TweakItemVm? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (Set(ref _selectedItem, value)) OnPropertyChanged(nameof(ShowDetails));
        }
    }

    private NavKind CurrentNav
    {
        get => _navKind;
        set
        {
            if (!Set(ref _navKind, value)) return;
            OnPropertyChanged(nameof(ShowList));
            OnPropertyChanged(nameof(ShowSnapshots));
            OnPropertyChanged(nameof(ShowCleanup));
            OnPropertyChanged(nameof(ShowReport));
            OnPropertyChanged(nameof(ShowFps));
            OnPropertyChanged(nameof(ShowDiagnostics));
            OnPropertyChanged(nameof(ShowDetails));
        }
    }

    public bool ShowList => CurrentNav == NavKind.Tweaks;
    public bool ShowSnapshots => CurrentNav == NavKind.Snapshots;
    public bool ShowCleanup => CurrentNav == NavKind.Cleanup;
    public bool ShowReport => CurrentNav == NavKind.Report;
    public bool ShowFps => CurrentNav == NavKind.Fps;
    public bool ShowDiagnostics => CurrentNav == NavKind.Diagnostics;

    public ObservableCollection<DiagnosticItemVm> DiagnosticItems { get; } = [];
    public RelayCommand FixDiagnosticCommand { get; }
    public RelayCommand KeepDisplayCommand { get; }
    public RelayCommand RevertDisplayCommand { get; }
    public bool ShowKeepDisplay { get => _showKeepDisplay; private set => Set(ref _showKeepDisplay, value); }
    public string KeepDisplayText { get => _keepDisplayText; private set => Set(ref _keepDisplayText, value); }

    public string FpsGame { get => _fpsGame; set => Set(ref _fpsGame, value?.Trim() ?? string.Empty); }
    public string FpsSeconds { get => _fpsSeconds; set => Set(ref _fpsSeconds, value ?? string.Empty); }
    public string FpsStatus { get => _fpsStatus; private set => Set(ref _fpsStatus, value); }
    public string FpsLineA { get => _fpsLineA; private set => Set(ref _fpsLineA, value); }
    public string FpsLineB { get => _fpsLineB; private set => Set(ref _fpsLineB, value); }
    public string FpsVerdict { get => _fpsVerdict; private set => Set(ref _fpsVerdict, value); }
    public RelayCommand CaptureACommand { get; }
    public RelayCommand CaptureBCommand { get; }
    public RelayCommand OpenPresentMonPageCommand { get; }

    public ObservableCollection<ReportRow> ReportRows { get; } = [];
    public string ReportInfo { get => _reportInfo; private set => Set(ref _reportInfo, value); }
    public RelayCommand MeasureReportCommand { get; }
    public RelayCommand SetBaselineCommand { get; }
    public RelayCommand CopyReportCommand { get; }
    public RelayCommand OpenDevDriveCommand { get; }

    /// <summary>Ajustes opt-in (segurança × desempenho) oferecidos como caixas separadas na confirmação.</summary>
    public ObservableCollection<TweakItemVm> OptInItems { get; } = [];
    public bool HasOptIns => OptInItems.Count > 0;

    public ObservableCollection<CleanupItemVm> CleanupItems { get; } = [];
    public RelayCommand CleanCommand { get; }

    public bool ShowDetails => ShowList && SelectedItem is not null;

    public bool ShowConfirm { get => _showConfirm; private set => Set(ref _showConfirm, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(BusyText));
            // Medição da limpeza pedida enquanto outra operação rodava: executa assim que liberar.
            if (!value && _measurePending)
            {
                _measurePending = false;
                _dispatcher.InvokeAsync(() => _ = MeasureCleanupAsync());
            }
        }
    }

    public string BusyText => IsBusy ? "executando…" : string.Empty;

    public string ConfirmTitle { get => _confirmTitle; private set => Set(ref _confirmTitle, value); }
    public string ConfirmBody { get => _confirmBody; private set => Set(ref _confirmBody, value); }
    public string ConfirmFooter { get => _confirmFooter; private set => Set(ref _confirmFooter, value); }

    public string Cpu { get => _cpu; private set => Set(ref _cpu, value); }
    public string Ram { get => _ram; private set => Set(ref _ram, value); }
    public string Procs { get => _procs; private set => Set(ref _procs, value); }
    public string AgentText { get => _agentText; private set => Set(ref _agentText, value); }

    public string Header
    {
        get
        {
            if (ShowSnapshots) return $"~/snapshots  {Snapshots.Count} registros";
            if (ShowReport) return "~/relatório  antes × agora (menor é melhor em tudo)";
            if (ShowFps) return "~/fps  A sem otimização × B com otimização";
            if (ShowDiagnostics) return "~/diagnóstico  o que mais pesa no seu PC além do Windows";
            if (ShowCleanup)
            {
                var total = CleanupItems.Where(c => c.IsChecked).Sum(c => c.Size ?? 0);
                return $"~/limpeza  {Labels.Size(total)} recuperáveis nos itens marcados";
            }
            var visible = ItemsView.Cast<TweakItemVm>().ToList();
            var on = visible.Count(i => i.Status == TweakStatus.On);
            return $"~/{_areaSlug}  {visible.Count} itens · {on} ativos · {CheckedCount} marcados";
        }
    }

    // ── Ciclo de vida ────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        Profile = ProfileKind.Ultra; // Game Ultra já vem pronto: tudo marcado.
        LoadSnapshots();
        var interrupted = Snapshots.Count(s => s.Snapshot.Status == SnapshotStatus.Pending);
        if (interrupted > 0)
            AddLog(LogLine.Warn($"{interrupted} lote(s) interrompido(s) encontrado(s). Veja em snapshots e desfaça se quiser."));
        AddLog(LogLine.Info("perfil Game Ultra pré-selecionado · enter aplica · espaço marca · / filtra · u desfaz · ? ajuda"));
        await RecoverDisplayTrialAsync();

        // A referência "antes" é medida ANTES de qualquer aplicação possível (IsBusy segura o Enter).
        if (_baselines.Load() is null)
        {
            IsBusy = true;
            try
            {
                var baseline = await Task.Run(_captureReport);
                _baselines.Save(baseline);
                var appliedBefore = _store.List().Any(s => s.Status is SnapshotStatus.Committed or SnapshotStatus.Partial);
                AddLog(LogLine.Info($"referência \"antes\" gravada: {baseline.Processes} processos, {baseline.RamUsedMb} MB de RAM em uso, {baseline.ServicesRunning} serviços. Compare em > relatório depois de aplicar e reiniciar."));
                if (appliedBefore)
                    AddLog(LogLine.Warn("já havia lotes aplicados antes desta versão: a referência não é o Windows original"));
            }
            catch (Exception ex)
            {
                AddLog(LogLine.Warn($"não foi possível gravar a referência do relatório: {ex.Message}"));
            }
            finally
            {
                IsBusy = false;
            }
        }

        await RefreshStatusesAsync();
    }

    internal void SelectNav(NavItemVm nav)
    {
        CurrentNav = nav.Kind;
        _area = nav.Area;
        _areaSlug = nav.Label;
        if (nav.Kind == NavKind.Snapshots) LoadSnapshots();
        if (nav.Kind == NavKind.Cleanup && CleanupItems.Any(c => c.Size is null)) _ = MeasureCleanupAsync();
        if (nav.Kind == NavKind.Report) _ = MeasureReportAsync();
        if (nav.Kind == NavKind.Fps) PrepareFps();
        if (nav.Kind == NavKind.Diagnostics) _ = RunDiagnosticsAsync();
        ItemsView.Refresh();
        OnPropertyChanged(nameof(Header));
    }

    // ── Relatório de antes e depois ──────────────────────────────────────────

    private async Task MeasureReportAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var now = await Task.Run(_captureReport);
            _lastReport = now;
            var baseline = _baselines.Load();
            if (baseline is null)
            {
                _baselines.Save(now);
                baseline = now;
            }
            ShowReportRows(baseline, now);
        }
        catch (Exception ex)
        {
            AddLog(LogLine.Error($"falha ao medir: {ex.Message}"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowReportRows(MetricsReport baseline, MetricsReport now)
    {
        ReportRows.Clear();
        foreach (var row in MetricsComparison.Compare(baseline, now)) ReportRows.Add(row);
        ReportInfo = $"antes: {baseline.At.LocalDateTime:dd/MM HH:mm}    agora: {now.At.LocalDateTime:dd/MM HH:mm}";
    }

    private void SetBaseline()
    {
        if (_lastReport is null)
        {
            AddLog(LogLine.Warn("meça primeiro (r) para usar como referência"));
            return;
        }
        try
        {
            _baselines.Save(_lastReport);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddLog(LogLine.Error($"não foi possível gravar a referência: {ex.Message}"));
            return;
        }
        ShowReportRows(_lastReport, _lastReport);
        AddLog(LogLine.Ok($"nova referência \"antes\": {_lastReport.At.LocalDateTime:dd/MM HH:mm}"));
    }

    private void CopyReport()
    {
        var baseline = _baselines.Load();
        if (baseline is null || _lastReport is null)
        {
            AddLog(LogLine.Warn("meça primeiro (r) para copiar o relatório"));
            return;
        }
        try
        {
            System.Windows.Clipboard.SetText(MetricsComparison.ToText(baseline, _lastReport));
            AddLog(LogLine.Ok("relatório copiado para a área de transferência"));
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            AddLog(LogLine.Error($"área de transferência ocupada: {ex.Message}"));
        }
    }

    private void OpenDevDrive()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:disksandvolumes") { UseShellExecute = true });
            AddLog(LogLine.Info("Configurações > Discos e volumes > Criar Dev Drive. Use para repositórios e node_modules (perfil Dev)."));
        }
        catch (Exception ex)
        {
            AddLog(LogLine.Error($"não foi possível abrir as Configurações: {ex.Message}"));
        }
    }

    // ── Diagnóstico (v1.2) ───────────────────────────────────────────────────

    private async Task RunDiagnosticsAsync()
    {
        if (_diagnostics is null || IsBusy) return;
        IsBusy = true;
        try
        {
            var input = await Task.Run(_diagnostics.Collect);
            _diagnosticFixes.Clear();
            foreach (var display in input.Displays.Where(d => d.BelowMax))
            {
                var fix = Diagnostics.RefreshTweak(display);
                _diagnosticFixes[fix.Id] = fix;
            }

            DiagnosticItems.Clear();
            foreach (var item in Diagnostics.Build(input)) DiagnosticItems.Add(new DiagnosticItemVm(item));
            var warnings = DiagnosticItems.Count(d => d.Level == DiagnosticLevel.Warn);
            AddLog(warnings == 0 ? LogLine.Ok("diagnóstico: nada crítico encontrado") : LogLine.Warn($"diagnóstico: {warnings} ponto(s) de atenção"));
        }
        catch (Exception ex)
        {
            AddLog(LogLine.Error($"falha no diagnóstico: {ex.Message}"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Corrige pelo motor (snapshot + desfazer) e pede confirmação na tela, como o Windows faz ao trocar o modo de vídeo.</summary>
    private async Task FixDiagnosticAsync(DiagnosticItemVm item)
    {
        if (IsBusy || ShowKeepDisplay || item.Item.FixTweakId is not { } id || !_diagnosticFixes.TryGetValue(id, out var tweak)) return;
        IsBusy = true;
        var trialStarted = false;
        try
        {
            var progress = new Progress<LogLine>(AddLog);
            var plan = await Task.Run(() => _engine.BuildPlan([tweak]));
            var snapshot = await Task.Run(() => _engine.Apply(plan, tweak.Title, progress));
            if (snapshot is null)
            {
                AddLog(LogLine.Warn("nada a mudar: o monitor já está nessa taxa"));
                return;
            }
            // Mesmo "Partial" (driver arredondou 164/165) o modo pode ter mudado: o teste abre do mesmo jeito.
            SaveTrialMarker(snapshot.Id);

            _displayTrial = snapshot;
            trialStarted = true; // o app fica ocupado até manter/voltar: nada concorre com a volta automática
            _displayCountdown = 15;
            KeepDisplayText = $"A imagem está boa? Volta sozinho em {_displayCountdown} s.";
            ShowKeepDisplay = true;
            _displayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _displayTimer.Tick += (_, _) =>
            {
                _displayCountdown--;
                KeepDisplayText = $"A imagem está boa? Volta sozinho em {_displayCountdown} s.";
                if (_displayCountdown <= 0) _ = RevertDisplayAsync("sem confirmação em 15 s");
            };
            _displayTimer.Start();
        }
        catch (Exception ex)
        {
            AddLog(LogLine.Error($"falha ao corrigir: {ex.Message}"));
        }
        finally
        {
            if (!trialStarted) IsBusy = false;
        }
    }

    private void KeepDisplay()
    {
        if (_displayTrial is null) return;
        if (_displayCountdown > 13) return; // Enter nos 2 primeiros segundos pode ser o mesmo que abriu: só aceita depois
        StopDisplayTimer();
        AddLog(LogLine.Ok($"mantido: {_displayTrial.Label} · desfazer em > snapshots"));
        _displayTrial = null;
        DeleteTrialMarker();
        ShowKeepDisplay = false;
        IsBusy = false;
        LoadSnapshots();
        _ = RunDiagnosticsAsync();
    }

    /// <summary>Volta a taxa original. Usada pelo Esc, pelo fim dos 15 s e ao fechar a janela no meio do teste.</summary>
    public async Task RevertDisplayAsync(string reason)
    {
        if (_displayTrial is not { } trial) return;
        StopDisplayTimer();
        ShowKeepDisplay = false;
        AddLog(LogLine.Warn($"voltando o monitor ({reason})"));
        try
        {
            var result = await RevertAsync(trial, null, force: true);
            if (result?.Status != SnapshotStatus.Reverted) ForceOriginalRate(trial);
            DeleteTrialMarker();
        }
        catch (Exception ex)
        {
            AddLog(LogLine.Error($"não foi possível voltar a taxa: {ex.Message}"));
        }
        finally
        {
            _displayTrial = null;
            IsBusy = false;
        }
        _ = RunDiagnosticsAsync();
    }

    /// <summary>Se o driver arredondou a taxa, o desfazer normal "preserva" o valor: aqui grava o original direto.</summary>
    private void ForceOriginalRate(Snapshot trial)
    {
        if (_diagnostics is null) return;
        foreach (var entry in trial.Entries.Where(e => e.Target.Type == TargetType.Display && e.Original.Kind == ValueKind.DWord))
        {
            _diagnostics.SetDisplayHz(entry.Target.Name, uint.Parse(entry.Original.Data!, CultureInfo.InvariantCulture));
            AddLog(LogLine.Ok($"{entry.Target} ← {entry.Original}"));
        }
    }

    private void SaveTrialMarker(int snapshotId)
    {
        if (_diagnostics is null) return;
        try { File.WriteAllText(_diagnostics.TrialMarkerPath, snapshotId.ToString(CultureInfo.InvariantCulture)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void DeleteTrialMarker()
    {
        if (_diagnostics is null) return;
        try { File.Delete(_diagnostics.TrialMarkerPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>O Leve fechou/travou no meio de um teste de taxa: ao abrir de novo, volta a taxa anterior.</summary>
    private async Task RecoverDisplayTrialAsync()
    {
        if (_diagnostics is null || !File.Exists(_diagnostics.TrialMarkerPath)) return;
        try
        {
            if (int.TryParse(File.ReadAllText(_diagnostics.TrialMarkerPath), out var id)
                && _store.List().FirstOrDefault(s => s.Id == id) is { CanRevert: true } trial)
            {
                AddLog(LogLine.Warn("o Leve fechou no meio do teste de taxa do monitor: voltando a taxa anterior"));
                _displayTrial = trial;
                await RevertDisplayAsync("recuperação");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        DeleteTrialMarker();
    }

    private void StopDisplayTimer()
    {
        _displayTimer?.Stop();
        _displayTimer = null;
    }

    public bool IsDisplayTrialOpen => ShowKeepDisplay;

    // ── FPS (PresentMon): nada roda sobre o jogo; só o resultado no fim ──────

    private void PrepareFps()
    {
        if (_fps is null)
        {
            FpsStatus = "medição indisponível";
            return;
        }
        if (string.IsNullOrEmpty(FpsGame) && _fps.CurrentGame() is { } game) FpsGame = game;

        var presentMon = _fps.FindPresentMon();
        if (presentMon is null)
        {
            FpsStatus = "PresentMon não encontrado: coloque o PresentMon-x.y.z-x64.exe ao lado do Leve.exe";
        }
        else
        {
            var (ok, reason) = _fps.CheckTrust(presentMon);
            FpsStatus = ok
                ? $"pronto · {Path.GetFileName(presentMon)} · nada roda sobre o jogo, o resultado aparece no fim"
                : $"PresentMon recusado: {reason}";
        }
        ShowLastCaptures();
    }

    private void ShowLastCaptures()
    {
        if (_fps is null) return;
        var all = _fps.Store.Load();
        var a = all.FirstOrDefault(c => c.Label == CaptureLabel.A);
        var b = all.FirstOrDefault(c => c.Label == CaptureLabel.B);
        FpsLineA = a is null ? "A  —" : FormatCapture(a);
        FpsLineB = b is null ? "B  —" : FormatCapture(b);
        FpsVerdict = a is not null && b is not null && string.Equals(a.Game, b.Game, StringComparison.OrdinalIgnoreCase)
            ? "→ " + FpsComparison.Compare(a.Stats, b.Stats).Text
            : string.Empty;
    }

    private static string FormatCapture(CaptureRecord c)
    {
        // CPU não tem leitura sem driver de kernel (vetado); GPU vem do driver NVIDIA quando existe.
        var temps = c.Stats.GpuTempMax is { } gpu ? $" · GPU {Temp(gpu)}" : string.Empty;
        return $"{c.Label}  {c.At.LocalDateTime:dd/MM HH:mm}  {c.Game}  {c.Stats.AvgFps:0} fps · 1% low {c.Stats.OnePercentLowFps:0}{temps}";
    }

    private static string Temp(double? celsius) => celsius is { } t ? $"{t:0}°C" : "—";

    private async Task CaptureAsync(CaptureLabel label)
    {
        if (IsBusy || _fps is null) return;
        var presentMon = _fps.FindPresentMon();
        if (presentMon is null || !_fps.CheckTrust(presentMon).Ok)
        {
            PrepareFps();
            AddLog(LogLine.Warn(FpsStatus));
            return;
        }

        var game = FpsGame.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? FpsGame : FpsGame + ".exe";
        if (game.Length <= 4 || game.Any(c => char.IsControl(c) || c is '"' or '\\' or '/'))
        {
            AddLog(LogLine.Warn("informe o executável do jogo (ex.: cs2.exe)"));
            return;
        }
        if (!int.TryParse(FpsSeconds, out var seconds) || seconds is < 20 or > 300)
        {
            AddLog(LogLine.Warn("duração entre 20 e 300 segundos"));
            return;
        }

        IsBusy = true;
        try
        {
            AddLog(LogLine.Info($"captura {label}: volte para o jogo; começa em 10 s e dura {seconds} s (jogue no mesmo lugar nas duas)"));
            await Task.Delay(10_000); // tempo de voltar ao jogo sem o alt-tab entrar na medição
            var stats = await Task.Run(() => _fps.Capture(presentMon, game, seconds));
            if (stats is null)
            {
                AddLog(LogLine.Warn("poucos frames capturados: o jogo estava em primeiro plano?"));
                return;
            }
            _fps.Store.Add(new CaptureRecord(label, game, DateTimeOffset.Now, seconds, stats));
            ShowLastCaptures();
            AddLog(LogLine.Ok($"captura {label}: {stats.AvgFps:0} fps · 1% low {stats.OnePercentLowFps:0}"));
            if (FpsVerdict.Length > 0) AddLog(LogLine.Info(FpsVerdict));
        }
        catch (Exception ex)
        {
            AddLog(LogLine.Error($"falha na captura: {ex.Message}"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AddLog(LogLine.Error($"não foi possível abrir o navegador: {ex.Message}"));
        }
    }

    // ── Limpeza de disco (fora do snapshot: não tem volta) ───────────────────

    private async Task MeasureCleanupAsync()
    {
        if (IsBusy)
        {
            _measurePending = true;
            return;
        }
        IsBusy = true;
        try
        {
            foreach (var item in CleanupItems)
            {
                item.Size = null;
                var id = item.Category.Id;
                item.Size = await Task.Run(() => _cleaner.Measure(id));
                OnPropertyChanged(nameof(Header));
            }
        }
        catch (Exception ex)
        {
            AddLog(LogLine.Error($"falha ao medir: {ex.Message}"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CleanAsync()
    {
        if (IsBusy) return;
        var selected = CleanupItems.Where(c => c.IsChecked).ToList();
        if (selected.Count == 0)
        {
            AddLog(LogLine.Warn("nada marcado para limpar"));
            return;
        }

        IsBusy = true;
        long freed = 0;
        try
        {
            foreach (var item in selected)
            {
                var id = item.Category.Id;
                var result = await Task.Run(() => _cleaner.Clean(id));
                freed += result.FreedBytes;
                AddLog(result.SkippedFiles == 0
                    ? LogLine.Ok($"{item.Title}: {Labels.Size(result.FreedBytes)} liberados")
                    : LogLine.Ok($"{item.Title}: {Labels.Size(result.FreedBytes)} liberados ({result.SkippedFiles} em uso, ficaram)"));
            }
            AddLog(LogLine.Ok($"limpeza concluída: {Labels.Size(freed)} no total"));
        }
        catch (Exception ex)
        {
            AddLog(LogLine.Error($"falha na limpeza: {ex.Message}"));
        }
        finally
        {
            IsBusy = false;
        }
        await MeasureCleanupAsync();
    }

    public void ToggleSelected()
    {
        if (SelectedItem is { IsBlocked: false } item) item.IsChecked = !item.IsChecked;
    }

    public void CycleProfile() => Profile = Profile switch
    {
        ProfileKind.Ultra => ProfileKind.Dev,
        ProfileKind.Dev => ProfileKind.Manual,
        _ => ProfileKind.Ultra,
    };

    public void ShowHelp()
    {
        AddLog(LogLine.Info("teclas: ↑↓ navegar · espaço marcar · a marcar visíveis · x desmarcar visíveis"));
        AddLog(LogLine.Info("        enter aplicar · p trocar perfil · / filtrar · u desfazer último lote · r reler estado"));
    }

    private bool Matches(TweakItemVm item)
    {
        if (_area is { } area && item.Tweak.Area != area) return false;
        if (string.IsNullOrWhiteSpace(_filter)) return true;
        return Contains(item.Title, _filter) || Contains(item.Tweak.Description, _filter) || Contains(item.Tweak.Id, _filter);
    }

    private static bool Contains(string text, string term) =>
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, term.Trim(),
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    private void OnCheckedChanged()
    {
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(ApplyLabel));
        OnPropertyChanged(nameof(Header));
        if (ShowConfirm) _ = RebuildConfirmAsync();
    }

    private async Task RebuildConfirmAsync()
    {
        try
        {
            await BuildConfirmAsync();
        }
        catch (Exception ex)
        {
            _planStale = false;
            _pendingPlan = null;
            AddLog(LogLine.Error($"falha ao recalcular o plano: {ex.Message}"));
        }
    }

    /// <summary>Monta o plano lendo o estado atual. Devolve falso se uma reconstrução mais nova passou na frente.</summary>
    private async Task<bool> BuildConfirmAsync()
    {
        var selected = Items.Where(i => i.IsChecked).Select(i => i.Tweak).ToList();
        var version = ++_planVersion;
        _planStale = true;
        var plan = await Task.Run(() => _engine.BuildPlan(selected));
        if (version != _planVersion) return false;

        _pendingPlan = plan;
        _planStale = false;
        ConfirmTitle = $"Aplicar {ProfileLabel}?  ({selected.Count} ajustes marcados)";
        ConfirmBody = Summarize(plan);
        ConfirmFooter = plan.Changes.Count == 0
            ? "nada a fazer: tudo já está aplicado"
            : $"snapshot #{_store.NextId():D4} é gravado antes da primeira mudança · u desfaz o lote inteiro";
        return true;
    }

    private void SetChecks(bool visibleOnly, bool value)
    {
        var targets = visibleOnly ? ItemsView.Cast<TweakItemVm>() : Items;
        // "Marcar todos" nunca liga um opt-in de segurança; desmarcar vale para todos.
        foreach (var item in targets.Where(i => !value || !i.Tweak.OptIn).ToList()) item.IsChecked = value;
    }

    // ── Aplicar ──────────────────────────────────────────────────────────────

    private async void OpenConfirm()
    {
        if (IsBusy || ShowConfirm) return;
        var selected = Items.Where(i => i.IsChecked).Select(i => i.Tweak).ToList();
        if (selected.Count == 0)
        {
            AddLog(LogLine.Warn("nada marcado: use espaço, 'a' ou escolha um perfil"));
            return;
        }

        OptInItems.Clear();
        foreach (var item in Items.Where(i => i.Tweak.OptIn && !i.IsBlocked && i.Status != TweakStatus.On))
            OptInItems.Add(item);
        OnPropertyChanged(nameof(HasOptIns));

        IsBusy = true;
        try
        {
            if (await BuildConfirmAsync()) ShowConfirm = true;
        }
        catch (Exception ex)
        {
            AddLog(LogLine.Error($"falha ao montar o plano: {ex.Message}"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Summarize(Plan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join("   ", plan.Tweaks.GroupBy(t => t.Area).Select(g => $"+ {g.Count()} {Labels.Area(g.Key)}")));
        sb.AppendLine();
        sb.AppendLine($"{plan.Changes.Count} alterações reais · {plan.AlreadyApplied.Count} ajustes já estavam aplicados");

        var services = plan.Changes.Count(c => c.Target.Type == TargetType.Service);
        if (services > 0)
            sb.AppendLine($"{services} serviços mudam o tipo de início e são parados na hora");

        var apps = plan.Changes.Count(c => c.Target.Type == TargetType.Package);
        if (apps > 0)
            sb.AppendLine($"{apps} apps removidos da sua conta (alguns segundos cada; desfazer re-registra)");

        var startup = plan.Changes.Count(c => c.Target.Path.Contains("StartupApproved", StringComparison.OrdinalIgnoreCase));
        if (startup > 0)
            sb.AppendLine($"{startup} programas deixam de abrir com o Windows");

        var changing = plan.Changes.SelectMany(c => c.TweakIds).ToHashSet();
        var active = plan.Tweaks.Where(t => changing.Contains(t.Id)).ToList();

        var reboot = active.Where(t => t.Restart == Restart.Reboot).Select(t => t.Title).ToList();
        if (reboot.Count > 0) sb.AppendLine($"reiniciar o PC: {string.Join(", ", reboot)}");

        var logoff = active.Where(t => t.Restart == Restart.Logoff).Select(t => t.Title).ToList();
        if (logoff.Count > 0) sb.AppendLine($"sair e entrar: {string.Join(", ", logoff)}");

        var notes = active.Where(t => t.Note is not null).Select(t => $"! {t.Title}: {t.Note}").ToList();
        if (notes.Count > 0)
        {
            sb.AppendLine();
            foreach (var note in notes) sb.AppendLine(note);
        }

        if (plan.Issues.Count > 0)
            sb.AppendLine($"{plan.Issues.Count} alvos não puderam ser lidos e serão pulados");

        // Estimativa honesta: onde o ganho costuma aparecer. O número real vem da tela > fps.
        sb.AppendLine();
        sb.AppendLine("ganho esperado (estimativa; o número real você mede em > fps):");
        foreach (var big in active.Where(t => t.Strength >= 2 && (t.Impact & (Impact.Fps | Impact.Loading)) != 0))
            sb.AppendLine($"  ▲ {big.Title}: {big.Gain}");
        sb.AppendLine("  engasgos, 1% low e RAM livre: onde a melhora costuma aparecer");
        sb.AppendLine("  FPS médio: 0–5% típico; 5–15% quando a CPU ou programas de fundo eram o gargalo");

        return sb.ToString().TrimEnd();
    }

    private async void ConfirmApply()
    {
        if (!ShowConfirm || _pendingPlan is null) return;
        if (_planStale)
        {
            AddLog(LogLine.Warn("recalculando o plano com a sua escolha; aperte enter de novo em um instante"));
            return;
        }
        var plan = _pendingPlan;
        _pendingPlan = null;
        ShowConfirm = false;

        if (plan.Changes.Count == 0)
        {
            AddLog(LogLine.Ok("tudo já estava aplicado"));
            return;
        }

        IsBusy = true;
        try
        {
            var progress = new Progress<LogLine>(AddLog);
            var label = ProfileLabel;
            var snapshot = await Task.Run(() => _engine.Apply(plan, label, progress));
            await Task.Yield(); // deixa o log de progresso escoar antes do resumo

            if (snapshot is not null)
            {
                var ok = snapshot.Entries.Count(e => e.State == EntryState.Applied);
                var failed = snapshot.Entries.Count(e => e.State == EntryState.Failed);
                AddLog(failed == 0
                    ? LogLine.Ok($"snapshot #{snapshot.Id:D4}: {ok} alterações aplicadas e confirmadas")
                    : LogLine.Warn($"snapshot #{snapshot.Id:D4}: {ok} aplicadas, {failed} falharam (veja acima)"));

                var applied = snapshot.Entries.Where(e => e.State == EntryState.Applied).SelectMany(e => e.TweakIds).ToHashSet();
                var tweaks = plan.Tweaks.Where(t => applied.Contains(t.Id)).ToList();
                if (tweaks.Any(t => t.Restart == Restart.Reboot))
                    AddLog(LogLine.Warn("reinicie o PC para o efeito completo (serviços, HAGS, telemetria)"));
                else if (tweaks.Any(t => t.Restart == Restart.Logoff))
                    AddLog(LogLine.Warn("saia e entre de novo na conta para o efeito completo"));
            }
        }
        catch (Exception ex)
        {
            AddLog(LogLine.Error($"falha ao aplicar: {ex.Message}"));
        }
        finally
        {
            IsBusy = false;
            LoadSnapshots();
            await RefreshStatusesAsync();
        }
    }

    // ── Desfazer ─────────────────────────────────────────────────────────────

    private void UndoLast()
    {
        var last = _store.List().Where(s => s.CanRevert).MaxBy(s => s.Id);
        if (last is null)
        {
            AddLog(LogLine.Warn("nenhum lote para desfazer"));
            return;
        }
        _ = RevertAsync(last, null);
    }

    private async void UndoSelected()
    {
        if (SelectedItem is not { } item) return;
        var id = item.Tweak.Id;
        var snapshots = _store.List()
            .Where(s => s.CanRevert && s.Entries.Any(e => e.TweakIds.Contains(id) && e.State != EntryState.Reverted))
            .OrderByDescending(s => s.Id)
            .ToList();

        if (snapshots.Count == 0)
        {
            AddLog(LogLine.Warn($"{item.Title}: nada aplicado pelo Leve para desfazer"));
            return;
        }

        // Um alvo compartilhado (ex.: DirectXUserGlobalSettings com janela + VRR) não é revertido
        // por um ajuste só: voltaria o valor original e desligaria o outro junto.
        var shared = snapshots.SelectMany(s => s.Entries)
            .Where(e => e.TweakIds.Contains(id) && e.TweakIds.Count > 1 && e.State != EntryState.Reverted)
            .ToList();
        foreach (var entry in shared)
        {
            var others = string.Join(", ", entry.TweakIds.Where(t => t != id).Select(t => Catalog.Find(t)?.Title ?? t));
            AddLog(LogLine.Warn($"{entry.Target}: compartilhado com {others}; desfaça o lote inteiro para voltar este valor"));
        }

        foreach (var snapshot in snapshots)
            await RevertAsync(snapshot, e => e.TweakIds.Count == 1 && e.TweakIds[0] == id);
    }

    private async Task<Snapshot?> RevertAsync(Snapshot snapshot, Func<SnapshotEntry, bool>? filter, bool force = false)
    {
        if (IsBusy && !force) return null;
        IsBusy = true;
        Snapshot? reverted = null;
        try
        {
            AddLog(LogLine.Info($"desfazendo snapshot #{snapshot.Id:D4}{(filter is null ? string.Empty : " (só o ajuste selecionado)")}"));
            var progress = new Progress<LogLine>(AddLog);
            var result = await Task.Run(() => _engine.Revert(snapshot, progress, filter));
            reverted = result;
            await Task.Yield();

            var drifted = result.Entries.Count(e => e.State == EntryState.Drifted);
            var failed = result.Entries.Count(e => e.State == EntryState.RevertFailed);
            if (failed > 0) AddLog(LogLine.Error($"{failed} itens não voltaram; veja acima"));
            if (drifted > 0) AddLog(LogLine.Warn($"{drifted} itens foram mudados por fora e preservados"));
            if (result.Status == SnapshotStatus.Reverted) AddLog(LogLine.Ok($"snapshot #{snapshot.Id:D4} desfeito por completo"));
            else if (failed == 0) AddLog(LogLine.Ok($"snapshot #{snapshot.Id:D4}: itens selecionados desfeitos"));
        }
        catch (Exception ex)
        {
            AddLog(LogLine.Error($"falha ao desfazer: {ex.Message}"));
        }
        finally
        {
            IsBusy = false;
            LoadSnapshots();
            await RefreshStatusesAsync();
        }
        return reverted;
    }

    // ── Leituras ─────────────────────────────────────────────────────────────

    public async Task RefreshStatusesAsync()
    {
        var tweaks = Items.Select(i => i.Tweak).ToList();
        var statuses = await Task.Run(() => tweaks.Select(_engine.Evaluate).ToList());
        for (var i = 0; i < Items.Count && i < statuses.Count; i++) Items[i].Status = statuses[i];
        OnPropertyChanged(nameof(Header));

        // Ajuste do agente ligado → agente rodando; desligado/desfeito → agente parado.
        var agentItem = Items.FirstOrDefault(i => i.Tweak.Id == AgentCatalog.TweakId);
        if (agentItem is not null && _agent is not null)
        {
            try
            {
                _agent.Sync(agentItem.Status);
            }
            catch (Exception ex)
            {
                AddLog(LogLine.Warn($"agente: {ex.Message}"));
            }
        }
    }

    private void LoadSnapshots()
    {
        Snapshots.Clear();
        foreach (var snapshot in _store.List().OrderByDescending(s => s.Id)) Snapshots.Add(new SnapshotVm(snapshot));
        OnPropertyChanged(nameof(Header));
    }

    private bool _sampling;

    private async void SampleMetrics()
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            // Fora da thread da UI: enumerar processos custa alguns ms.
            var m = await Task.Run(() => _metrics.Sample());
            Cpu = $"{m.CpuPercent:0}%";
            Ram = $"{m.UsedBytes / 1073741824.0:0.0}/{m.TotalBytes / 1073741824.0:0}G";
            Procs = m.Processes.ToString(CultureInfo.InvariantCulture);
            if (_agent is not null) AgentText = _agent.StatusText();
        }
        catch (Exception)
        {
            // Métrica é decorativa: falha aqui nunca derruba a tela.
        }
        finally
        {
            _sampling = false;
        }
    }

    private void AddLog(LogLine line)
    {
        Log.Add(line);
        if (Log.Count > 500) Log.RemoveAt(0);
        try
        {
            File.AppendAllText(_logPath, $"{line.Time:yyyy-MM-dd HH:mm:ss} [{line.Kind}] {line.Text}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
