namespace GlDrive.Services.Operations;

public sealed record OpsEvent(DateTimeOffset Time, string Kind, string Severity, string ServerId,
    string Subject, string Detail, string Actor, string CorrelationId);

public sealed record OpsIncident(string Id, DateTimeOffset FromUtc, DateTimeOffset ToUtc, string ServerId,
    int EventCount, IReadOnlyList<OpsEvent> Events)
{
    public IReadOnlyList<OpsEvent> PrecedingChanges { get; init; } = [];
}

public interface IOpsReadCoverage
{
    bool Partial { get; }
    IReadOnlyList<string> Limits { get; }
}

internal sealed class OpsReadResult<T>(IReadOnlyList<T> items, IReadOnlyList<string> limits)
    : IReadOnlyList<T>, IOpsReadCoverage
{
    public bool Partial => Limits.Count != 0;
    public IReadOnlyList<string> Limits { get; } = limits;
    public int Count => items.Count;
    public T this[int index] => items[index];
    public IEnumerator<T> GetEnumerator() => items.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed record OpsJournalLimits(int MaxEvents = 10_000, int MaxBytes = 8 * 1024 * 1024,
    int QueueCapacity = 512, int MaxEventBytes = 16 * 1024);
