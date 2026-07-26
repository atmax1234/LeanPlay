namespace LeanPlay.Core.Abstractions;

public sealed record AuditRecord(
    DateTimeOffset Timestamp,
    Guid? SessionId,
    string EventType,
    string Outcome,
    string? Target,
    string? Details);

public interface IAuditSink
{
    ValueTask WriteAsync(AuditRecord record, CancellationToken cancellationToken);
}

public sealed class NullAuditSink : IAuditSink
{
    public ValueTask WriteAsync(AuditRecord record, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
