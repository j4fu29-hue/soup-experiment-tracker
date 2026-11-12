using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Soup.Api.Tests;

[Collection("Soup API")]
public sealed class EndToEndTests
{
    private static readonly Uri BaseAddress = new(
        Environment.GetEnvironmentVariable("SOUP_BASE_URL") ?? "http://localhost:8080");

    [Fact]
    public async Task Concurrent_exact_batch_retries_commit_once_and_replay_once()
    {
        using var client = new HttpClient { BaseAddress = BaseAddress };
        var apiKey = await CreateProjectAsync(client, "concurrent-idempotency");
        var runId = await CreateRunAsync(client, apiKey, "same-batch-race");
        var eventId = Guid.NewGuid();
        var batch = new
        {
            batchId = Guid.NewGuid(),
            events = new[] { Event(eventId, 0, 0.5, "2026-11-12T00:00:00Z") }
        };

        var responses = await Task.WhenAll(
            PostAsync(client, $"/api/runs/{runId}/metrics", apiKey, batch),
            PostAsync(client, $"/api/runs/{runId}/metrics", apiKey, batch));

        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.All(responses, response => response.Dispose());

        var durableEvents = await GetJsonAsync(client, $"/api/runs/{runId}/metrics", apiKey);
        Assert.Equal(1, durableEvents.GetArrayLength());
        Assert.Equal(eventId, durableEvents[0].GetProperty("eventId").GetGuid());
    }

    [Fact]
    public async Task Consistency_survives_out_of_order_duplicates_late_events_and_reconnect()
    {
        using var client = new HttpClient { BaseAddress = BaseAddress };
        var apiKey = await CreateProjectAsync(client, "consistency");
        var runId = await CreateRunAsync(client, apiKey, "chaotic-network-run");

        var firstEventId = Guid.NewGuid();
        var secondEventId = Guid.NewGuid();
        var thirdEventId = Guid.NewGuid();
        var firstBatchId = Guid.NewGuid();
        var firstBatch = new
        {
            batchId = firstBatchId,
            events = new object[]
            {
                Event(thirdEventId, step: 2, value: 0.7, recordedAt: "2026-11-12T00:00:02Z"),
                Event(firstEventId, step: 0, value: 0.9, recordedAt: "2026-11-12T00:00:00Z"),
                Event(secondEventId, step: 1, value: 0.8, recordedAt: "2026-11-12T00:00:01Z")
            }
        };

        var accepted = await PostAsync(client, $"/api/runs/{runId}/metrics", apiKey, firstBatch);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var acceptedBody = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, acceptedBody.GetProperty("accepted").GetInt32());
        Assert.Equal(0, acceptedBody.GetProperty("deduplicated").GetInt32());

        var exactRetry = await PostAsync(client, $"/api/runs/{runId}/metrics", apiKey, firstBatch);
        Assert.Equal(HttpStatusCode.OK, exactRetry.StatusCode);
        var retryBody = await exactRetry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(retryBody.GetProperty("idempotentReplay").GetBoolean());

        var conflictingRetry = await PostAsync(client, $"/api/runs/{runId}/metrics", apiKey, new
        {
            batchId = firstBatchId,
            events = new[] { Event(firstEventId, 0, 999, "2026-11-12T00:00:00Z") }
        });
        Assert.Equal(HttpStatusCode.Conflict, conflictingRetry.StatusCode);

        var initialStreamEvents = await ReadSseEventsAsync(client, runId, apiKey, afterSequence: 0, count: 3);
        var cursorBeforeDisconnect = initialStreamEvents.Max(item => item.GetProperty("sequence").GetInt64());
        Assert.Equal(
            new long[] { 0, 1, 2 },
            initialStreamEvents.Select(item => item.GetProperty("step").GetInt64()).Order());

        var lateEventId = Guid.NewGuid();
        var secondBatch = new
        {
            batchId = Guid.NewGuid(),
            events = new object[]
            {
                Event(firstEventId, step: 0, value: 0.9, recordedAt: "2026-11-12T00:00:00Z"),
                Event(lateEventId, step: 3, value: 0.6, recordedAt: "2026-11-11T23:59:00Z")
            }
        };
        var afterDisconnect = await PostAsync(client, $"/api/runs/{runId}/metrics", apiKey, secondBatch);
        Assert.Equal(HttpStatusCode.Created, afterDisconnect.StatusCode);
        var secondBody = await afterDisconnect.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, secondBody.GetProperty("accepted").GetInt32());
        Assert.Equal(1, secondBody.GetProperty("deduplicated").GetInt32());

        var caughtUp = await ReadSseEventsAsync(
            client,
            runId,
            apiKey,
            afterSequence: cursorBeforeDisconnect,
            count: 1);
        Assert.Equal(lateEventId, caughtUp[0].GetProperty("eventId").GetGuid());

        var durableEvents = await GetJsonAsync(client, $"/api/runs/{runId}/metrics", apiKey);
        Assert.Equal(4, durableEvents.GetArrayLength());
        Assert.Equal(4, durableEvents.EnumerateArray().Select(item => item.GetProperty("eventId").GetGuid()).Distinct().Count());
        Assert.True(durableEvents.EnumerateArray().Select(item => item.GetProperty("sequence").GetInt64()).SequenceEqual(
            durableEvents.EnumerateArray().Select(item => item.GetProperty("sequence").GetInt64()).Order()));
    }

    [Fact]
    public async Task Project_isolation_covers_read_write_stream_guessing_and_cache_regression()
    {
        using var client = new HttpClient { BaseAddress = BaseAddress };
        var keyA = await CreateProjectAsync(client, "project-a");
        var keyB = await CreateProjectAsync(client, "project-b");
        var runA = await CreateRunAsync(client, keyA, "private-a");
        var runB = await CreateRunAsync(client, keyB, "private-b");

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, $"/api/runs/{runA}", keyA)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(client, $"/api/runs/{runA}", keyB)).StatusCode);

        var foreignWrite = await PostAsync(client, $"/api/runs/{runA}/metrics", keyB, new
        {
            batchId = Guid.NewGuid(),
            events = new[] { Event(Guid.NewGuid(), 0, 1, "2026-11-12T00:00:00Z") }
        });
        Assert.Equal(HttpStatusCode.NotFound, foreignWrite.StatusCode);

        using var streamRequest = AuthorizedRequest(HttpMethod.Get, $"/api/runs/{runA}/events", keyB);
        using var foreignStream = await client.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.NotFound, foreignStream.StatusCode);

        var guessedId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(client, $"/api/runs/{guessedId}", keyA)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(client, $"/api/runs/{guessedId}", keyB)).StatusCode);

        var listA = await GetJsonAsync(client, "/api/runs", keyA);
        var listB = await GetJsonAsync(client, "/api/runs", keyB);
        Assert.Contains(listA.EnumerateArray(), item => item.GetProperty("id").GetGuid() == runA);
        Assert.DoesNotContain(listA.EnumerateArray(), item => item.GetProperty("id").GetGuid() == runB);
        Assert.Contains(listB.EnumerateArray(), item => item.GetProperty("id").GetGuid() == runB);
        Assert.DoesNotContain(listB.EnumerateArray(), item => item.GetProperty("id").GetGuid() == runA);

        // Repeat after an authorized read: a cache keyed only by run id would leak here.
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, $"/api/runs/{runA}", keyA)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(client, $"/api/runs/{runA}", keyB)).StatusCode);

        using var missingKey = await client.GetAsync($"/api/runs/{runA}");
        Assert.Equal(HttpStatusCode.Unauthorized, missingKey.StatusCode);
    }

    private static object Event(Guid eventId, long step, double value, string recordedAt) => new
    {
        eventId,
        metric = "loss",
        step,
        value,
        recordedAt
    };

    private static async Task<string> CreateProjectAsync(HttpClient client, string prefix)
    {
        using var response = await client.PostAsJsonAsync("/api/projects", new
        {
            name = $"{prefix}-{Guid.NewGuid():N}",
            apiKeyName = "e2e"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("apiKey").GetString()!;
    }

    private static async Task<Guid> CreateRunAsync(HttpClient client, string apiKey, string name)
    {
        using var response = await PostAsync(client, "/api/runs", apiKey, new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string path,
        string apiKey,
        object body)
    {
        using var request = AuthorizedRequest(HttpMethod.Post, path, apiKey);
        request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string apiKey)
    {
        var request = AuthorizedRequest(HttpMethod.Get, path, apiKey);
        return client.SendAsync(request);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path, string apiKey)
    {
        using var response = await GetAsync(client, path, apiKey);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static HttpRequestMessage AuthorizedRequest(HttpMethod method, string path, string apiKey)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Api-Key", apiKey);
        return request;
    }

    private static async Task<List<JsonElement>> ReadSseEventsAsync(
        HttpClient client,
        Guid runId,
        string apiKey,
        long afterSequence,
        int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = AuthorizedRequest(
            HttpMethod.Get,
            $"/api/runs/{runId}/events?afterSequence={afterSequence}",
            apiKey);
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream);
        var result = new List<JsonElement>();
        while (result.Count < count)
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            Assert.NotNull(line);
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                result.Add(JsonSerializer.Deserialize<JsonElement>(line[6..]));
            }
        }

        return result;
    }
}

[CollectionDefinition("Soup API", DisableParallelization = true)]
public sealed class SoupApiCollection;
