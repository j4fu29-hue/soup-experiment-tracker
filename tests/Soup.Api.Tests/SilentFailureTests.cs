using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Soup.Api.Tests;

[Collection("Soup API")]
public sealed class SilentFailureTests
{
    private static readonly Uri BaseAddress = new(
        Environment.GetEnvironmentVariable("SOUP_BASE_URL") ?? "http://localhost:8080");

    [Fact]
    public async Task Conflicting_batch_is_all_or_nothing()
    {
        using var client = NewClient();
        var key = await CreateProjectAsync(client, "partial-write");
        var runId = await CreateRunAsync(client, key, "atomic-run");
        var storedEventId = Guid.NewGuid();

        using var seed = await PostAsync(client, $"/runs/{runId}/metrics", key, new
        {
            batchId = Guid.NewGuid(),
            events = new[] { Metric(storedEventId, 0, 1.0) }
        });
        Assert.Equal(HttpStatusCode.Created, seed.StatusCode);

        var mustNotBeStored = Guid.NewGuid();
        using var conflict = await PostAsync(client, $"/runs/{runId}/metrics", key, new
        {
            batchId = Guid.NewGuid(),
            events = new[]
            {
                Metric(mustNotBeStored, 1, 0.9),
                Metric(storedEventId, 0, 999.0)
            }
        });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var durable = await GetJsonAsync(client, $"/runs/{runId}/metrics", key);
        Assert.Single(durable.EnumerateArray());
        Assert.Equal(storedEventId, durable[0].GetProperty("eventId").GetGuid());
        Assert.DoesNotContain(
            durable.EnumerateArray(),
            item => item.GetProperty("eventId").GetGuid() == mustNotBeStored);
    }

    [Fact]
    public async Task Rotated_key_stops_new_requests_and_closes_existing_stream()
    {
        using var client = NewClient();
        var oldKey = await CreateProjectAsync(client, "key-rotation");
        var runId = await CreateRunAsync(client, oldKey, "before-rotation");

        using var streamRequest = Authorized(HttpMethod.Get, $"/runs/{runId}/events", oldKey);
        using var oldKeyStream = await client.SendAsync(
            streamRequest,
            HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, oldKeyStream.StatusCode);
        await using var stream = await oldKeyStream.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        Assert.Equal(": connected", await reader.ReadLineAsync());

        using var rotation = await PostAsync(client, "/api-keys/rotate", oldKey, new { name = "replacement" });
        Assert.Equal(HttpStatusCode.Created, rotation.StatusCode);
        var rotationBody = await rotation.Content.ReadFromJsonAsync<JsonElement>();
        var newKey = rotationBody.GetProperty("apiKey").GetString()!;

        using var oldKeyRead = await GetAsync(client, $"/runs/{runId}", oldKey);
        Assert.Equal(HttpStatusCode.Unauthorized, oldKeyRead.StatusCode);

        using var newKeyRead = await GetAsync(client, $"/runs/{runId}", newKey);
        Assert.Equal(HttpStatusCode.OK, newKeyRead.StatusCode);

        using var streamCloseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string? line;
        do
        {
            line = await reader.ReadLineAsync(streamCloseTimeout.Token);
        } while (line is not null);

        using var oldKeyWrite = await PostAsync(client, $"/runs/{runId}/metrics", oldKey, new
        {
            batchId = Guid.NewGuid(),
            events = new[] { Metric(Guid.NewGuid(), 0, 1.0) }
        });
        Assert.Equal(HttpStatusCode.Unauthorized, oldKeyWrite.StatusCode);
    }

    [Fact]
    public async Task Slow_sse_client_can_disconnect_and_catch_up_without_event_loss()
    {
        using var client = NewClient();
        var key = await CreateProjectAsync(client, "slow-client");
        var runId = await CreateRunAsync(client, key, "slow-stream-run");

        using var slowRequest = Authorized(HttpMethod.Get, $"/runs/{runId}/events", key);
        using var slowResponse = await client.SendAsync(slowRequest, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, slowResponse.StatusCode);

        const int batchCount = 3;
        const int eventsPerBatch = 500;
        for (var batchIndex = 0; batchIndex < batchCount; batchIndex++)
        {
            var events = Enumerable.Range(0, eventsPerBatch)
                .Select(index => Metric(
                    Guid.NewGuid(),
                    batchIndex * eventsPerBatch + index,
                    1.0 / (1 + batchIndex * eventsPerBatch + index)))
                .ToArray();
            using var ingestion = await PostAsync(client, $"/runs/{runId}/metrics", key, new
            {
                batchId = Guid.NewGuid(),
                events
            });
            Assert.Equal(HttpStatusCode.Created, ingestion.StatusCode);
        }

        slowResponse.Dispose();

        var durable = await GetJsonAsync(client, $"/runs/{runId}/metrics", key);
        Assert.Equal(batchCount * eventsPerBatch, durable.GetArrayLength());

        var streamedIds = await ReadSseEventIdsAsync(
            client,
            runId,
            key,
            expectedCount: batchCount * eventsPerBatch);
        Assert.Equal(batchCount * eventsPerBatch, streamedIds.Count);
        Assert.Equal(streamedIds.Count, streamedIds.Distinct().Count());
    }

    [Fact]
    public async Task Foreign_and_unknown_ids_have_indistinguishable_errors()
    {
        using var client = NewClient();
        var keyA = await CreateProjectAsync(client, "errors-a");
        var keyB = await CreateProjectAsync(client, "errors-b");
        var runA = await CreateRunAsync(client, keyA, "secret-run");

        using var foreign = await GetAsync(client, $"/runs/{runA}", keyB);
        using var unknown = await GetAsync(client, $"/runs/{Guid.NewGuid()}", keyB);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await foreign.Content.ReadAsStringAsync());
    }

    private static HttpClient NewClient() => new() { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(30) };

    private static object Metric(Guid eventId, long step, double value) => new
    {
        eventId,
        metric = "loss",
        step,
        value,
        recordedAt = DateTimeOffset.UtcNow.AddMinutes(-step % 7)
    };

    private static async Task<string> CreateProjectAsync(HttpClient client, string prefix)
    {
        using var response = await client.PostAsJsonAsync("/projects", new
        {
            name = $"{prefix}-{Guid.NewGuid():N}",
            apiKeyName = "e2e"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("apiKey").GetString()!;
    }

    private static async Task<Guid> CreateRunAsync(HttpClient client, string key, string name)
    {
        using var response = await PostAsync(client, "/runs", key, new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string key, object body)
    {
        var request = Authorized(HttpMethod.Post, path, key);
        request.Content = JsonContent.Create(body);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string key) =>
        client.SendAsync(Authorized(HttpMethod.Get, path, key));

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path, string key)
    {
        using var response = await GetAsync(client, path, key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string key)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Api-Key", key);
        return request;
    }

    private static async Task<List<Guid>> ReadSseEventIdsAsync(
        HttpClient client,
        Guid runId,
        string key,
        int expectedCount)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var request = Authorized(HttpMethod.Get, $"/runs/{runId}/events?afterSequence=0", key);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream);
        var ids = new List<Guid>(expectedCount);
        while (ids.Count < expectedCount)
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            Assert.NotNull(line);
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = JsonSerializer.Deserialize<JsonElement>(line[6..]);
            ids.Add(payload.GetProperty("eventId").GetGuid());
        }

        return ids;
    }
}
