namespace Leve.Core;

public sealed record PlannedChange(Target Target, StateValue Original, StateValue Desired, IReadOnlyList<string> TweakIds);

public sealed record PlanIssue(Target Target, string TweakId, string Message);

/// <summary>O que um lote vai mudar, calculado lendo o estado atual antes de qualquer escrita.</summary>
public sealed record Plan(
    IReadOnlyList<Tweak> Tweaks,
    IReadOnlyList<PlannedChange> Changes,
    IReadOnlyList<string> AlreadyApplied,
    IReadOnlyList<PlanIssue> Issues);

/// <summary>
/// Motor transacional do Leve.
/// - Um lote = um snapshot. O original de cada alvo é capturado UMA vez, mesmo que vários ajustes o toquem.
/// - Aplicar: snapshot gravado antes da primeira escrita; cada escrita é confirmada por releitura.
/// - Reverter (LIFO): só restaura o que ainda está com o valor que o Leve escreveu. Mudança externa é preservada.
/// </summary>
public sealed class TweakEngine(ISystemAccess system, ISnapshotStore store, string? userSid = null)
{
    public string? UserSid => userSid;

    public TweakStatus Evaluate(Tweak tweak)
    {
        int present = 0, on = 0;
        try
        {
            foreach (var group in tweak.Actions.GroupBy(a => a.Target.Key))
            {
                var current = system.Read(group.First().Target);
                if (current.Kind == ValueKind.Missing) continue;
                present++;
                var desired = group.Aggregate(current, (value, action) => action.Desired(value));
                if (desired == current) on++;
            }
        }
        catch (SystemAccessException)
        {
            return TweakStatus.Unknown;
        }

        if (present == 0) return TweakStatus.NotPresent;
        if (on == present) return TweakStatus.On;
        return on == 0 ? TweakStatus.Off : TweakStatus.Partial;
    }

    public Plan BuildPlan(IEnumerable<Tweak> tweaks)
    {
        var list = tweaks.ToList();
        var order = new List<string>();
        var map = new Dictionary<string, Accumulator>();
        var unreadable = new HashSet<string>();
        var issues = new List<PlanIssue>();

        foreach (var tweak in list)
        {
            foreach (var action in tweak.Actions)
            {
                var key = action.Target.Key;
                if (unreadable.Contains(key))
                {
                    issues.Add(new PlanIssue(action.Target, tweak.Id, "alvo ilegível"));
                    continue;
                }

                if (!map.TryGetValue(key, out var acc))
                {
                    StateValue current;
                    try
                    {
                        current = system.Read(action.Target);
                    }
                    catch (SystemAccessException ex)
                    {
                        unreadable.Add(key);
                        issues.Add(new PlanIssue(action.Target, tweak.Id, ex.Message));
                        continue;
                    }
                    acc = new Accumulator(action.Target, current);
                    map[key] = acc;
                    order.Add(key);
                }

                if (acc.Original.Kind == ValueKind.Missing) continue;
                var before = acc.Desired;
                acc.Desired = action.Desired(before);
                // Só é "dono" da mudança quem de fato altera o valor.
                if (acc.Desired != before && !acc.TweakIds.Contains(tweak.Id)) acc.TweakIds.Add(tweak.Id);
            }
        }

        var changes = order
            .Select(k => map[k])
            .Where(a => a.Original.Kind != ValueKind.Missing && a.Desired != a.Original)
            .Select(a => new PlannedChange(a.Target, a.Original, a.Desired, a.TweakIds))
            .ToList();

        var changing = changes.SelectMany(c => c.TweakIds).ToHashSet();
        var failing = issues.Select(i => i.TweakId).ToHashSet();
        var alreadyApplied = list
            .Where(t => !changing.Contains(t.Id) && !failing.Contains(t.Id))
            .Select(t => t.Id)
            .ToList();

        return new Plan(list, changes, alreadyApplied, issues);
    }

    /// <summary>Aplica o plano. Devolve null se não houver nada a mudar.</summary>
    public Snapshot? Apply(Plan plan, string label, IProgress<LogLine>? progress = null, CancellationToken ct = default)
    {
        foreach (var issue in plan.Issues)
            progress?.Report(LogLine.Warn($"pulado: {issue.Target}: {issue.Message}"));

        if (plan.Changes.Count == 0) return null;

        var snapshot = new Snapshot
        {
            Id = store.NextId(),
            Label = label,
            CreatedAt = DateTimeOffset.Now,
            UserSid = userSid,
            Status = SnapshotStatus.Pending,
            Entries = plan.Changes.Select(c => new SnapshotEntry
            {
                Target = c.Target,
                Original = c.Original,
                Desired = c.Desired,
                TweakIds = c.TweakIds.ToList(),
                State = EntryState.Pending,
            }).ToList(),
        };

        // Regra de ouro: o estado original vai para o disco antes da primeira mutação.
        store.Save(snapshot);
        progress?.Report(LogLine.Info($"snapshot #{snapshot.Id:D4} criado ({snapshot.Entries.Count} alterações)"));

        foreach (var entry in snapshot.Entries)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                // O plano pode ter ficado esperando na confirmação: se o valor mudou desde então, não escreve.
                var now = system.Read(entry.Target);
                if (now != entry.Original)
                {
                    entry.State = EntryState.Failed;
                    entry.Message = $"mudou desde o plano (agora {now}); nada gravado";
                    progress?.Report(LogLine.Warn($"{entry.Target}: {entry.Message}"));
                    continue;
                }

                system.Write(entry.Target, entry.Desired);
                var after = system.Read(entry.Target);
                if (after == entry.Desired)
                {
                    entry.State = EntryState.Applied;
                    progress?.Report(LogLine.Ok($"{entry.Target} → {entry.Desired}  (era {entry.Original})"));
                }
                else
                {
                    entry.State = EntryState.Failed;
                    entry.Message = $"releitura devolveu {after}";
                    progress?.Report(LogLine.Error($"{entry.Target}: {entry.Message}"));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                entry.State = EntryState.Failed;
                entry.Message = ex.Message;
                progress?.Report(LogLine.Error($"{entry.Target}: {ex.Message}"));
            }
        }

        snapshot.Status = snapshot.Entries.All(e => e.State == EntryState.Applied)
            ? SnapshotStatus.Committed
            : SnapshotStatus.Partial;
        store.Save(snapshot);
        return snapshot;
    }

    /// <summary>Reverte o lote em ordem inversa. <paramref name="filter"/> limita a reversão (ex.: um único ajuste).</summary>
    public Snapshot Revert(Snapshot snapshot, IProgress<LogLine>? progress = null, Func<SnapshotEntry, bool>? filter = null)
    {
        var otherUser = snapshot.UserSid is not null && userSid is not null && snapshot.UserSid != userSid;

        foreach (var entry in Enumerable.Reverse(snapshot.Entries))
        {
            if (entry.State == EntryState.Reverted) continue;
            if (filter is not null && !filter(entry)) continue;

            if (otherUser && entry.Target.IsPerUser)
            {
                entry.Message = "snapshot pertence a outra conta; HKCU não foi tocado";
                progress?.Report(LogLine.Warn($"{entry.Target}: {entry.Message}"));
                continue;
            }

            try
            {
                var current = system.Read(entry.Target);

                if (current.Kind == ValueKind.Missing && entry.Target.Type == TargetType.Display)
                {
                    // O modo salvo volta quando o monitor for reconectado: não dá para considerar desfeito.
                    entry.State = EntryState.RevertFailed;
                    entry.Message = "monitor desconectado; reconecte e desfaça de novo";
                    progress?.Report(LogLine.Error($"{entry.Target}: {entry.Message}"));
                    continue;
                }

                if (current == entry.Original || current.Kind == ValueKind.Missing)
                {
                    entry.State = EntryState.Reverted;
                    entry.Message = current.Kind == ValueKind.Missing ? "alvo não existe mais" : null;
                    continue;
                }

                if (current != entry.Desired)
                {
                    entry.State = EntryState.Drifted;
                    entry.Message = $"alterado por fora para {current}; preservado";
                    progress?.Report(LogLine.Warn($"{entry.Target}: {entry.Message}"));
                    continue;
                }

                system.Write(entry.Target, entry.Original);
                var after = system.Read(entry.Target);
                if (after == entry.Original)
                {
                    entry.State = EntryState.Reverted;
                    entry.Message = null;
                    progress?.Report(LogLine.Ok($"{entry.Target} ← {entry.Original}"));
                }
                else
                {
                    entry.State = EntryState.RevertFailed;
                    entry.Message = $"releitura devolveu {after}";
                    progress?.Report(LogLine.Error($"{entry.Target}: {entry.Message}"));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                entry.State = EntryState.RevertFailed;
                entry.Message = ex.Message;
                progress?.Report(LogLine.Error($"{entry.Target}: {ex.Message}"));
            }
        }

        snapshot.Status = snapshot.Entries.All(e => e.State == EntryState.Reverted)
            ? SnapshotStatus.Reverted
            : SnapshotStatus.PartiallyReverted;
        store.Save(snapshot);
        return snapshot;
    }

    private sealed class Accumulator(Target target, StateValue original)
    {
        public Target Target { get; } = target;
        public StateValue Original { get; } = original;
        public StateValue Desired { get; set; } = original;
        public List<string> TweakIds { get; } = [];
    }
}
