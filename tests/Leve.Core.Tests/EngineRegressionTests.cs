using Leve.Core;

namespace Leve.Core.Tests;

/// <summary>Casos vindos da revisão do M1.</summary>
public class EngineRegressionTests
{
    private static readonly Target RegA = Target.Reg(Hive.HKLM, @"SOFTWARE\Test", "A");
    private static readonly Target Flags = Target.Reg(Hive.HKCU, @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings");

    private static Tweak MakeTweak(string id, params TweakAction[] actions) => new()
    {
        Id = id,
        Title = id,
        Area = Area.Gaming,
        Description = id,
        Actions = actions,
    };

    [Fact]
    public void Value_changed_between_plan_and_apply_is_not_written()
    {
        var system = new FakeSystem();
        system.Seed(RegA, StateValue.DWord(1));
        var engine = new TweakEngine(system, new MemoryStore());
        var plan = engine.BuildPlan([MakeTweak("t", new SetValue(RegA, StateValue.DWord(0)))]);

        system.Seed(RegA, StateValue.DWord(5)); // mudou enquanto a confirmação estava aberta

        var snapshot = engine.Apply(plan, "teste")!;

        Assert.Equal(StateValue.DWord(5), system.Read(RegA));
        Assert.Equal(EntryState.Failed, snapshot.Entries[0].State);
    }

    [Fact]
    public void Tweak_that_changes_nothing_on_a_shared_target_does_not_own_the_entry()
    {
        var system = new FakeSystem();
        system.Seed(Flags, StateValue.Text("VRROptimizeEnable=1;"));
        var engine = new TweakEngine(system, new MemoryStore());

        var windowed = MakeTweak("windowed", new SetStringFlags(Flags, [new("SwapEffectUpgradeEnable", "1")]));
        var vrr = MakeTweak("vrr", new SetStringFlags(Flags, [new("VRROptimizeEnable", "1")]));

        var plan = engine.BuildPlan([windowed, vrr]);

        var change = Assert.Single(plan.Changes);
        Assert.Equal(new[] { "windowed" }, change.TweakIds);
        Assert.Contains("vrr", plan.AlreadyApplied);
    }

    [Fact]
    public void Every_tweak_on_an_unreadable_target_is_reported_not_marked_applied()
    {
        var system = new FakeSystem();
        system.FailReads.Add(RegA.Key);
        var engine = new TweakEngine(system, new MemoryStore());

        var plan = engine.BuildPlan([
            MakeTweak("one", new SetValue(RegA, StateValue.DWord(0))),
            MakeTweak("two", new SetValue(RegA, StateValue.DWord(0))),
        ]);

        Assert.Equal(2, plan.Issues.Count);
        Assert.Empty(plan.AlreadyApplied);
    }

    [Fact]
    public void Snapshot_with_only_drifted_entries_is_no_longer_revertible()
    {
        var system = new FakeSystem();
        system.Seed(RegA, StateValue.DWord(1));
        var engine = new TweakEngine(system, new MemoryStore());
        var snapshot = engine.Apply(engine.BuildPlan([MakeTweak("t", new SetValue(RegA, StateValue.DWord(0)))]), "teste")!;

        system.Seed(RegA, StateValue.DWord(9));
        engine.Revert(snapshot);

        Assert.False(snapshot.CanRevert); // "u" passa para o lote anterior em vez de travar aqui
    }
}
