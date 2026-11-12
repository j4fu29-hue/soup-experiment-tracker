namespace Soup.Api.Data;

public sealed class Project
{
    public Guid Id { get; init; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class ApiKey
{
    public Guid Id { get; init; }
    public Guid ProjectId { get; init; }
    public required string Name { get; set; }
    public required string KeyHash { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class Run
{
    public Guid Id { get; init; }
    public Guid ProjectId { get; init; }
    public required string Name { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class MetricBatch
{
    public Guid ProjectId { get; init; }
    public Guid BatchId { get; init; }
    public Guid RunId { get; init; }
    public required string PayloadHash { get; init; }
    public int AcceptedCount { get; init; }
    public int DeduplicatedCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class MetricEvent
{
    public Guid ProjectId { get; init; }
    public Guid EventId { get; init; }
    public Guid RunId { get; init; }
    public required string Metric { get; init; }
    public long Step { get; init; }
    public double Value { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
    public long Sequence { get; private set; }
    public DateTimeOffset ReceivedAt { get; init; }
}
