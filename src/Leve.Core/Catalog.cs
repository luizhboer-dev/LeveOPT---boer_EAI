using static Leve.Core.Hive;

namespace Leve.Core;

/// <summary>
/// Catálogo fechado do M1. Cada ajuste é só dado: alvos + valores desejados.
/// Fora daqui por decisão: limpeza de RAM/Standby List, limpador de Registro, Win32PrioritySeparation,
/// NetworkThrottlingIndex, Timer Resolution, Defender/Windows Update desligados, Registro de driver de GPU, bcdedit.
/// </summary>
public static class Catalog
{
    private const string Policies = @"SOFTWARE\Policies\Microsoft\Windows";
    private const string UserPolicies = @"Software\Policies\Microsoft\Windows";
    private const string CurrentVersion = @"Software\Microsoft\Windows\CurrentVersion";
    private const string ExplorerAdvanced = CurrentVersion + @"\Explorer\Advanced";
    private const string ContentDelivery = CurrentVersion + @"\ContentDeliveryManager";
    private const string DirectXPrefs = @"Software\Microsoft\DirectX\UserGpuPreferences";

    public static IReadOnlyList<Tweak> All { get; } = Build();

    public static Tweak? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    private static SetValue Dw(Hive hive, string path, string name, uint value) => new(Target.Reg(hive, path, name), StateValue.DWord(value));
    private static SetValue Sz(Hive hive, string path, string name, string value) => new(Target.Reg(hive, path, name), StateValue.Text(value));
    private static SetValue Svc(string name, ServiceStart start) => new(Target.Svc(name), StateValue.ServiceState(start));
    private static SetValue TaskOff(string path) => new(Target.ScheduledTask(path), StateValue.TaskState(false));

    private static Tweak DisableService(string id, string name, string title, string description, Risk risk = Risk.Low,
        Profiles profiles = Profiles.All, string? note = null, ServiceStart target = ServiceStart.Disabled) => new()
    {
        Id = id,
        Title = title,
        Area = Area.Services,
        Description = description,
        Risk = risk,
        Profiles = profiles,
        Restart = Restart.Reboot,
        Note = note,
        Actions = [Svc(name, target)],
    };

    private static List<Tweak> Build() =>
    [
        // ── Privacidade ───────────────────────────────────────────────────────────────
        new()
        {
            Id = "privacy.telemetry",
            Title = "Telemetria (DiagTrack)",
            Area = Area.Privacy,
            Description = "Desliga o serviço DiagTrack, o inventário de apps e as tarefas de coleta (CEIP, Appraiser, Feedback). No Home/Pro a política fica no mínimo \"Obrigatório\"; o ganho real vem do serviço parado.",
            Restart = Restart.Reboot,
            Actions =
            [
                Dw(HKLM, Policies + @"\DataCollection", "AllowTelemetry", 0),
                Dw(HKLM, Policies + @"\DataCollection", "DoNotShowFeedbackNotifications", 1),
                Dw(HKLM, Policies + @"\DataCollection", "AllowDeviceNameInTelemetry", 0),
                Dw(HKLM, Policies + @"\AppCompat", "AITEnable", 0),
                Dw(HKLM, Policies + @"\AppCompat", "DisableInventory", 1),
                Dw(HKCU, @"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0),
                Svc("DiagTrack", ServiceStart.Disabled),
                Svc("dmwappushservice", ServiceStart.Disabled),
                TaskOff(@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser"),
                TaskOff(@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser Exp"),
                TaskOff(@"\Microsoft\Windows\Application Experience\ProgramDataUpdater"),
                TaskOff(@"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator"),
                TaskOff(@"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip"),
                TaskOff(@"\Microsoft\Windows\Autochk\Proxy"),
                TaskOff(@"\Microsoft\Windows\Feedback\Siuf\DmClient"),
                TaskOff(@"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload"),
                TaskOff(@"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector"),
            ],
        },
        new()
        {
            Id = "privacy.error-reporting",
            Title = "Relatório de erros (WER)",
            Area = Area.Privacy,
            Description = "Para de empacotar e enviar relatórios de travamento para a Microsoft.",
            Actions =
            [
                Dw(HKLM, @"SOFTWARE\Microsoft\Windows\Windows Error Reporting", "Disabled", 1),
                TaskOff(@"\Microsoft\Windows\Windows Error Reporting\QueueReporting"),
            ],
        },
        new()
        {
            Id = "privacy.copilot",
            Title = "Copilot",
            Area = Area.Privacy,
            Description = "Desliga o Copilot integrado (versões até 23H2) e esconde o botão. O app Copilot da Store sai no debloat (M2).",
            Restart = Restart.Logoff,
            Actions =
            [
                Dw(HKLM, Policies + @"\WindowsCopilot", "TurnOffWindowsCopilot", 1),
                Dw(HKCU, UserPolicies + @"\WindowsCopilot", "TurnOffWindowsCopilot", 1),
                Dw(HKCU, ExplorerAdvanced, "ShowCopilotButton", 0),
            ],
        },
        new()
        {
            Id = "privacy.recall",
            Title = "Recall e Click to Do",
            Area = Area.Privacy,
            Description = "Impede o Recall de capturar a tela e desliga a análise de IA do Click to Do.",
            AppliesTo = f => f.Build >= 26100,
            Risk = Risk.Medium,
            Note = "em PCs Copilot+ o componente Recall é removido; desfazer não o reinstala sozinho",
            Restart = Restart.Reboot,
            Actions =
            [
                Dw(HKLM, Policies + @"\WindowsAI", "AllowRecallEnablement", 0),
                Dw(HKLM, Policies + @"\WindowsAI", "DisableAIDataAnalysis", 1),
                Dw(HKLM, Policies + @"\WindowsAI", "DisableClickToDo", 1),
                Dw(HKCU, UserPolicies + @"\WindowsAI", "DisableAIDataAnalysis", 1),
                Dw(HKCU, UserPolicies + @"\WindowsAI", "DisableClickToDo", 1),
            ],
        },
        new()
        {
            Id = "privacy.ads",
            Title = "Anúncios e sugestões",
            Area = Area.Privacy,
            Description = "Corta apps instalados em silêncio, dicas, recomendações no Iniciar e ID de publicidade.",
            Restart = Restart.Logoff,
            Actions =
            [
                Dw(HKCU, ContentDelivery, "ContentDeliveryAllowed", 0),
                Dw(HKCU, ContentDelivery, "SilentInstalledAppsEnabled", 0),
                Dw(HKCU, ContentDelivery, "PreInstalledAppsEnabled", 0),
                Dw(HKCU, ContentDelivery, "OemPreInstalledAppsEnabled", 0),
                Dw(HKCU, ContentDelivery, "SystemPaneSuggestionsEnabled", 0),
                Dw(HKCU, ContentDelivery, "SoftLandingEnabled", 0),
                Dw(HKCU, ContentDelivery, "SubscribedContent-338388Enabled", 0),
                Dw(HKCU, ContentDelivery, "SubscribedContent-338389Enabled", 0),
                Dw(HKCU, ContentDelivery, "SubscribedContent-353694Enabled", 0),
                Dw(HKCU, ContentDelivery, "SubscribedContent-353696Enabled", 0),
                Dw(HKCU, ExplorerAdvanced, "Start_IrisRecommendations", 0),
                Dw(HKCU, CurrentVersion + @"\AdvertisingInfo", "Enabled", 0),
                Dw(HKCU, UserPolicies + @"\CloudContent", "DisableTailoredExperiencesWithDiagnosticData", 1),
            ],
        },
        new()
        {
            Id = "privacy.activity-history",
            Title = "Histórico de atividades",
            Area = Area.Privacy,
            Description = "Para de registrar e enviar a linha do tempo de apps e arquivos abertos.",
            Actions =
            [
                Dw(HKLM, Policies + @"\System", "EnableActivityFeed", 0),
                Dw(HKLM, Policies + @"\System", "PublishUserActivities", 0),
                Dw(HKLM, Policies + @"\System", "UploadUserActivities", 0),
            ],
        },
        new()
        {
            Id = "privacy.start-web-search",
            Title = "Busca web no Iniciar (Bing)",
            Area = Area.Privacy,
            Description = "A busca do Iniciar procura só no PC, sem ir à internet a cada tecla.",
            Restart = Restart.Logoff,
            Actions =
            [
                Dw(HKCU, UserPolicies + @"\Explorer", "DisableSearchBoxSuggestions", 1),
            ],
        },

        // ── Processos em segundo plano ────────────────────────────────────────────────
        new()
        {
            Id = "background.widgets",
            Title = "Widgets e Notícias",
            Area = Area.Background,
            Description = "Desliga o painel de Widgets e seus processos (Widgets.exe, WidgetService).",
            Restart = Restart.Logoff,
            Actions = [Dw(HKLM, @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0)],
        },
        new()
        {
            Id = "background.store-apps",
            Title = "Apps da Store em 2º plano",
            Area = Area.Background,
            Description = "Apps UWP/Store não rodam em segundo plano quando fechados.",
            Risk = Risk.Medium,
            Note = "apps da Store (ex.: WhatsApp da Store) só notificam com a janela aberta",
            Actions = [Dw(HKLM, Policies + @"\AppPrivacy", "LetAppsRunInBackground", 2)],
        },
        new()
        {
            Id = "background.edge",
            Title = "Edge em 2º plano",
            Area = Area.Background,
            Description = "Desliga o Startup Boost e a opção de continuar rodando após fechar o Edge.",
            Note = "o Edge passa a mostrar \"gerenciado pela organização\"",
            Restart = Restart.AppRestart,
            Actions =
            [
                Dw(HKLM, @"SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled", 0),
                Dw(HKLM, @"SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled", 0),
            ],
        },
        new()
        {
            Id = "background.chrome",
            Title = "Chrome em 2º plano",
            Area = Area.Background,
            Description = "O Chrome deixa de continuar rodando depois de fechado.",
            Note = "o Chrome passa a mostrar \"gerenciado pela organização\"",
            AppliesTo = f => f.ChromeInstalled,
            Restart = Restart.AppRestart,
            Actions = [Dw(HKLM, @"SOFTWARE\Policies\Google\Chrome", "BackgroundModeEnabled", 0)],
        },
        new()
        {
            Id = "background.delivery-optimization",
            Title = "Delivery Optimization P2P",
            Area = Area.Background,
            Description = "O Windows Update baixa só da Microsoft e para de enviar atualizações para outros PCs.",
            Actions = [Dw(HKLM, Policies + @"\DeliveryOptimization", "DODownloadMode", 0)],
        },

        // ── Serviços (lista curta, nada em massa) ─────────────────────────────────────
        DisableService("services.search-indexer", "WSearch", "Indexador de busca (WSearch)",
            "Para a indexação contínua de arquivos em disco.", Risk.Medium, Profiles.Ultra,
            note: "a busca do Explorer fica mais lenta; busca do Outlook clássico para"),
        new()
        {
            Id = "services.print-spooler",
            Title = "Spooler de impressão",
            Area = Area.Services,
            Description = "Sem impressora física detectada: o spooler não inicia com o Windows.",
            Risk = Risk.Medium,
            Profiles = Profiles.Ultra,
            Restart = Restart.Reboot,
            AppliesTo = f => !f.HasPhysicalPrinter,
            Note = "\"Imprimir em PDF\" para de funcionar até desfazer",
            Actions = [Svc("Spooler", ServiceStart.Manual)],
        },
        DisableService("services.fax", "Fax", "Fax", "Serviço de fax.", note: null),
        DisableService("services.retail-demo", "RetailDemo", "Modo demonstração de loja", "Serviço de vitrine de loja."),
        DisableService("services.maps", "MapsBroker", "Mapas offline", "Gerenciador de download de mapas offline.",
            note: "o app Mapas não atualiza mapas offline"),
        DisableService("services.geolocation", "lfsvc", "Localização (lfsvc)", "Serviço de geolocalização.",
            note: "apps não conseguem saber sua localização (clima, mapas)"),
        DisableService("services.wallet", "WalletService", "Carteira (Wallet)", "Serviço da carteira do Windows."),
        DisableService("services.phone", "PhoneSvc", "Telefonia (PhoneSvc)", "Estado de telefonia do dispositivo.",
            note: "chamadas pelo Vincular ao Celular podem parar"),
        DisableService("services.insider", "wisvc", "Programa Insider (wisvc)", "Serviço do Windows Insider.",
            note: "a página do Insider nas Configurações para"),
        DisableService("services.alljoyn", "AJRouter", "AllJoyn Router", "Protocolo IoT legado."),
        DisableService("services.parental", "WpcMonSvc", "Controle dos pais", "Monitor do Family Safety.",
            note: "restrições do Family Safety param de funcionar"),
        DisableService("services.compat-assistant", "PcaSvc", "Assistente de compatibilidade", "Monitora programas antigos e mostra avisos de compatibilidade."),
        DisableService("services.link-tracking", "TrkWks", "Rastreamento de links (TrkWks)",
            "Mantém atalhos NTFS entre PCs de rede; inútil em PC doméstico.", target: ServiceStart.Manual),

        // ── Gaming ────────────────────────────────────────────────────────────────────
        new()
        {
            Id = "gaming.game-mode",
            Title = "Modo de Jogo ligado",
            Area = Area.Gaming,
            Description = "Garante o Game Mode ativo: o Windows prioriza o jogo e segura o Windows Update durante a partida.",
            Actions =
            [
                Dw(HKCU, @"Software\Microsoft\GameBar", "AllowAutoGameMode", 1),
                Dw(HKCU, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1),
            ],
        },
        new()
        {
            Id = "gaming.game-dvr",
            Title = "Game DVR / gravação de fundo",
            Area = Area.Gaming,
            Description = "Para a captura contínua em segundo plano. O app Xbox e o Game Pass continuam funcionando.",
            Restart = Restart.AppRestart,
            Actions =
            [
                Dw(HKCU, @"System\GameConfigStore", "GameDVR_Enabled", 0),
                Dw(HKCU, CurrentVersion + @"\GameDVR", "AppCaptureEnabled", 0),
                Dw(HKCU, CurrentVersion + @"\GameDVR", "HistoricalCaptureEnabled", 0),
            ],
        },
        new()
        {
            Id = "gaming.gamebar-controller",
            Title = "Game Bar no botão Xbox",
            Area = Area.Gaming,
            Description = "O botão Xbox do controle não abre mais a Game Bar por cima do jogo.",
            Profiles = Profiles.Ultra,
            Actions = [Dw(HKCU, @"Software\Microsoft\GameBar", "UseNexusForGameBarEnabled", 0)],
        },
        new()
        {
            Id = "gaming.hags",
            Title = "Agendamento de GPU por hardware",
            Area = Area.Gaming,
            Description = "HAGS: a GPU gerencia a própria fila de trabalho. Exigido pelo DLSS Frame Generation.",
            Restart = Restart.Reboot,
            Note = "ignorado pelo Windows se a GPU/driver não suportar",
            Actions = [Dw(HKLM, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2)],
        },
        new()
        {
            Id = "gaming.windowed-optimizations",
            Title = "Otimizações para jogos em janela",
            Area = Area.Gaming,
            Description = "Jogos DX10/11 em janela/borderless usam o modelo flip: menos latência, VRR e Auto HDR.",
            Restart = Restart.AppRestart,
            Actions = [new SetStringFlags(Target.Reg(HKCU, DirectXPrefs, "DirectXUserGlobalSettings"), [new("SwapEffectUpgradeEnable", "1")])],
        },
        new()
        {
            Id = "gaming.vrr",
            Title = "Taxa de atualização variável",
            Area = Area.Gaming,
            Description = "Liga o VRR do Windows para jogos que não suportam G-Sync/FreeSync nativamente.",
            Restart = Restart.AppRestart,
            Note = "sem efeito em monitor sem VRR",
            Actions = [new SetStringFlags(Target.Reg(HKCU, DirectXPrefs, "DirectXUserGlobalSettings"), [new("VRROptimizeEnable", "1")])],
        },
        new()
        {
            Id = "gaming.mouse-acceleration",
            Title = "Aceleração do mouse desligada",
            Area = Area.Gaming,
            Description = "Movimento 1:1: a mesma distância física sempre gira a mira o mesmo tanto.",
            Restart = Restart.Logoff,
            Actions =
            [
                Sz(HKCU, @"Control Panel\Mouse", "MouseSpeed", "0"),
                Sz(HKCU, @"Control Panel\Mouse", "MouseThreshold1", "0"),
                Sz(HKCU, @"Control Panel\Mouse", "MouseThreshold2", "0"),
            ],
        },

        new()
        {
            Id = "gaming.mpo-off",
            Title = "MPO desligado (teste A/B)",
            Area = Area.Gaming,
            Description = "Desliga o Multi-Plane Overlay do DWM. Corrige flicker e stutter em alguns setups NVIDIA com mais de um monitor, "
                          + "mas piora outros. Não entra em perfil: meça com a tela > fps antes e depois e só mantenha se melhorar.",
            Risk = Risk.Medium,
            Profiles = Profiles.None,
            Restart = Restart.Reboot,
            Note = "o Windows mudou a chave entre versões; o Leve grava as duas (OverlayTestMode e DisableOverlays)",
            Actions =
            [
                Dw(HKLM, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", 5),
                Dw(HKLM, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "DisableOverlays", 1),
            ],
        },

        // ── Visual (só Ultra) ─────────────────────────────────────────────────────────
        new()
        {
            Id = "visual.animations",
            Title = "Animações de janela",
            Area = Area.Visual,
            Description = "Janelas e barra de tarefas abrem e minimizam sem animação.",
            Profiles = Profiles.Ultra,
            Restart = Restart.Logoff,
            Actions =
            [
                Sz(HKCU, @"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0"),
                Dw(HKCU, ExplorerAdvanced, "TaskbarAnimations", 0),
            ],
        },
        new()
        {
            Id = "visual.transparency",
            Title = "Transparência",
            Area = Area.Visual,
            Description = "Desliga o efeito acrílico/mica do Iniciar e da barra de tarefas.",
            Profiles = Profiles.Ultra,
            Actions = [Dw(HKCU, CurrentVersion + @"\Themes\Personalize", "EnableTransparency", 0)],
        },
        new()
        {
            Id = "visual.menu-delay",
            Title = "Atraso de menus",
            Area = Area.Visual,
            Description = "Submenus abrem na hora em vez de esperar 400 ms.",
            Profiles = Profiles.Ultra,
            Restart = Restart.Logoff,
            Actions = [Sz(HKCU, @"Control Panel\Desktop", "MenuShowDelay", "0")],
        },

        // ── Apps (M2) e energia (M2) ───────────────────────────────────────────────────
        .. AppRemovals(),
        .. PowerTweaks(),

        // ── Segurança × desempenho (M3, opt-in) ────────────────────────────────────────
        new()
        {
            Id = "security.hvci",
            Title = "Integridade de memória (HVCI) desligada",
            Area = Area.Security,
            Description = "Desliga a integridade de memória do Isolamento de Núcleo. Costuma render 5 a 10% de FPS, porque o hypervisor deixa de checar cada driver carregado no kernel.",
            Risk = Risk.High,
            Profiles = Profiles.None,
            OptIn = true,
            Restart = Restart.Reboot,
            AppliesTo = f => f.HvciConfigured,
            Note = "reduz a proteção contra drivers maliciosos; se algum jogo ou anti-cheat exigir a integridade de memória, desfaça. Não tem efeito se uma política da empresa ou o UEFI travar o HVCI",
            Actions = [Dw(HKLM, @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", 0)],
        },
    ];

    // Removidos só da conta atual. NUNCA entram aqui: Store, App Installer, Xbox/GamingApp, Gaming Services,
    // Terminal, Calculadora, Fotos, Media Player, codecs.
    // Propriedade, não campo: All = Build() roda antes de qualquer campo estático declarado abaixo.
    private static (string Package, string Title, Profiles Profiles, Risk Risk, string? Note)[] Apps =>
    [
        ("Microsoft.BingNews", "Notícias (Bing)", Profiles.All, Risk.Low, null),
        ("Microsoft.BingWeather", "Clima (Bing)", Profiles.All, Risk.Low, null),
        ("Microsoft.BingSearch", "Bing Search", Profiles.All, Risk.Low, null),
        ("Microsoft.GetHelp", "Obter Ajuda", Profiles.All, Risk.Low, null),
        ("Microsoft.Getstarted", "Dicas", Profiles.All, Risk.Low, null),
        ("Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection", Profiles.All, Risk.Low, null),
        ("Microsoft.People", "Pessoas", Profiles.All, Risk.Low, null),
        ("Microsoft.WindowsMaps", "Mapas", Profiles.All, Risk.Low, null),
        ("Microsoft.WindowsFeedbackHub", "Hub de Comentários", Profiles.All, Risk.Low, null),
        ("Microsoft.PowerAutomateDesktop", "Power Automate", Profiles.All, Risk.Low, null),
        ("Clipchamp.Clipchamp", "Clipchamp", Profiles.All, Risk.Low, null),
        ("Microsoft.MicrosoftOfficeHub", "Microsoft 365 (atalho do Office)", Profiles.All, Risk.Low, "o Office instalado continua funcionando"),
        ("Microsoft.549981C3F5F10", "Cortana", Profiles.All, Risk.Low, null),
        ("Microsoft.MixedReality.Portal", "Portal de Realidade Mista", Profiles.All, Risk.Low, null),
        ("Microsoft.Microsoft3DViewer", "Visualizador 3D", Profiles.All, Risk.Low, null),
        ("Microsoft.SkypeApp", "Skype (Store)", Profiles.All, Risk.Low, null),
        ("Microsoft.ZuneVideo", "Filmes e TV", Profiles.All, Risk.Low, null),
        ("Microsoft.Copilot", "Copilot (app)", Profiles.All, Risk.Medium, StoreOnlyUndo),
        ("Microsoft.Windows.DevHome", "Dev Home (descontinuado)", Profiles.All, Risk.Medium, StoreOnlyUndo),
        ("Microsoft.OutlookForWindows", "Outlook (novo)", Profiles.All, Risk.Medium, "o Outlook clássico não é afetado; " + StoreOnlyUndo),
        ("MSTeams", "Teams (novo)", Profiles.All, Risk.Medium, "reuniões pelo app param; " + StoreOnlyUndo),
        ("Microsoft.Todos", "Microsoft To Do", Profiles.Ultra, Risk.Low, null),
        ("MicrosoftCorporationII.QuickAssist", "Assistência Rápida", Profiles.Ultra, Risk.Low, null),
        ("Microsoft.YourPhone", "Vincular ao Celular", Profiles.Ultra, Risk.Medium, "notificações e chamadas do celular no PC param"),
    ];

    private const string StoreOnlyUndo = "costuma não ficar guardado no PC: desfazer pode exigir reinstalar pela Store";

    private static IEnumerable<Tweak> AppRemovals() => Apps.Select(app => new Tweak
    {
        Id = "apps." + app.Package.ToLowerInvariant(),
        Title = app.Title,
        Area = Area.Apps,
        Description = $"Remove o app da sua conta ({app.Package}). Desfazer re-registra o pacote sem internet enquanto ele ainda estiver no PC. Atualizações grandes do Windows podem trazê-lo de volta.",
        Risk = app.Risk,
        Profiles = app.Profiles,
        Note = app.Note,
        Actions = [new SetValue(Target.Package(app.Package), StateValue.PackageRemoved)],
    });

    private static IEnumerable<Tweak> PowerTweaks() =>
    [
        new()
        {
            Id = "power.leve-plan",
            Title = "Plano de energia Leve Ultimate",
            Area = Area.Power,
            Description = "Clona o Ultimate Performance e, na tomada: CPU mínima 100%, sem core parking, USB e PCIe sem economia, disco nunca desliga. Os C-states continuam ligados.",
            Note = "na bateria o plano volta a economizar (CPU mínima 5%, USB/PCIe com economia)",
            Actions = [new SetValue(Target.Power(LevePlan.ActiveSchemeSetting), StateValue.Text(LevePlan.Id))],
        },
        new()
        {
            Id = "power.hibernate",
            Title = "Hibernação e Inicialização Rápida",
            Area = Area.Power,
            Description = "Desliga a hibernação (libera o hiberfil.sys, vários GB) e a Inicialização Rápida, que mantém drivers \"meio carregados\" entre boots.",
            AppliesTo = f => !f.IsLaptop,
            Restart = Restart.Reboot,
            Note = "o boot fica alguns segundos mais lento, mas sempre limpo",
            Actions =
            [
                new SetValue(Target.Power(LevePlan.HibernateSetting), StateValue.Flag(false)),
                Dw(HKLM, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0),
            ],
        },
    ];
}

/// <summary>Identidade fixa do plano de energia do Leve, para reconhecê-lo e reativá-lo entre execuções.</summary>
public static class LevePlan
{
    public const string Id = "6f1c9a52-3b7e-4d2a-8e44-1c5e7a9b0d21";
    public const string Name = "Leve Ultimate";
    public const string ActiveSchemeSetting = "active-scheme";
    public const string HibernateSetting = "hibernate";
    public const string UltimatePerformance = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    public const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
}
