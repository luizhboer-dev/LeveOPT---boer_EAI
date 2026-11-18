using Leve.Core;

namespace Leve.Core.Tests;

public class HardwareCatalogTests
{
    [Fact]
    public void Gpu_per_game_targets_each_exe_and_only_shows_on_hybrid_gpus()
    {
        var tweak = HardwareCatalog.GpuPerGame(
        [
            @"D:\SteamLibrary\steamapps\common\Game\game.exe",
            @"d:\steamlibrary\steamapps\common\game\GAME.exe",
            @"D:\SteamLibrary\steamapps\common\Game\readme.txt",
            @"D:\x\bad;name.exe",
        ])!;

        Assert.Single(tweak.Actions);
        Assert.False(tweak.IsApplicable(new SystemFacts { HasHybridGpu = false }));
        Assert.True(tweak.IsApplicable(new SystemFacts { HasHybridGpu = true }));
        var action = Assert.IsType<SetStringFlags>(tweak.Actions[0]);
        Assert.Equal(StateValue.Text("GpuPreference=2;"), action.Desired(StateValue.Absent));
        Assert.Null(HardwareCatalog.GpuPerGame([]));
    }

    [Fact]
    public void Network_tweak_only_touches_keywords_the_driver_has()
    {
        var adapter = new NetworkAdapterInfo("0007", "Realtek PCIe 2.5GbE",
            new Dictionary<string, string> { ["*EEE"] = "1", ["EnableGreenEthernet"] = "1", ["RandomKeyword"] = "1" });

        var tweak = Assert.Single(HardwareCatalog.NetworkPowerSaving([adapter]));
        var names = tweak.Actions.Select(a => a.Target.Name).ToList();

        Assert.Equal(new[] { "*EEE", "EnableGreenEthernet", "PnPCapabilities" }, names);
        Assert.All(tweak.Actions, a => Assert.StartsWith(HardwareCatalog.NetworkClass + @"\0007", a.Target.Path));
        Assert.Equal(Profiles.Ultra, tweak.Profiles);
    }

    [Fact]
    public void Network_tweak_rejects_weird_class_subkeys()
    {
        var adapter = new NetworkAdapterInfo(@"..\x", "x", new Dictionary<string, string>());
        Assert.Empty(HardwareCatalog.NetworkPowerSaving([adapter]));
    }

    [Fact]
    public void Mpo_is_manual_only()
    {
        var mpo = Catalog.Find("gaming.mpo-off")!;
        Assert.Equal(Profiles.None, mpo.Profiles);
        Assert.False(mpo.OptIn);
    }
}

public class FpsTests
{
    private static List<double> Frames(double ms, int count) => Enumerable.Repeat(ms, count).ToList();

    [Fact]
    public void Computes_average_and_one_percent_low()
    {
        var frames = Frames(10, 980).Concat(Frames(20, 20)).ToList(); // 2% de travadas a 20 ms

        var stats = FrameStats.Compute(frames)!;

        Assert.Equal(1000, stats.Frames);
        Assert.InRange(stats.AvgFps, 98.0, 98.1);       // média de 10,2 ms
        Assert.Equal(20, stats.P99FrameTimeMs);
        Assert.Equal(50, stats.OnePercentLowFps, 3);
        Assert.Equal(6, stats.SegmentFps.Count);
    }

    [Fact]
    public void Too_few_frames_gives_no_result_and_loading_hitches_are_ignored()
    {
        Assert.Null(FrameStats.Compute(Frames(10, 20)));
        var withLoading = Frames(10, 600).Append(5000).ToList();
        Assert.Equal(600, FrameStats.Compute(withLoading)!.Frames);
    }

    [Fact]
    public void Clear_gain_is_better_and_small_noise_is_inconclusive()
    {
        var a = FrameStats.Compute(Frames(10, 1200))!;   // 100 fps
        var b = FrameStats.Compute(Frames(8, 1200))!;    // 125 fps
        var c = FrameStats.Compute(Frames(9.9, 1200))!;  // ~101 fps

        Assert.Equal(FpsVerdict.Better, FpsComparison.Compare(a, b).Verdict);
        Assert.Equal(FpsVerdict.Worse, FpsComparison.Compare(b, a).Verdict);
        Assert.Equal(FpsVerdict.Inconclusive, FpsComparison.Compare(a, c).Verdict);
    }

    [Fact]
    public void Parses_presentmon_v1_and_v2_csv_with_temperatures()
    {
        string[] v2 =
        [
            "Application,ProcessID,FrameTime,GPUTemperature,CPUTemperature",
            "cs2.exe,123,6.94,70.5,65",
            "cs2.exe,123,7.10,71.0,66",
        ];
        var (frames, gpu, cpu) = PresentMonCsv.Parse(v2);
        Assert.Equal(new[] { 6.94, 7.10 }, frames);
        Assert.Equal(71.0, gpu.Max());
        Assert.Equal(66.0, cpu.Max());

        string[] v1 = ["Application,ProcessID,MsBetweenPresents", "game.exe,1,16.6"];
        var parsed = PresentMonCsv.Parse(v1);
        Assert.Equal(new[] { 16.6 }, parsed.FrameTimes);
        Assert.Empty(parsed.GpuTemps);
    }

    [Fact]
    public void Captures_are_kept_newest_first_and_capped()
    {
        var path = Path.Combine(Path.GetTempPath(), $"leve-fps-{Guid.NewGuid():N}.json");
        try
        {
            var store = new CaptureStore(path);
            var stats = FrameStats.Compute(Frames(10, 600))!;
            for (var i = 0; i < 55; i++)
                store.Add(new CaptureRecord(i % 2 == 0 ? CaptureLabel.A : CaptureLabel.B, "cs2.exe", DateTimeOffset.Now.AddMinutes(i), 60, stats));

            var all = store.Load();
            Assert.Equal(50, all.Count);
            Assert.True(all[0].At > all[^1].At);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
