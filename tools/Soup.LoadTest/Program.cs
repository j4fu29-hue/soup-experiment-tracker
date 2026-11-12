using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

const int runCount = 50;
const int eventsPerRunPerSecond = 10;
var durationSeconds = args.Length > 1 && int.TryParse(args[1], out var parsedDuration)
    ? parsedDuration
    : 10;
var baseAddress = new Uri(args.FirstOrDefault() ?? "http://localhost:8080");

using var client = new HttpClient
{
    BaseAddress = baseAddress,
    Timeout = TimeSpan.FromSeconds(10)
};

Console.WriteLine($"PROFILE runs={runCount} events_per_run_per_second={eventsPerRunPerSecond} duration_seconds={durationSeconds}");
Console.WriteLine($"TARGET {baseAddress}");

var projectResponse = await client.PostAsJsonAsync("/api/projects", new
{
    name = $"load-test-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}",
    apiKeyName = "load-test"
});
projectResponse.EnsureSuccessStatusCode();
var project = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
var apiKey = project.GetProperty("apiKey").GetString()!;

var runIds = await Task.WhenAll(Enumerable.Range(0, runCount).Select(async index =>
{
    using var request = Authorized(HttpMethod.Post, "/api/runs", apiKey);
    request.Content = JsonContent.Create(new { name = $"load-run-{index:D2}" });
    using var response = await client.SendAsync(request);
    response.EnsureSuccessStatusCode();
    var run = await response.Content.ReadFromJsonAsync<JsonElement>();
    return run.GetProperty("id").GetGuid();
}));

Console.WriteLine($"SETUP created_runs={runIds.Length}");

var latenciesMs = new ConcurrentBag<double>();
var errors = new ConcurrentBag<string>();
var clock = Stopwatch.StartNew();

for (var second = 0; second < durationSeconds; second++)
{
    var tick = second;
    await Task.WhenAll(runIds.Select((runId, runIndex) => SendBatchAsync(runId, runIndex, tick)));

    var nextTick = TimeSpan.FromSeconds(second + 1);
    var delay = nextTick - clock.Elapsed;
    if (delay > TimeSpan.Zero)
    {
        await Task.Delay(delay);
    }
}

var elapsed = clock.Elapsed;
var durableCounts = await Task.WhenAll(runIds.Select(async runId =>
{
    using var request = Authorized(HttpMethod.Get, $"/api/runs/{runId}/metrics", apiKey);
    using var response = await client.SendAsync(request);
    response.EnsureSuccessStatusCode();
    var events = await response.Content.ReadFromJsonAsync<JsonElement>();
    return events.GetArrayLength();
}));

var sortedLatencies = latenciesMs.Order().ToArray();
var expectedEvents = runCount * eventsPerRunPerSecond * durationSeconds;
var durableEvents = durableCounts.Sum();

Console.WriteLine($"RESULT requests={sortedLatencies.Length} errors={errors.Count} elapsed_seconds={elapsed.TotalSeconds:F3}");
Console.WriteLine($"LATENCY_MS p50={Percentile(sortedLatencies, 50):F1} p95={Percentile(sortedLatencies, 95):F1} p99={Percentile(sortedLatencies, 99):F1} max={sortedLatencies.DefaultIfEmpty().Max():F1}");
Console.WriteLine($"THROUGHPUT accepted_events_per_second={expectedEvents / elapsed.TotalSeconds:F1}");
Console.WriteLine($"CONSISTENCY expected_events={expectedEvents} durable_events={durableEvents} lost_events={expectedEvents - durableEvents}");
foreach (var error in errors.Take(10))
{
    Console.WriteLine($"ERROR {error}");
}

Environment.ExitCode = errors.IsEmpty && durableEvents == expectedEvents ? 0 : 1;

async Task SendBatchAsync(Guid runId, int runIndex, int second)
{
    var started = Stopwatch.GetTimestamp();
    try
    {
        var events = Enumerable.Range(0, eventsPerRunPerSecond).Select(eventIndex => new
        {
            eventId = Guid.NewGuid(),
            metric = "loss",
            step = second * eventsPerRunPerSecond + eventIndex,
            value = 1.0 / (1 + second * eventsPerRunPerSecond + eventIndex + runIndex),
            recordedAt = DateTimeOffset.UtcNow.AddMilliseconds(eventIndex)
        }).ToArray();

        using var request = Authorized(HttpMethod.Post, $"/api/runs/{runId}/metrics", apiKey);
        request.Content = JsonContent.Create(new { batchId = Guid.NewGuid(), events });
        using var response = await client.SendAsync(request);
        if ((int)response.StatusCode != 201)
        {
            var body = await response.Content.ReadAsStringAsync();
            errors.Add($"run={runIndex} second={second} status={(int)response.StatusCode} body={body[..Math.Min(300, body.Length)]}");
        }
    }
    catch (Exception exception)
    {
        errors.Add($"run={runIndex} second={second} exception={exception.GetType().Name}:{exception.Message}");
    }
    finally
    {
        latenciesMs.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
}

static HttpRequestMessage Authorized(HttpMethod method, string path, string apiKey)
{
    var request = new HttpRequestMessage(method, path);
    request.Headers.Add("X-Api-Key", apiKey);
    return request;
}

static double Percentile(double[] sorted, int percentile)
{
    if (sorted.Length == 0)
    {
        return 0;
    }

    var index = (int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1;
    return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
}
