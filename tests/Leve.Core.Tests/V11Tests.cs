using Leve.Core;

namespace Leve.Core.Tests;

public class GameRulesTests
{
    private static readonly AgentConfig Config = new()
    {
        GameFolders = [@"C:\Program Files (x86)\Steam\steamapps\common", @"D:\XboxGames"],
        ExtraGames = ["minecraft.exe"],
        IgnoredGames = ["Benchmark.exe"],
    };

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe", true)]
    [InlineData(@"D:\XboxGames\Forza Horizon 5\Content\ForzaHorizon5.exe", true)]
    [InlineData(@"C:\Users\me\AppData\Roaming\.minecraft\minecraft.exe", true)]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe", false)]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Game\unins000.exe", false)]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Game\UnityCrashHandler64.exe", false)]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Game\Benchmark.exe", false)]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\commonX\fake.exe", false)]
    [InlineData(@"C:\Windows\explorer.exe", false)]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", false)]
    [InlineData("", false)]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Cyberpunk 2077\REDprelauncher.exe", false)]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\SteamVR\bin\win64\vrmonitor.exe", false)]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\wallpaper_engine\wallpaper64.exe", false)]
    public void Detects_games_only_inside_libraries_or_user_lists(string path, bool expected)
    {
        Assert.Equal(expected, GameRules.IsGame(path, Config));
    }

    [Theory]
    [InlineData("Discord", DemoteTier.None)]
    [InlineData("Spotify", DemoteTier.None)]
    [InlineData("chrome", DemoteTier.Heavy)]
    [InlineData("msedge", DemoteTier.Heavy)]
    [InlineData("GoogleDriveFS", DemoteTier.Heavy)]
    [InlineData("steamwebhelper", DemoteTier.Heavy)]
    [InlineData("audiodg", DemoteTier.None)]
    [InlineData("vgc", DemoteTier.None)]
    [InlineData("explorer", DemoteTier.None)]
    [InlineData("cs2", DemoteTier.None)]
    public void Demotes_only_known_background_apps(string processName, DemoteTier expected)
    {
        Assert.Equal(expected, DemoteRules.TierFor(processName));
    }
}

public class AgentTweakTests
{
    [Fact]
    public void Agent_is_a_reversible_run_entry_in_both_profiles()
    {
        var tweak = AgentCatalog.Create(@"C:\dev\leve-next\publish\Leve.Agent.exe");

        Assert.Equal(AgentCatalog.TweakId, tweak.Id);
        Assert.Equal(Profiles.All, tweak.Profiles);
        Assert.False(tweak.OptIn);
        var action = Assert.IsType<SetValue>(Assert.Single(tweak.Actions));
        Assert.Equal(Target.Reg(Hive.HKCU, AgentCatalog.RunKey, AgentCatalog.RunValueName), action.Target);
        Assert.Equal(StateValue.Text("\"C:\\dev\\leve-next\\publish\\Leve.Agent.exe\""), action.Value);
    }

    [Fact]
    public void Enabling_then_undoing_the_agent_removes_the_run_entry()
    {
        var system = new FakeSystem();
        var engine = new TweakEngine(system, new MemoryStore());
        var tweak = AgentCatalog.Create(@"C:\Leve\Leve.Agent.exe");

        var snapshot = engine.Apply(engine.BuildPlan([tweak]), "teste")!;
        Assert.Equal(TweakStatus.On, engine.Evaluate(tweak));

        engine.Revert(snapshot);
        Assert.Equal(StateValue.Absent, system.Read(tweak.Actions[0].Target));
        Assert.Equal(TweakStatus.Off, engine.Evaluate(tweak));
    }

    [Fact]
    public void The_agent_own_startup_entry_is_protected_from_the_startup_cleaner()
    {
        var entry = new StartupEntry(AgentCatalog.RunValueName, "\"C:\\Leve\\Leve.Agent.exe\"", StartupSource.UserRun);
        Assert.Equal(StartupClass.Protected, DynamicCatalog.Classify(entry));
    }

    [Fact]
    public void Config_and_status_round_trip_through_disk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "leve-agent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var configPath = Path.Combine(dir, "agent.json");
            new AgentConfig { GameFolders = [@"D:\XboxGames"], ExtraGames = ["a.exe"] }.Save(configPath);
            var config = AgentConfig.Load(configPath)!;
            Assert.Equal(@"D:\XboxGames", Assert.Single(config.GameFolders));
            Assert.Equal("a.exe", Assert.Single(config.ExtraGames));

            var statusPath = Path.Combine(dir, "agent-status.json");
            var status = new AgentStatus(true, "cs2.exe", 7, DateTimeOffset.Now);
            status.Save(statusPath);
            Assert.Equal(status, AgentStatus.Load(statusPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class AgentConfigRobustnessTests
{
    [Fact]
    public void Null_lists_in_a_hand_edited_config_become_empty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"leve-agent-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "GameFolders": null, "ExtraGames": [ "a.exe", "" ] }""");
            var config = AgentConfig.Load(path)!;

            Assert.Empty(config.GameFolders);
            Assert.Equal("a.exe", Assert.Single(config.ExtraGames));
            Assert.Empty(config.IgnoredGames);
            Assert.False(GameRules.IsGame(@"C:\x\game.exe", config));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
