using Leve.Core;

namespace Leve.Core.Tests;

public class ImpactTests
{
    [Fact]
    public void Every_static_tweak_has_an_impact_and_a_typical_gain()
    {
        Assert.All(Catalog.All, t =>
        {
            Assert.NotEqual(Impact.None, t.Impact);
            Assert.False(string.IsNullOrWhiteSpace(t.Gain), t.Id);
            Assert.InRange(t.Strength, 1, 3);
        });
    }

    [Fact]
    public void Big_fps_claims_are_reserved_for_the_few_tweaks_that_earn_them()
    {
        var big = Catalog.All.Where(t => t.Impact.HasFlag(Impact.Fps) && t.Strength >= 2).Select(t => t.Id).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "power.leve-plan", "security.hvci" }, big);
    }

    [Fact]
    public void Dynamic_tweaks_are_annotated_too()
    {
        var (startup, _) = DynamicCatalog.FromStartup([new StartupEntry("Steam", "steam.exe", StartupSource.UserRun)]);
        Assert.True(startup[0].Impact.HasFlag(Impact.Ram));
        Assert.Equal(3, HardwareCatalog.GpuPerGame([@"D:\Games\game.exe"])!.Strength);
        Assert.Equal(Impact.Lows, AgentCatalog.Create(@"C:\Leve\Leve.Agent.exe").Impact);
    }
}

public class DiagnosticsTests
{
    private static readonly DateTime Now = new(2026, 11, 18);

    private static DiagnosticInput Input(
        IReadOnlyList<DisplayInfo>? displays = null,
        IReadOnlyList<MemoryModule>? memory = null,
        DateTime? driver = null,
        IReadOnlyList<DriveSpace>? drives = null,
        bool laptop = false) =>
        new(displays ?? Array.Empty<DisplayInfo>(), memory ?? Array.Empty<MemoryModule>(), driver, "GeForce RTX",
            drives ?? Array.Empty<DriveSpace>(), laptop, Now);

    [Fact]
    public void Monitor_below_native_rate_gets_a_fix_and_a_frame_cap_hint()
    {
        var display = new DisplayInfo(@"\\.\DISPLAY1", "1 (Generic PnP Monitor)", 2560, 1440, 60, 165);
        var items = Diagnostics.Build(Input(displays: [display]));

        var warn = items.First(i => i.Level == DiagnosticLevel.Warn);
        Assert.Equal(Diagnostics.RefreshTweakId(display), warn.FixTweakId);
        Assert.Contains(items, i => i.Title.Contains("Limite de FPS") && i.Detail.Contains("162"));

        var tweak = Diagnostics.RefreshTweak(display);
        Assert.Equal(Profiles.None, tweak.Profiles);
        var action = Assert.IsType<SetValue>(Assert.Single(tweak.Actions));
        Assert.Equal(Target.Display(@"\\.\DISPLAY1"), action.Target);
        Assert.Equal(StateValue.DWord(165), action.Value);
    }

    [Fact]
    public void Monitor_already_at_max_is_ok_without_fix()
    {
        var items = Diagnostics.Build(Input(displays: [new DisplayInfo(@"\\.\DISPLAY1", "1", 1920, 1080, 144, 144)]));
        Assert.Contains(items, i => i.Level == DiagnosticLevel.Ok && i.FixTweakId is null);
    }

    [Theory]
    [InlineData(MemoryKind.Ddr4, 2133, true)]
    [InlineData(MemoryKind.Ddr4, 2666, true)]
    [InlineData(MemoryKind.Ddr4, 3200, false)]
    [InlineData(MemoryKind.Ddr5, 4800, true)]
    [InlineData(MemoryKind.Ddr5, 6000, false)]
    [InlineData(MemoryKind.Lpddr5, 4800, false)]
    public void Jedec_default_speeds_are_flagged(MemoryKind kind, int mts, bool expected)
    {
        Assert.Equal(expected, Diagnostics.IsJedecDefault(kind, mts));
    }

    [Fact]
    public void Slow_single_stick_on_desktop_warns_about_xmp_and_dual_channel_but_not_on_laptop()
    {
        MemoryModule[] memory = [new MemoryModule(16384, 3200, 2133, MemoryKind.Ddr4)];

        var desktop = Diagnostics.Build(Input(memory: memory));
        Assert.Contains(desktop, i => i.Title == "RAM na velocidade padrão");
        Assert.Contains(desktop, i => i.Title == "Memória em canal único");

        var laptop = Diagnostics.Build(Input(memory: memory, laptop: true));
        Assert.DoesNotContain(laptop, i => i.Title == "RAM na velocidade padrão" || i.Title == "Memória em canal único");
    }

    [Fact]
    public void Old_driver_and_full_disk_are_flagged()
    {
        var items = Diagnostics.Build(Input(
            driver: Now.AddDays(-400),
            drives: [new DriveSpace(@"D:\", 20L << 30, 1000L << 30, HasGames: true)]));

        Assert.Contains(items, i => i.Level == DiagnosticLevel.Warn && i.Title.StartsWith("Driver de vídeo"));
        Assert.Contains(items, i => i.Level == DiagnosticLevel.Warn && i.Title.StartsWith(@"Disco D:\") && i.Detail.StartsWith("tem jogos"));
    }
}

public class SmbiosTests
{
    /// <summary>Monta uma tabela SMBIOS bruta com um dispositivo de memória (tipo 17) e o fim de tabela (127).</summary>
    private static byte[] Table(ushort sizeMb, byte type, ushort speed, ushort configured)
    {
        var device = new byte[0x22];
        device[0] = 17;
        device[1] = 0x22;
        BitConverter.GetBytes(sizeMb).CopyTo(device, 0x0C);
        device[0x12] = type;
        BitConverter.GetBytes(speed).CopyTo(device, 0x15);
        BitConverter.GetBytes(configured).CopyTo(device, 0x20);

        byte[] strings = [0, 0];
        byte[] end = [127, 4, 0, 0, 0, 0];
        var body = device.Concat(strings).Concat(end).ToArray();

        var header = new byte[8];
        BitConverter.GetBytes(body.Length).CopyTo(header, 4);
        return header.Concat(body).ToArray();
    }

    [Fact]
    public void Reads_size_type_and_speeds_of_a_memory_device()
    {
        var module = Assert.Single(Smbios.ParseMemory(Table(16384, 0x1A, 3200, 2133)));

        Assert.Equal(16384, module.SizeMb);
        Assert.Equal(MemoryKind.Ddr4, module.Kind);
        Assert.Equal(3200, module.RatedMts);
        Assert.Equal(2133, module.ConfiguredMts);
    }

    [Fact]
    public void Empty_slot_reports_zero_size_and_garbage_does_not_throw()
    {
        Assert.Equal(0, Assert.Single(Smbios.ParseMemory(Table(0, 0x22, 0, 0))).SizeMb);
        Assert.Empty(Smbios.ParseMemory([1, 2, 3]));
        Assert.Empty(Smbios.ParseMemory(new byte[64]));
    }
}
