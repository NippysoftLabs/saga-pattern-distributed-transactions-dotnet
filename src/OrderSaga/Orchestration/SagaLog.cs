using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrderSaga.Orchestration;

public enum SagaEventType
{
    Started,
    StepStarted,
    StepCompleted,
    CompensationRequested,
    StepCompensated,
    Completed,
    Compensated,
    Stuck,
}

public sealed record SagaEvent(string SagaId, SagaEventType Type, string? Step, string? Detail, DateTimeOffset At);

/// <summary>Where the orchestrator writes what it did, so another process can pick up the saga.</summary>
public interface ISagaLog
{
    void Append(SagaEvent e);
    IReadOnlyList<SagaEvent> Read(string sagaId);
    IReadOnlyList<string> SagaIds();
}

public sealed class InMemorySagaLog : ISagaLog
{
    private readonly List<SagaEvent> _events = new();

    public void Append(SagaEvent e) => _events.Add(e);
    public IReadOnlyList<SagaEvent> Read(string sagaId) => _events.Where(e => e.SagaId == sagaId).ToList();
    public IReadOnlyList<string> SagaIds() => _events.Select(e => e.SagaId).Distinct().ToList();
}

/// <summary>
/// One JSON object per line, appended and flushed to disk before the orchestrator moves on.
/// A last line without its newline is a write cut short by a crash: it is ignored and trimmed
/// before the next append. Any other unreadable line stops the read.
/// </summary>
public sealed class FileSagaLog(string path) : ISagaLog
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly Lock _gate = new();

    public void Append(SagaEvent e)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(e, Json) + "\n");
        lock (_gate)
        {
            using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            TrimUnfinishedLine(file);
            file.Seek(0, SeekOrigin.End);
            file.Write(bytes);
            file.Flush(flushToDisk: true);
        }
    }

    public IReadOnlyList<SagaEvent> Read(string sagaId) => ReadAll().Where(e => e.SagaId == sagaId).ToList();

    public IReadOnlyList<string> SagaIds() => ReadAll().Select(e => e.SagaId).Distinct().ToList();

    private List<SagaEvent> ReadAll()
    {
        lock (_gate)
        {
            if (!File.Exists(path))
                return [];
            var text = File.ReadAllText(path, Encoding.UTF8);
            var lines = text.Split('\n');
            var events = new List<SagaEvent>();
            // The piece after the last '\n' is either empty or an unfinished write.
            for (var i = 0; i < lines.Length - 1; i++)
            {
                if (lines[i].Length == 0)
                    throw new InvalidDataException($"saga log line {i + 1} is empty");
                SagaEvent? e;
                try { e = JsonSerializer.Deserialize<SagaEvent>(lines[i], Json); }
                catch (JsonException ex) { throw new InvalidDataException($"saga log line {i + 1} is not valid JSON", ex); }
                if (e is null || string.IsNullOrWhiteSpace(e.SagaId))
                    throw new InvalidDataException($"saga log line {i + 1} has no saga id");
                events.Add(e);
            }
            return events;
        }
    }

    private static void TrimUnfinishedLine(FileStream file)
    {
        if (file.Length == 0)
            return;
        var pos = file.Length;
        while (pos > 0)
        {
            file.Seek(pos - 1, SeekOrigin.Begin);
            if (file.ReadByte() == '\n')
                break;
            pos--;
        }
        if (pos != file.Length)
            file.SetLength(pos);
    }
}
