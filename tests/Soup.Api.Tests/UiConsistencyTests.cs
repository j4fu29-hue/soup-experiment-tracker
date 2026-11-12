using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using Npgsql;

namespace Soup.Api.Tests;

[Collection("Soup API")]
public sealed class UiConsistencyTests
{
    private static readonly Uri BaseAddress = new(
        Environment.GetEnvironmentVariable("SOUP_BASE_URL") ?? "http://localhost:8080");

    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("SOUP_TEST_CONNECTION") ??
        "Host=localhost;Port=5432;Database=soup;Username=soup;Password=soup_local_dev";

    [Fact]
    public async Task Browser_recovers_after_disconnect_and_matches_api_and_database()
    {
        using var client = new HttpClient { BaseAddress = BaseAddress };
        using var playwright = await Playwright.CreateAsync();
        var browserChannel = Environment.GetEnvironmentVariable("SOUP_BROWSER_CHANNEL") ??
            (OperatingSystem.IsWindows() ? "msedge" : null);
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Channel = browserChannel,
            Headless = true
        });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        var unique = Guid.NewGuid().ToString("N");
        var projectName = $"browser-{unique}";
        var runName = $"disconnect-{unique}";

        await page.GotoAsync(BaseAddress.ToString());
        await page.Locator("#project-name").FillAsync(projectName);
        await page.Locator("#project-form button[type=submit]").ClickAsync();
        await Assertions.Expect(page.Locator("#connection")).ToHaveTextAsync("project connected");

        var apiKey = await page.EvaluateAsync<string>("localStorage.getItem('soup.apiKey')");
        Assert.StartsWith("soup_", apiKey);

        await page.Locator("#run-name").FillAsync(runName);
        await page.Locator("#run-form button[type=submit]").ClickAsync();
        var runButton = page.Locator("button.run", new PageLocatorOptions { HasText = runName });
        await Assertions.Expect(runButton).ToBeVisibleAsync();
        await runButton.ClickAsync();
        await Assertions.Expect(page.Locator("#connection")).ToHaveTextAsync("live · receiving");

        var runs = await GetJsonAsync(client, "/runs", apiKey);
        var runId = runs.EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == runName)
            .GetProperty("id").GetGuid();

        await context.SetOfflineAsync(true);
        await Assertions.Expect(page.Locator("#connection")).ToContainTextAsync("reconnect");

        var event0 = Guid.NewGuid();
        var event1 = Guid.NewGuid();
        var event2 = Guid.NewGuid();
        var event3 = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var firstBatch = new
        {
            batchId,
            events = new object[]
            {
                Metric(event2, 2, 0.70, "2026-11-12T00:00:02Z"),
                Metric(event0, 0, 0.90, "2026-11-12T00:00:00Z"),
                Metric(event1, 1, 0.80, "2026-11-12T00:00:01Z")
            }
        };

        using var accepted = await PostAsync(client, $"/runs/{runId}/metrics", apiKey, firstBatch);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);

        using var exactRetry = await PostAsync(client, $"/runs/{runId}/metrics", apiKey, firstBatch);
        Assert.Equal(HttpStatusCode.OK, exactRetry.StatusCode);

        using var lateAndDuplicate = await PostAsync(client, $"/runs/{runId}/metrics", apiKey, new
        {
            batchId = Guid.NewGuid(),
            events = new object[]
            {
                Metric(event0, 0, 0.90, "2026-11-12T00:00:00Z"),
                Metric(event3, 3, 0.60, "2026-11-11T23:59:00Z")
            }
        });
        Assert.Equal(HttpStatusCode.Created, lateAndDuplicate.StatusCode);

        await context.SetOfflineAsync(false);
        await Assertions.Expect(page.Locator("#event-count")).ToHaveTextAsync("4 events", new LocatorAssertionsToHaveTextOptions
        {
            Timeout = 15_000
        });
        await Assertions.Expect(page.Locator("#connection")).ToHaveTextAsync("live · receiving");

        var uiEvents = await page.Locator("#event-table tr[data-event-id]").EvaluateAllAsync<UiEvent[]>(
            "rows => rows.map(row => ({ " +
            "eventId: row.dataset.eventId, sequence: row.dataset.sequence, " +
            "metric: row.cells[1].textContent, step: row.cells[2].textContent, " +
            "value: row.cells[3].textContent, recordedAt: row.dataset.recordedAt }))");

        var apiJson = await GetJsonAsync(client, $"/runs/{runId}/metrics", apiKey);
        var apiEvents = apiJson.EnumerateArray()
            .Select(item => new StoredEvent(
                item.GetProperty("eventId").GetGuid(),
                item.GetProperty("metric").GetString()!,
                item.GetProperty("step").GetInt64(),
                item.GetProperty("value").GetDouble(),
                item.GetProperty("recordedAt").GetDateTimeOffset(),
                item.GetProperty("sequence").GetInt64()))
            .ToDictionary(item => item.EventId);

        var databaseEvents = (await ReadEventsFromDatabaseAsync(runId))
            .ToDictionary(item => item.EventId);
        var expected = new HashSet<Guid> { event0, event1, event2, event3 };

        Assert.True(expected.SetEquals(uiEvents.Select(item => Guid.Parse(item.EventId))));
        Assert.True(expected.SetEquals(apiEvents.Keys));
        Assert.True(expected.SetEquals(databaseEvents.Keys));
        foreach (var uiEvent in uiEvents)
        {
            var eventId = Guid.Parse(uiEvent.EventId);
            var databaseEvent = databaseEvents[eventId];
            Assert.Equal(databaseEvent, apiEvents[eventId]);
            Assert.Equal(databaseEvent.Sequence, long.Parse(uiEvent.Sequence, CultureInfo.InvariantCulture));
            Assert.Equal(databaseEvent.Metric, uiEvent.Metric);
            Assert.Equal(databaseEvent.Step, long.Parse(uiEvent.Step, CultureInfo.InvariantCulture));
            Assert.Equal(databaseEvent.Value, double.Parse(uiEvent.Value, CultureInfo.InvariantCulture));
            Assert.Equal(databaseEvent.RecordedAt, DateTimeOffset.Parse(uiEvent.RecordedAt, CultureInfo.InvariantCulture));
        }
    }

    private static object Metric(Guid eventId, long step, double value, string recordedAt) => new
    {
        eventId,
        metric = "loss",
        step,
        value,
        recordedAt
    };

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string path,
        string key,
        object body)
    {
        using var request = Authorized(HttpMethod.Post, path, key);
        request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path, string key)
    {
        using var request = Authorized(HttpMethod.Get, path, key);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string key)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Api-Key", key);
        return request;
    }

    private static async Task<List<StoredEvent>> ReadEventsFromDatabaseAsync(Guid runId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT \"EventId\", \"Metric\", \"Step\", \"Value\", \"RecordedAt\", \"Sequence\" " +
            "FROM metric_events WHERE \"RunId\" = @runId ORDER BY \"Sequence\"",
            connection);
        command.Parameters.AddWithValue("runId", runId);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<StoredEvent>();
        while (await reader.ReadAsync())
        {
            result.Add(new StoredEvent(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetDouble(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetInt64(5)));
        }

        return result;
    }
    private sealed class UiEvent
    {
        public string EventId { get; set; } = string.Empty;
        public string Sequence { get; set; } = string.Empty;
        public string Metric { get; set; } = string.Empty;
        public string Step { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string RecordedAt { get; set; } = string.Empty;
    }
    private sealed record StoredEvent(Guid EventId, string Metric, long Step, double Value, DateTimeOffset RecordedAt, long Sequence);

}
