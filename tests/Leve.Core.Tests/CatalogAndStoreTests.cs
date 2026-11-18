using Leve.Core;

namespace Leve.Core.Tests;

public class CatalogTests
{
    [Fact]
    public void Ids_are_unique()
    {
        var duplicates = Catalog.All.GroupBy(t => t.Id).Where(g => g.Count() > 1).Select(g => g.Key);
        Assert.Empty(duplicates);
    }

    [Fact]
    public void Every_tweak_has_actions_and_text()
    {
        Assert.All(Catalog.All, t =>
        {
            Assert.NotEmpty(t.Actions);
            Assert.False(string.IsNullOrWhiteSpace(t.Title));
            Assert.False(string.IsNullOrWhiteSpace(t.Description));
        });
    }

    [Fact]
    public void Dev_profile_is_a_subset_of_ultra()
    {
        Assert.All(Catalog.All.Where(t => t.Profiles.HasFlag(Profiles.Dev)), t => Assert.True(t.Profiles.HasFlag(Profiles.Ultra), t.Id));
    }

    [Fact]
    public void Fixed_values_never_conflict_across_tweaks()
    {
        // Dois ajustes podem tocar o mesmo alvo, mas nunca pedindo valores fixos diferentes.
        var conflicts = Catalog.All
            .SelectMany(t => t.Actions.OfType<SetValue>())
            .GroupBy(a => a.Target.Key)
            .Where(g => g.Select(a => a.Value).Distinct().Count() > 1)
            .Select(g => g.Key);
        Assert.Empty(conflicts);
    }

    [Fact]
    public void No_placebo_or_banned_targets()
    {
        string[] banned = ["Win32PrioritySeparation", "NetworkThrottlingIndex", "SystemResponsiveness", "SvcHostSplitThresholdInKB",
            "DisableAntiSpyware", "NoAutoUpdate", "GlobalTimerResolutionRequests"];
        var names = Catalog.All.SelectMany(t => t.Actions).Select(a => a.Target.Name);
        Assert.DoesNotContain(names, n => banned.Contains(n, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Xbox_services_are_never_touched()
    {
        var services = Catalog.All.SelectMany(t => t.Actions).Where(a => a.Target.Type == TargetType.Service).Select(a => a.Target.Name);
        Assert.DoesNotContain(services, s => s.StartsWith("Xbl", StringComparison.OrdinalIgnoreCase) || s.StartsWith("Xbox", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Ultra_covers_the_whole_m1_catalog()
    {
        // Tudo que não é opt-in de segurança entra no Game Ultra.
        string[] labOnly = ["gaming.mpo-off"]; // testes A/B: só manuais, por definição
        Assert.All(Catalog.All.Where(t => !t.OptIn && !labOnly.Contains(t.Id)), t => Assert.True(t.Profiles.HasFlag(Profiles.Ultra), t.Id));
        Assert.True(Catalog.All.Count >= 30);
    }

    [Fact]
    public void Printer_and_chrome_tweaks_hide_when_not_applicable()
    {
        var facts = new SystemFacts { Build = 26100, HasPhysicalPrinter = true, ChromeInstalled = false };
        Assert.False(Catalog.Find("services.print-spooler")!.IsApplicable(facts));
        Assert.False(Catalog.Find("background.chrome")!.IsApplicable(facts));
        Assert.False(Catalog.Find("privacy.recall")!.IsApplicable(facts with { Build = 22631 }));
    }
}

public class StoreTests
{
    [Fact]
    public void Json_store_round_trips_and_numbers_ids()
    {
        var dir = Path.Combine(Path.GetTempPath(), "leve-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonSnapshotStore(dir);
            Assert.Equal(1, store.NextId());

            var snapshot = new Snapshot
            {
                Id = store.NextId(),
                Label = "Game Ultra",
                CreatedAt = DateTimeOffset.Now,
                UserSid = "S-1-5-21-X",
                Status = SnapshotStatus.Committed,
                Entries =
                [
                    new SnapshotEntry
                    {
                        Target = Target.Reg(Hive.HKCU, @"Control Panel\Mouse", "MouseSpeed"),
                        Original = StateValue.Text("1"),
                        Desired = StateValue.Text("0"),
                        TweakIds = ["gaming.mouse-acceleration"],
                        State = EntryState.Applied,
                    },
                    new SnapshotEntry
                    {
                        Target = Target.Svc("DiagTrack"),
                        Original = StateValue.ServiceState(ServiceStart.AutomaticDelayed),
                        Desired = StateValue.ServiceState(ServiceStart.Disabled),
                        TweakIds = ["privacy.telemetry"],
                        State = EntryState.Applied,
                    },
                ],
            };
            store.Save(snapshot);

            var loaded = Assert.Single(store.List());
            Assert.Equal(2, store.NextId());
            Assert.Equal(snapshot.Label, loaded.Label);
            Assert.Equal(snapshot.Entries[0].Target, loaded.Entries[0].Target);
            Assert.Equal(snapshot.Entries[1].Original, loaded.Entries[1].Original);
            Assert.Equal(EntryState.Applied, loaded.Entries[1].State);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Corrupt_file_is_moved_aside_not_overwritten()
    {
        var dir = Path.Combine(Path.GetTempPath(), "leve-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonSnapshotStore(dir);
            File.WriteAllText(Path.Combine(dir, "snapshot-0001.json"), "{ quebrado");

            Assert.Empty(store.List());
            Assert.True(File.Exists(Path.Combine(dir, "snapshot-0001.json.corrompido")));
            Assert.Equal(2, store.NextId()); // nunca reutiliza o número do arquivo corrompido
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
