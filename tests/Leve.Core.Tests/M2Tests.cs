using Leve.Core;

namespace Leve.Core.Tests;

public class StartupTests
{
    private static StartupEntry Run(string name, string command = "") =>
        new(name, command == string.Empty ? $@"C:\Program Files\{name}\{name}.exe" : command, StartupSource.UserRun);

    [Theory]
    [InlineData("Steam", StartupClass.Launcher)]
    [InlineData("Discord", StartupClass.Launcher)]
    [InlineData("MicrosoftEdgeAutoLaunch_0A1B2C", StartupClass.Launcher)]
    [InlineData("EpicGamesLauncher", StartupClass.Launcher)]
    [InlineData("GoogleDriveFS", StartupClass.CloudSync)]
    [InlineData("OneDrive", StartupClass.CloudSync)]
    [InlineData("Docker Desktop", StartupClass.DevTool)]
    [InlineData("SecurityHealth", StartupClass.Protected)]
    [InlineData("RtkAudUService", StartupClass.Protected)]
    [InlineData("Riot Vanguard", StartupClass.Protected)]
    [InlineData("LGHUB", StartupClass.Protected)]
    [InlineData("MinhaFerramentaQualquer", StartupClass.Unknown)]
    public void Classifies_startup_entries(string name, StartupClass expected)
    {
        Assert.Equal(expected, DynamicCatalog.Classify(Run(name)));
    }

    [Fact]
    public void Razer_cortex_is_a_booster_not_a_protected_driver()
    {
        Assert.Equal(StartupClass.Launcher, DynamicCatalog.Classify(Run("Razer Cortex")));
        Assert.Equal(StartupClass.Protected, DynamicCatalog.Classify(Run("Razer Synapse")));
    }

    [Fact]
    public void IntelliJ_is_not_mistaken_for_an_intel_driver()
    {
        Assert.NotEqual(StartupClass.Protected, DynamicCatalog.Classify(Run("IntelliJ IDEA")));
    }

    [Fact]
    public void Protected_entries_never_become_tweaks_and_cloud_sync_is_never_preselected()
    {
        var (tweaks, protectedCount) = DynamicCatalog.FromStartup([Run("SecurityHealth"), Run("Steam"), Run("GoogleDriveFS"), Run("Ferramenta")]);

        Assert.Equal(1, protectedCount);
        Assert.Equal(3, tweaks.Count);
        Assert.Equal(Profiles.All, tweaks.Single(t => t.Title.StartsWith("Steam")).Profiles);
        Assert.Equal(Profiles.None, tweaks.Single(t => t.Title.StartsWith("GoogleDriveFS")).Profiles);
        Assert.Equal(Profiles.None, tweaks.Single(t => t.Title.StartsWith("Ferramenta")).Profiles);
        Assert.Equal(Risk.Medium, tweaks.Single(t => t.Title.StartsWith("Ferramenta")).Risk);
    }

    [Fact]
    public void Startup_target_points_to_startup_approved()
    {
        var hklm32 = new StartupEntry("Steam", "steam.exe", StartupSource.MachineRun32);
        Assert.Equal(Target.Reg(Hive.HKLM, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32", "Steam"), hklm32.ApprovalTarget);
    }

    [Fact]
    public void Disable_startup_keeps_an_entry_already_disabled_by_task_manager()
    {
        var action = new DisableStartupEntry(Target.Reg(Hive.HKCU, "x", "Steam"));
        var disabledByTaskManager = new StateValue(ValueKind.Binary, Convert.ToBase64String([0x03, 0, 0, 0, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x01]));
        var enabled = new StateValue(ValueKind.Binary, Convert.ToBase64String([0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));

        Assert.Equal(disabledByTaskManager, action.Desired(disabledByTaskManager));
        Assert.True(DisableStartupEntry.IsDisabled(action.Desired(enabled)));
        Assert.True(DisableStartupEntry.IsDisabled(action.Desired(StateValue.Absent)));
    }

    [Fact]
    public void Disabling_and_reverting_a_startup_entry_restores_absent()
    {
        var system = new FakeSystem();
        var engine = new TweakEngine(system, new MemoryStore());
        var (tweaks, _) = DynamicCatalog.FromStartup([Run("Steam")]);

        var snapshot = engine.Apply(engine.BuildPlan(tweaks), "teste")!;
        Assert.Equal(TweakStatus.On, engine.Evaluate(tweaks[0]));

        engine.Revert(snapshot);
        Assert.Equal(StateValue.Absent, system.Read(tweaks[0].Actions[0].Target));
    }

    [Fact]
    public void Only_known_updater_tasks_are_offered()
    {
        var tweaks = DynamicCatalog.FromTasks(
        [
            @"\GoogleUpdaterTaskSystem140.0.7339.128{1A2B}",
            @"\MicrosoftEdgeUpdateTaskMachineCore{3C4D}",
            @"\Adobe Acrobat Update Task",
            @"\MinhaTarefaDeBackup",
            @"\NvTmRep_CrashReport1",
        ]);

        Assert.Equal(3, tweaks.Count);
        Assert.All(tweaks, t => Assert.Equal(Area.Startup, t.Area));
    }
}

public class AppAndPowerTests
{
    [Fact]
    public void Removing_an_app_remembers_its_family_and_revert_reinstalls_it()
    {
        var system = new FakeSystem();
        var target = Target.Package("Microsoft.BingNews");
        system.Seed(target, StateValue.PackageInstalled("Microsoft.BingNews_8wekyb3d8bbwe"));
        var engine = new TweakEngine(system, new MemoryStore());
        var tweak = Catalog.Find("apps.microsoft.bingnews")!;

        var snapshot = engine.Apply(engine.BuildPlan([tweak]), "teste")!;
        Assert.Equal(StateValue.PackageRemoved, system.Read(target));
        Assert.Equal("Microsoft.BingNews_8wekyb3d8bbwe", snapshot.Entries[0].Original.PackageFamily);

        engine.Revert(snapshot);
        Assert.Equal(StateValue.PackageInstalled("Microsoft.BingNews_8wekyb3d8bbwe"), system.Read(target));
    }

    [Fact]
    public void App_already_gone_is_already_applied()
    {
        var system = new FakeSystem();
        system.Seed(Target.Package("Microsoft.BingNews"), StateValue.PackageRemoved);
        var engine = new TweakEngine(system, new MemoryStore());

        var plan = engine.BuildPlan([Catalog.Find("apps.microsoft.bingnews")!]);

        Assert.Empty(plan.Changes);
    }

    [Fact]
    public void Gaming_store_and_essential_apps_are_never_in_the_removal_list()
    {
        string[] keep = ["Microsoft.GamingApp", "Microsoft.XboxApp", "Microsoft.XboxIdentityProvider", "Microsoft.XboxGamingOverlay",
            "Microsoft.Xbox.TCUI", "Microsoft.GamingServices", "Microsoft.WindowsStore", "Microsoft.DesktopAppInstaller",
            "Microsoft.WindowsTerminal", "Microsoft.WindowsCalculator", "Microsoft.Windows.Photos", "Microsoft.ZuneMusic",
            "Microsoft.HEVCVideoExtension", "Microsoft.AV1VideoExtension", "Microsoft.VP9VideoExtensions"];
        var removed = Catalog.All.SelectMany(t => t.Actions).Where(a => a.Target.Type == TargetType.Package).Select(a => a.Target.Name).ToList();

        Assert.NotEmpty(removed);
        Assert.DoesNotContain(removed, r => keep.Contains(r, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Power_plan_switches_back_to_the_previous_scheme_on_revert()
    {
        var system = new FakeSystem();
        var active = Target.Power(LevePlan.ActiveSchemeSetting);
        const string balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
        system.Seed(active, StateValue.Text(balanced));
        var engine = new TweakEngine(system, new MemoryStore());

        var snapshot = engine.Apply(engine.BuildPlan([Catalog.Find("power.leve-plan")!]), "teste")!;
        Assert.Equal(StateValue.Text(LevePlan.Id), system.Read(active));

        engine.Revert(snapshot);
        Assert.Equal(StateValue.Text(balanced), system.Read(active));
    }

    [Fact]
    public void Hibernation_is_hidden_on_laptops()
    {
        var tweak = Catalog.Find("power.hibernate")!;
        Assert.False(tweak.IsApplicable(new SystemFacts { Build = 26100, IsLaptop = true }));
        Assert.True(tweak.IsApplicable(new SystemFacts { Build = 26100, IsLaptop = false }));
    }

    [Fact]
    public void Package_state_round_trips_through_json()
    {
        var dir = Path.Combine(Path.GetTempPath(), "leve-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonSnapshotStore(dir);
            store.Save(new Snapshot
            {
                Id = 1,
                Label = "apps",
                CreatedAt = DateTimeOffset.Now,
                Status = SnapshotStatus.Committed,
                Entries =
                [
                    new SnapshotEntry
                    {
                        Target = Target.Package("Clipchamp.Clipchamp"),
                        Original = StateValue.PackageInstalled("Clipchamp.Clipchamp_yxz26nhyzhsrt"),
                        Desired = StateValue.PackageRemoved,
                        TweakIds = ["apps.clipchamp.clipchamp"],
                        State = EntryState.Applied,
                    },
                ],
            });

            var entry = Assert.Single(Assert.Single(store.List()).Entries);
            Assert.Equal("Clipchamp.Clipchamp_yxz26nhyzhsrt", entry.Original.PackageFamily);
            Assert.Null(entry.Desired.PackageFamily);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class StartupReviewTests
{
    [Theory]
    [InlineData(@"""C:\Program Files\App\app.exe"" --minimized", "app.exe")]
    [InlineData(@"C:\Users\ASUS\AppData\Local\Discord\Update.exe --processStart Discord.exe", "Update.exe")]
    [InlineData(@"C:\Tools\tool", "tool")]
    public void Executable_name_ignores_the_folder(string command, string expected)
    {
        Assert.Equal(expected, DynamicCatalog.ExecutableName(command));
    }

    [Fact]
    public void Oem_user_folder_named_asus_does_not_protect_everything()
    {
        var entry = new StartupEntry("Discord", @"C:\Users\ASUS\AppData\Local\Discord\Update.exe --processStart Discord.exe", StartupSource.UserRun);
        Assert.Equal(StartupClass.Launcher, DynamicCatalog.Classify(entry));
    }

    [Theory]
    [InlineData("ZoomIt")]
    [InlineData("AnyDesk")]
    [InlineData("TeamViewer")]
    public void Remote_access_and_zoomit_are_never_preselected(string name)
    {
        var entry = new StartupEntry(name, $@"C:\Tools\{name}.exe", StartupSource.UserRun);
        Assert.Equal(StartupClass.Unknown, DynamicCatalog.Classify(entry));
    }
}
