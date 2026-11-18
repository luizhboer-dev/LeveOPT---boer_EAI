using System.Text.Json;
using System.Text.Json.Serialization;

namespace Leve.Core;

public enum SnapshotStatus { Pending, Committed, Partial, Reverted, PartiallyReverted }

public enum EntryState { Pending, Applied, Failed, Reverted, Drifted, RevertFailed }

/// <summary>Um alvo dentro de um lote: o valor original (capturado uma única vez) e o valor que o Leve escreveu.</summary>
public sealed class SnapshotEntry
{
    public required Target Target { get; init; }
    public required StateValue Original { get; init; }
    public required StateValue Desired { get; init; }
    public required List<string> TweakIds { get; init; }
    public EntryState State { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// Um lote aplicado. É gravado em disco ANTES da primeira escrita, então um travamento no meio
/// deixa um snapshot Pending que ainda pode ser revertido.
/// </summary>
public sealed class Snapshot
{
    public int SchemaVersion { get; init; } = 1;
    public required int Id { get; init; }
    public required string Label { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? UserSid { get; init; }
    public SnapshotStatus Status { get; set; }
    public List<SnapshotEntry> Entries { get; init; } = [];

    /// <summary>Ainda há algo que o Leve escreveu (ou pode ter escrito) e que não voltou. Itens preservados por drift não contam.</summary>
    [JsonIgnore]
    public bool CanRevert => Entries.Any(e => e.State is EntryState.Applied or EntryState.Pending
        or EntryState.Failed or EntryState.RevertFailed);

    public bool Contains(string tweakId) => Entries.Any(e => e.TweakIds.Contains(tweakId));
}

public interface ISnapshotStore
{
    IReadOnlyList<Snapshot> List();
    int NextId();
    void Save(Snapshot snapshot);
}

/// <summary>Um arquivo JSON por lote em %LOCALAPPDATA%\Leve\snapshots, com gravação atômica.</summary>
public sealed class JsonSnapshotStore : ISnapshotStore
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _dir;
    private readonly Lock _gate = new();

    public JsonSnapshotStore(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(_dir);
    }

    public IReadOnlyList<Snapshot> List()
    {
        lock (_gate)
        {
            var result = new List<Snapshot>();
            foreach (var file in Directory.GetFiles(_dir, "snapshot-*.json"))
            {
                try
                {
                    var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(file), Json);
                    if (snapshot is not null) result.Add(snapshot);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException)
                {
                    // Nunca sobrescrever um backup ilegível: tira do caminho e preserva para inspeção.
                    File.Move(file, file + ".corrompido", overwrite: true);
                }
                catch (IOException)
                {
                    // Arquivo travado por outro processo: ignora nesta leitura, tenta de novo na próxima.
                }
            }
            return result.OrderBy(s => s.Id).ToList();
        }
    }

    public int NextId()
    {
        lock (_gate)
        {
            var max = 0;
            foreach (var file in Directory.EnumerateFiles(_dir, "snapshot-*"))
            {
                var name = Path.GetFileName(file);
                var digits = new string(name["snapshot-".Length..].TakeWhile(char.IsDigit).ToArray());
                if (int.TryParse(digits, out var id)) max = Math.Max(max, id);
            }
            return max + 1;
        }
    }

    public void Save(Snapshot snapshot)
    {
        lock (_gate)
        {
            var path = Path.Combine(_dir, $"snapshot-{snapshot.Id:D4}.json");
            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, snapshot, Json);
            }
            File.Move(temp, path, overwrite: true);
        }
    }
}
