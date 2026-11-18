using Leve.Core;

namespace Leve.Core.Tests;

public class OptInTests
{
    [Fact]
    public void Opt_in_tweaks_are_never_part_of_a_profile()
    {
        var optIns = Catalog.All.Where(t => t.OptIn).ToList();
        Assert.NotEmpty(optIns);
        Assert.All(optIns, t => Assert.Equal(Profiles.None, t.Profiles));
    }

    [Fact]
    public void Hvci_tweak_only_shows_when_the_pc_has_hvci()
    {
        var hvci = Catalog.Find("security.hvci")!;
        Assert.True(hvci.OptIn);
        Assert.Equal(Risk.High, hvci.Risk);
        Assert.False(hvci.IsApplicable(new SystemFacts { Build = 26100, HvciConfigured = false }));
        Assert.True(hvci.IsApplicable(new SystemFacts { Build = 26100, HvciConfigured = true }));
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common", true)]
    [InlineData(@"D:\SteamLibrary\steamapps\common", true)]
    [InlineData(@"C:\XboxGames", true)]
    [InlineData(@"C:\Program Files\Epic Games", true)]
    [InlineData(@"C:\", false)]
    [InlineData(@"D:", false)]
    [InlineData(@"C:\Windows", false)]
    [InlineData(@"C:\Windows\System32", false)]
    [InlineData(@"C:\Program Files", false)]
    [InlineData(@"C:\Users", false)]
    [InlineData(@"C:\Users\luizh", false)]
    [InlineData(@"C:\Users\luizh\Games", false)]
    [InlineData(@"C:\Users\luizh\Documents", false)]
    [InlineData(@"C:\Jogos\O'Brien", false)]
    [InlineData("C:\\Jogos\\a\u2019;calc #\\steamapps\\common", false)]
    [InlineData(@"C:\PROGRA~1\Epic Games", false)]
    [InlineData(@"C:\Windows.\XboxGames", false)]
    [InlineData(@"C:\x\..\Windows\XboxGames", false)]
    [InlineData(@"C:\*", false)]
    [InlineData(@"C:\Program Files\Ubisoft\Ubisoft Game Launcher\games", true)]
    [InlineData(@"C:\Program Files\WindowsApps", false)]
    [InlineData(@"C:\XboxGames:stream", false)]
    [InlineData(@"relativo\pasta", false)]
    public void Exclusions_are_only_accepted_for_game_folders(string folder, bool expected)
    {
        Assert.Equal(expected, DynamicCatalog.IsSafeExclusion(folder));
    }

    [Fact]
    public void Game_libraries_become_one_opt_in_tweak_without_dangerous_folders()
    {
        var tweak = DynamicCatalog.FromGameLibraries(
        [
            @"D:\SteamLibrary\steamapps\common\",
            @"d:\steamlibrary\steamapps\common",
            @"C:\XboxGames",
            @"C:\Windows",
        ])!;

        Assert.True(tweak.OptIn);
        Assert.Equal(Profiles.None, tweak.Profiles);
        Assert.Equal(2, tweak.Actions.Count);
        Assert.All(tweak.Actions, a => Assert.Equal(TargetType.DefenderExclusion, a.Target.Type));
    }

    [Fact]
    public void No_libraries_means_no_tweak()
    {
        Assert.Null(DynamicCatalog.FromGameLibraries([@"C:\", @"C:\Windows"]));
    }

    [Fact]
    public void Exclusion_is_removed_again_on_revert()
    {
        var system = new FakeSystem();
        var folder = @"D:\SteamLibrary\steamapps\common";
        system.Seed(Target.DefenderExclusion(folder), StateValue.Flag(false));
        var engine = new TweakEngine(system, new MemoryStore());
        var tweak = DynamicCatalog.FromGameLibraries([folder])!;

        var snapshot = engine.Apply(engine.BuildPlan([tweak]), "teste")!;
        Assert.Equal(StateValue.Flag(true), system.Read(Target.DefenderExclusion(folder)));

        engine.Revert(snapshot);
        Assert.Equal(StateValue.Flag(false), system.Read(Target.DefenderExclusion(folder)));
    }
}

public class ReportTests
{
    private static MetricsReport Sample(int processes, long ram, long commit, int services, int startup) =>
        new(DateTimeOffset.Now, processes, ram, commit, services, startup);

    [Fact]
    public void Fewer_is_better_in_every_metric()
    {
        var rows = MetricsComparison.Compare(Sample(142, 5900, 9000, 120, 14), Sample(98, 4300, 7000, 101, 3));

        Assert.Equal(5, rows.Count);
        Assert.All(rows, r => Assert.Equal(Trend.Better, r.Trend));
        Assert.Equal("−44", rows[0].Delta);
        Assert.Equal("−1.6 GB", rows[1].Delta);
    }

    [Fact]
    public void Growth_is_flagged_as_worse_and_equal_as_same()
    {
        var rows = MetricsComparison.Compare(Sample(100, 4000, 7000, 100, 3), Sample(120, 4000, 7000, 100, 3));

        Assert.Equal(Trend.Worse, rows[0].Trend);
        Assert.Equal("+20", rows[0].Delta);
        Assert.Equal(Trend.Same, rows[1].Trend);
        Assert.Equal("±0 MB", rows[1].Delta);
    }

    [Fact]
    public void Baseline_round_trips_through_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"leve-baseline-{Guid.NewGuid():N}.json");
        try
        {
            var store = new BaselineStore(path);
            Assert.Null(store.Load());

            var report = Sample(142, 5900, 9000, 120, 14);
            store.Save(report);

            Assert.Equal(report, store.Load());
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class ReportUnavailableMetricTests
{
    [Fact]
    public void Unavailable_metric_shows_question_mark_instead_of_a_fake_delta()
    {
        var before = new MetricsReport(DateTimeOffset.Now, 100, 4000, 7000, -1, 3);
        var after = new MetricsReport(DateTimeOffset.Now, 90, 3900, 6900, 95, 2);

        var services = MetricsComparison.Compare(before, after)[3];

        Assert.Equal("?", services.Before);
        Assert.Equal("?", services.Delta);
        Assert.Equal(Trend.Same, services.Trend);
    }
}
