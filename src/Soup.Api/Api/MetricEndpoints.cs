using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Soup.Api.Auth;
using Soup.Api.Data;

namespace Soup.Api.Api;

public static class MetricEndpoints
{
    private const int MaxBatchSize = 1_000;

    public static IEndpointRouteBuilder MapMetricEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/runs/{runId:guid}/metrics", IngestAsync);
        endpoints.MapGet("/api/runs/{runId:guid}/metrics", ReadAsync);
        endpoints.MapGet("/api/runs/{runId:guid}/events", StreamAsync);
        endpoints.MapPost("/runs/{runId:guid}/metrics", IngestAsync);
        endpoints.MapGet("/runs/{runId:guid}/metrics", ReadAsync);
        endpoints.MapGet("/runs/{runId:guid}/events", StreamAsync);
        return endpoints;
    }

    private static async Task<IResult> IngestAsync(
        Guid runId,
        IngestMetricsRequest request,
        ProjectContext project,
        SoupDbContext database,
        CancellationToken cancellationToken)
    {
        var validationError = Validate(request);
        if (validationError is not null)
        {
            return Results.BadRequest(new { error = "invalid_batch", message = validationError });
        }

        var runExists = await database.Runs.AsNoTracking().AnyAsync(
            run => run.ProjectId == project.ProjectId && run.Id == runId,
            cancellationToken);
        if (!runExists)
        {
            return Results.NotFound();
        }

        var normalizedEvents = request.Events
            .Select(Normalize)
            .ToList();

        foreach (var group in normalizedEvents.GroupBy(metricEvent => metricEvent.EventId))
        {
            if (group.Skip(1).Any(candidate => !SameContent(group.First(), candidate)))
            {
                return Results.Conflict(new
                {
                    error = "event_id_conflict",
                    message = $"Event {group.Key} appears more than once with different content."
                });
            }
        }

        var payloadHash = HashPayload(runId, normalizedEvents);

        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var previousBatch = await database.MetricBatches.AsNoTracking().SingleOrDefaultAsync(
            batch => batch.ProjectId == project.ProjectId && batch.BatchId == request.BatchId,
            cancellationToken);

        if (previousBatch is not null)
        {
            if (previousBatch.PayloadHash != payloadHash || previousBatch.RunId != runId)
            {
                return Results.Conflict(new
                {
                    error = "batch_payload_conflict",
                    message = "This batchId was already used with a different run or payload."
                });
            }

            var previousCursor = await MaxSequenceForEventsAsync(
                database,
                project.ProjectId,
                normalizedEvents.Select(metricEvent => metricEvent.EventId),
                cancellationToken);

            return Results.Ok(new IngestMetricsResponse(
                request.BatchId,
                previousBatch.AcceptedCount,
                previousBatch.DeduplicatedCount,
                previousCursor,
                true));
        }

        var distinctEvents = normalizedEvents
            .DistinctBy(metricEvent => metricEvent.EventId)
            .ToList();
        var eventIds = distinctEvents.Select(metricEvent => metricEvent.EventId).ToList();
        var storedEvents = await database.MetricEvents
            .Where(metricEvent => metricEvent.ProjectId == project.ProjectId &&
                                  eventIds.Contains(metricEvent.EventId))
            .ToListAsync(cancellationToken);
        var storedById = storedEvents.ToDictionary(metricEvent => metricEvent.EventId);

        foreach (var candidate in distinctEvents)
        {
            if (storedById.TryGetValue(candidate.EventId, out var stored) &&
                (stored.RunId != runId || !SameContent(stored, candidate)))
            {
                return Results.Conflict(new
                {
                    error = "event_id_conflict",
                    message = $"Event {candidate.EventId} was already stored with different content."
                });
            }
        }

        var now = DateTimeOffset.UtcNow;
        var newEvents = distinctEvents
            .Where(metricEvent => !storedById.ContainsKey(metricEvent.EventId))
            .Select(metricEvent => new MetricEvent
            {
                ProjectId = project.ProjectId,
                EventId = metricEvent.EventId,
                RunId = runId,
                Metric = metricEvent.Metric,
                Step = metricEvent.Step,
                Value = metricEvent.Value,
                RecordedAt = metricEvent.RecordedAt,
                ReceivedAt = now
            })
            .ToList();

        var acceptedCount = newEvents.Count;
        var deduplicatedCount = normalizedEvents.Count - acceptedCount;
        database.MetricEvents.AddRange(newEvents);
        database.MetricBatches.Add(new MetricBatch
        {
            ProjectId = project.ProjectId,
            BatchId = request.BatchId,
            RunId = runId,
            PayloadHash = payloadHash,
            AcceptedCount = acceptedCount,
            DeduplicatedCount = deduplicatedCount,
            CreatedAt = now
        });

        try
        {
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            database.ChangeTracker.Clear();

            var winningBatch = await database.MetricBatches.AsNoTracking().SingleOrDefaultAsync(
                batch => batch.ProjectId == project.ProjectId && batch.BatchId == request.BatchId,
                cancellationToken);
            if (winningBatch is not null &&
                winningBatch.RunId == runId &&
                winningBatch.PayloadHash == payloadHash)
            {
                var winningCursor = await MaxSequenceForEventsAsync(
                    database,
                    project.ProjectId,
                    eventIds,
                    cancellationToken);
                return Results.Ok(new IngestMetricsResponse(
                    request.BatchId,
                    winningBatch.AcceptedCount,
                    winningBatch.DeduplicatedCount,
                    winningCursor,
                    true));
            }

            return Results.Conflict(new
            {
                error = "concurrent_id_conflict",
                message = "A batch or event id was committed concurrently with different content. Retry with the same batchId to reconcile."
            });
        }

        var cursor = newEvents.Count > 0
            ? newEvents.Max(metricEvent => metricEvent.Sequence)
            : await MaxSequenceForEventsAsync(database, project.ProjectId, eventIds, cancellationToken);

        return Results.Json(
            new IngestMetricsResponse(request.BatchId, acceptedCount, deduplicatedCount, cursor, false),
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> ReadAsync(
        Guid runId,
        long? afterSequence,
        ProjectContext project,
        SoupDbContext database,
        CancellationToken cancellationToken)
    {
        var runExists = await database.Runs.AsNoTracking().AnyAsync(
            run => run.ProjectId == project.ProjectId && run.Id == runId,
            cancellationToken);
        if (!runExists)
        {
            return Results.NotFound();
        }

        var cursor = Math.Max(0, afterSequence ?? 0);
        var events = await database.MetricEvents
            .AsNoTracking()
            .Where(metricEvent => metricEvent.ProjectId == project.ProjectId &&
                                  metricEvent.RunId == runId &&
                                  metricEvent.Sequence > cursor)
            .OrderBy(metricEvent => metricEvent.Sequence)
            .Take(5_000)
            .Select(metricEvent => new MetricEventResponse(
                metricEvent.EventId,
                metricEvent.Metric,
                metricEvent.Step,
                metricEvent.Value,
                metricEvent.RecordedAt,
                metricEvent.Sequence,
                metricEvent.ReceivedAt))
            .ToListAsync(cancellationToken);

        return Results.Ok(events);
    }

    private static async Task StreamAsync(
        Guid runId,
        long? afterSequence,
        HttpContext httpContext,
        ProjectContext project,
        SoupDbContext database)
    {
        var cancellationToken = httpContext.RequestAborted;
        var runExists = await database.Runs.AsNoTracking().AnyAsync(
            run => run.ProjectId == project.ProjectId && run.Id == runId,
            cancellationToken);
        if (!runExists)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var headerCursor = long.TryParse(httpContext.Request.Headers["Last-Event-ID"], out var parsed)
            ? parsed
            : 0;
        var cursor = Math.Max(0, Math.Max(afterSequence ?? 0, headerCursor));

        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache, no-transform";
        httpContext.Response.Headers.Append("X-Accel-Buffering", "no");
        await httpContext.Response.StartAsync(cancellationToken);
        await httpContext.Response.WriteAsync(": connected\n\n", cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);

        var lastHeartbeat = DateTimeOffset.UtcNow;
        while (!cancellationToken.IsCancellationRequested)
        {
            var events = await database.MetricEvents
                .AsNoTracking()
                .Where(metricEvent => metricEvent.ProjectId == project.ProjectId &&
                                      metricEvent.RunId == runId &&
                                      metricEvent.Sequence > cursor)
                .OrderBy(metricEvent => metricEvent.Sequence)
                .Take(1_000)
                .Select(metricEvent => new MetricEventResponse(
                    metricEvent.EventId,
                    metricEvent.Metric,
                    metricEvent.Step,
                    metricEvent.Value,
                    metricEvent.RecordedAt,
                    metricEvent.Sequence,
                    metricEvent.ReceivedAt))
                .ToListAsync(cancellationToken);

            foreach (var metricEvent in events)
            {
                var json = JsonSerializer.Serialize(metricEvent, JsonSerializerOptions.Web);
                await httpContext.Response.WriteAsync(
                    $"id: {metricEvent.Sequence}\nevent: metric\ndata: {json}\n\n",
                    cancellationToken);
                cursor = metricEvent.Sequence;
            }

            if (events.Count > 0)
            {
                await httpContext.Response.Body.FlushAsync(cancellationToken);
                lastHeartbeat = DateTimeOffset.UtcNow;
                continue;
            }

            if (DateTimeOffset.UtcNow - lastHeartbeat >= TimeSpan.FromSeconds(15))
            {
                await httpContext.Response.WriteAsync(": keep-alive\n\n", cancellationToken);
                await httpContext.Response.Body.FlushAsync(cancellationToken);
                lastHeartbeat = DateTimeOffset.UtcNow;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private static string? Validate(IngestMetricsRequest request)
    {
        if (request.BatchId == Guid.Empty)
        {
            return "batchId must be a non-empty UUID.";
        }

        if (request.Events is null || request.Events.Count is < 1 or > MaxBatchSize)
        {
            return $"events must contain between 1 and {MaxBatchSize} items.";
        }

        foreach (var metricEvent in request.Events)
        {
            if (metricEvent.EventId == Guid.Empty)
            {
                return "Every eventId must be a non-empty UUID.";
            }

            if (string.IsNullOrWhiteSpace(metricEvent.Metric) || metricEvent.Metric.Trim().Length > 100)
            {
                return "metric must contain 1 to 100 characters.";
            }

            if (!double.IsFinite(metricEvent.Value))
            {
                return "value must be a finite number.";
            }
        }

        return null;
    }

    private static NormalizedMetricEvent Normalize(MetricEventRequest metricEvent) => new(
        metricEvent.EventId,
        metricEvent.Metric.Trim(),
        metricEvent.Step,
        metricEvent.Value,
        metricEvent.RecordedAt.ToUniversalTime());

    private static string HashPayload(Guid runId, IReadOnlyList<NormalizedMetricEvent> events)
    {
        var canonicalJson = JsonSerializer.Serialize(new { runId, events });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));
    }

    private static bool SameContent(NormalizedMetricEvent left, NormalizedMetricEvent right) =>
        left.Metric == right.Metric &&
        left.Step == right.Step &&
        left.Value.Equals(right.Value) &&
        left.RecordedAt.Equals(right.RecordedAt);

    private static bool SameContent(MetricEvent stored, NormalizedMetricEvent candidate) =>
        stored.Metric == candidate.Metric &&
        stored.Step == candidate.Step &&
        stored.Value.Equals(candidate.Value) &&
        stored.RecordedAt.Equals(candidate.RecordedAt);

    private static async Task<long> MaxSequenceForEventsAsync(
        SoupDbContext database,
        Guid projectId,
        IEnumerable<Guid> eventIds,
        CancellationToken cancellationToken)
    {
        var ids = eventIds.Distinct().ToList();
        return await database.MetricEvents
            .Where(metricEvent => metricEvent.ProjectId == projectId && ids.Contains(metricEvent.EventId))
            .Select(metricEvent => (long?)metricEvent.Sequence)
            .MaxAsync(cancellationToken) ?? 0;
    }

    public sealed record IngestMetricsRequest(Guid BatchId, IReadOnlyList<MetricEventRequest> Events);
    public sealed record MetricEventRequest(
        Guid EventId,
        string Metric,
        long Step,
        double Value,
        DateTimeOffset RecordedAt);
    private sealed record NormalizedMetricEvent(
        Guid EventId,
        string Metric,
        long Step,
        double Value,
        DateTimeOffset RecordedAt);
    public sealed record IngestMetricsResponse(
        Guid BatchId,
        int Accepted,
        int Deduplicated,
        long Cursor,
        bool IdempotentReplay);
    public sealed record MetricEventResponse(
        Guid EventId,
        string Metric,
        long Step,
        double Value,
        DateTimeOffset RecordedAt,
        long Sequence,
        DateTimeOffset ReceivedAt);
}
