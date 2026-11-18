using Leve.Core;

namespace Leve.Core.Tests;

/// <summary>Windows de mentira: Registro ausente por padrão, serviços e tarefas inexistentes até serem semeados.</summary>
internal sealed class FakeSystem : ISystemAccess
{
    private readonly Dictionary<string, StateValue> _values = new();

    public HashSet<string> FailWrites { get; } = [];
    public HashSet<string> FailReads { get; } = [];
    public int WriteCount { get; private set; }

    public void Seed(Target target, StateValue value) => _values[target.Key] = value;

    public StateValue Read(Target target)
    {
        if (FailReads.Contains(target.Key)) throw new SystemAccessException("acesso negado");
        if (_values.TryGetValue(target.Key, out var value)) return value;
        return target.Type == TargetType.Registry ? StateValue.Absent : StateValue.Missing;
    }

    public void Write(Target target, StateValue value)
    {
        if (FailWrites.Contains(target.Key)) throw new SystemAccessException("acesso negado");
        if (target.Type != TargetType.Registry && Read(target).Kind == ValueKind.Missing)
            throw new SystemAccessException("não existe");

        WriteCount++;
        if (value.Kind == ValueKind.Absent) _values.Remove(target.Key);
        else _values[target.Key] = value;
    }
}

internal sealed class MemoryStore : ISnapshotStore
{
    private readonly List<Snapshot> _snapshots = [];

    public int SaveCount { get; private set; }

    public IReadOnlyList<Snapshot> List() => _snapshots.OrderBy(s => s.Id).ToList();

    public int NextId() => _snapshots.Count == 0 ? 1 : _snapshots.Max(s => s.Id) + 1;

    public void Save(Snapshot snapshot)
    {
        SaveCount++;
        if (!_snapshots.Contains(snapshot)) _snapshots.Add(snapshot);
    }
}
