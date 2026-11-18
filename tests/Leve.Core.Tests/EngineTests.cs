using Leve.Core;

namespace Leve.Core.Tests;

public class EngineTests
{
    private static readonly Target RegA = Target.Reg(Hive.HKLM, @"SOFTWARE\Test", "A");
    private static readonly Target RegB = Target.Reg(Hive.HKCU, @"Software\Test", "B");
    private static readonly Target Svc = Target.Svc("DiagTrack");
    private static readonly Target Task = Target.ScheduledTask(@"\Microsoft\Windows\Test\Job");
    private static readonly Target Flags = Target.Reg(Hive.HKCU, @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings");

    private static Tweak MakeTweak(string id, params TweakAction[] actions) => new()
    {
        Id = id,
        Title = id,
        Area = Area.Privacy,
        Description = id,
        Actions = actions,
    };

    private static SetValue Set(Target target, StateValue value) => new(target, value);

    [Fact]
    public void Apply_then_revert_restores_existing_values_and_deletes_created_ones()
    {
        var system = new FakeSystem();
        system.Seed(RegA, StateValue.DWord(1));
        system.Seed(Svc, StateValue.ServiceState(ServiceStart.AutomaticDelayed));
        system.Seed(Task, StateValue.TaskState(true));
        var engine = new TweakEngine(system, new MemoryStore());

        var tweak = MakeTweak("t",
            Set(RegA, StateValue.DWord(0)),
            Set(RegB, StateValue.DWord(1)),
            Set(Svc, StateValue.ServiceState(ServiceStart.Disabled)),
            Set(Task, StateValue.TaskState(false)));

        var snapshot = engine.Apply(engine.BuildPlan([tweak]), "teste");

        Assert.NotNull(snapshot);
        Assert.Equal(SnapshotStatus.Committed, snapshot.Status);
        Assert.Equal(StateValue.DWord(0), system.Read(RegA));
        Assert.Equal(StateValue.DWord(1), system.Read(RegB));
        Assert.Equal(StateValue.ServiceState(ServiceStart.Disabled), system.Read(Svc));
        Assert.Equal(TweakStatus.On, engine.Evaluate(tweak));

        engine.Revert(snapshot);

        Assert.Equal(SnapshotStatus.Reverted, snapshot.Status);
        Assert.Equal(StateValue.DWord(1), system.Read(RegA));
        Assert.Equal(StateValue.Absent, system.Read(RegB));
        Assert.Equal(StateValue.ServiceState(ServiceStart.AutomaticDelayed), system.Read(Svc));
        Assert.Equal(StateValue.TaskState(true), system.Read(Task));
        Assert.Equal(TweakStatus.Off, engine.Evaluate(tweak));
    }

    [Fact]
    public void Snapshot_is_saved_before_the_first_write()
    {
        var system = new FakeSystem();
        var store = new MemoryStore();
        var engine = new TweakEngine(system, store);
        system.FailWrites.Add(RegA.Key);

        var snapshot = engine.Apply(engine.BuildPlan([MakeTweak("t", Set(RegA, StateValue.DWord(0)))]), "teste");

        Assert.NotNull(snapshot);
        Assert.Equal(2, store.SaveCount); // antes da escrita + depois
        Assert.Equal(EntryState.Failed, snapshot.Entries[0].State);
        Assert.Equal(SnapshotStatus.Partial, snapshot.Status);
    }

    [Fact]
    public void Already_applied_tweak_produces_no_changes_and_no_snapshot()
    {
        var system = new FakeSystem();
        system.Seed(RegA, StateValue.DWord(0));
        var engine = new TweakEngine(system, new MemoryStore());

        var plan = engine.BuildPlan([MakeTweak("t", Set(RegA, StateValue.DWord(0)))]);

        Assert.Empty(plan.Changes);
        Assert.Equal(new[] { "t" }, plan.AlreadyApplied);
        Assert.Null(engine.Apply(plan, "teste"));
        Assert.Equal(0, system.WriteCount);
    }

    [Fact]
    public void Missing_service_or_task_is_ignored_not_created()
    {
        var system = new FakeSystem();
        var engine = new TweakEngine(system, new MemoryStore());
        var tweak = MakeTweak("t",
            Set(Target.Svc("Fax"), StateValue.ServiceState(ServiceStart.Disabled)),
            Set(Task, StateValue.TaskState(false)));

        var plan = engine.BuildPlan([tweak]);

        Assert.Empty(plan.Changes);
        Assert.Equal(TweakStatus.NotPresent, engine.Evaluate(tweak));
    }

    [Fact]
    public void Type_matters_dword_zero_is_not_string_zero()
    {
        var system = new FakeSystem();
        system.Seed(RegA, StateValue.Text("0"));
        var engine = new TweakEngine(system, new MemoryStore());

        var plan = engine.BuildPlan([MakeTweak("t", Set(RegA, StateValue.DWord(0)))]);

        Assert.Single(plan.Changes);
        Assert.Equal(StateValue.Text("0"), plan.Changes[0].Original);
    }

    [Fact]
    public void External_change_after_apply_is_preserved_on_revert()
    {
        var system = new FakeSystem();
        system.Seed(RegA, StateValue.DWord(1));
        var engine = new TweakEngine(system, new MemoryStore());
        var snapshot = engine.Apply(engine.BuildPlan([MakeTweak("t", Set(RegA, StateValue.DWord(0)))]), "teste")!;

        system.Seed(RegA, StateValue.DWord(7)); // outro programa mexeu depois

        engine.Revert(snapshot);

        Assert.Equal(StateValue.DWord(7), system.Read(RegA));
        Assert.Equal(EntryState.Drifted, snapshot.Entries[0].State);
        Assert.Equal(SnapshotStatus.PartiallyReverted, snapshot.Status);
    }

    [Fact]
    public void Shared_target_captures_the_original_once_and_composes_flags()
    {
        var system = new FakeSystem();
        system.Seed(Flags, StateValue.Text("AutoHDREnable=1;"));
        var engine = new TweakEngine(system, new MemoryStore());

        var windowed = MakeTweak("windowed", new SetStringFlags(Flags, [new("SwapEffectUpgradeEnable", "1")]));
        var vrr = MakeTweak("vrr", new SetStringFlags(Flags, [new("VRROptimizeEnable", "1")]));

        var plan = engine.BuildPlan([windowed, vrr]);

        var change = Assert.Single(plan.Changes);
        Assert.Equal(StateValue.Text("AutoHDREnable=1;"), change.Original);
        Assert.Equal(StateValue.Text("AutoHDREnable=1;SwapEffectUpgradeEnable=1;VRROptimizeEnable=1;"), change.Desired);
        Assert.Equal(new[] { "windowed", "vrr" }, change.TweakIds);

        var snapshot = engine.Apply(plan, "teste")!;
        Assert.Equal(TweakStatus.On, engine.Evaluate(windowed));
        Assert.Equal(TweakStatus.On, engine.Evaluate(vrr));

        engine.Revert(snapshot);
        Assert.Equal(StateValue.Text("AutoHDREnable=1;"), system.Read(Flags));
    }

    [Fact]
    public void String_flags_replace_existing_key_and_keep_the_rest()
    {
        var action = new SetStringFlags(Flags, [new("SwapEffectUpgradeEnable", "1")]);

        var result = action.Desired(StateValue.Text("SwapEffectUpgradeEnable=0;VRROptimizeEnable=1;"));

        Assert.Equal(StateValue.Text("SwapEffectUpgradeEnable=1;VRROptimizeEnable=1;"), result);
    }

    [Fact]
    public void Interrupted_batch_can_be_reverted()
    {
        // Simula queda de energia: snapshot Pending no disco, só a primeira escrita aconteceu.
        var system = new FakeSystem();
        system.Seed(RegA, StateValue.DWord(1));
        var store = new MemoryStore();
        var snapshot = new Snapshot
        {
            Id = 1,
            Label = "interrompido",
            CreatedAt = DateTimeOffset.Now,
            Status = SnapshotStatus.Pending,
            Entries =
            [
                new SnapshotEntry { Target = RegA, Original = StateValue.DWord(1), Desired = StateValue.DWord(0), TweakIds = ["t"], State = EntryState.Pending },
                new SnapshotEntry { Target = RegB, Original = StateValue.Absent, Desired = StateValue.DWord(1), TweakIds = ["t"], State = EntryState.Pending },
            ],
        };
        store.Save(snapshot);
        system.Write(RegA, StateValue.DWord(0));

        new TweakEngine(system, store).Revert(snapshot);

        Assert.Equal(StateValue.DWord(1), system.Read(RegA));
        Assert.Equal(StateValue.Absent, system.Read(RegB));
        Assert.Equal(SnapshotStatus.Reverted, snapshot.Status);
    }

    [Fact]
    public void Revert_with_filter_only_touches_that_tweak()
    {
        var system = new FakeSystem();
        var engine = new TweakEngine(system, new MemoryStore());
        var one = MakeTweak("one", Set(RegA, StateValue.DWord(1)));
        var two = MakeTweak("two", Set(RegB, StateValue.DWord(1)));
        var snapshot = engine.Apply(engine.BuildPlan([one, two]), "teste")!;

        engine.Revert(snapshot, filter: e => e.TweakIds.Contains("one"));

        Assert.Equal(StateValue.Absent, system.Read(RegA));
        Assert.Equal(StateValue.DWord(1), system.Read(RegB));
        Assert.Equal(SnapshotStatus.PartiallyReverted, snapshot.Status);
        Assert.True(snapshot.CanRevert);
    }

    [Fact]
    public void Unreadable_target_is_skipped_and_reported()
    {
        var system = new FakeSystem();
        system.FailReads.Add(RegA.Key);
        var engine = new TweakEngine(system, new MemoryStore());

        var plan = engine.BuildPlan([MakeTweak("t", Set(RegA, StateValue.DWord(0)), Set(RegB, StateValue.DWord(1)))]);

        Assert.Single(plan.Issues);
        Assert.Single(plan.Changes);
        Assert.Equal(RegB, plan.Changes[0].Target);
    }

    [Fact]
    public void Snapshot_from_another_user_does_not_touch_hkcu()
    {
        var system = new FakeSystem();
        var store = new MemoryStore();
        var snapshot = new TweakEngine(system, store, "S-1-5-21-A")
            .Apply(new TweakEngine(system, store, "S-1-5-21-A").BuildPlan([MakeTweak("t", Set(RegB, StateValue.DWord(1)))]), "teste")!;

        new TweakEngine(system, store, "S-1-5-21-B").Revert(snapshot);

        Assert.Equal(StateValue.DWord(1), system.Read(RegB));
        Assert.NotEqual(SnapshotStatus.Reverted, snapshot.Status);
    }
}
