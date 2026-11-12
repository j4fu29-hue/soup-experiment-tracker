# Soup Mini Experiment Tracker

A small experiment tracker that accepts batched metrics over an unreliable network, stores them
idempotently in PostgreSQL, and shows a live run page over Server-Sent Events (SSE).

## Quick start

Requirements: Docker Desktop with Linux containers. No paid service or external account is used.

```bash
docker compose up --build --wait
```

Open <http://localhost:8080>. Create a project in the UI, save the API key shown once, create a run,
and select it. Stop the stack without deleting its database:

```bash
docker compose down
```

## Verify

Keep the Compose stack running, then execute:

```bash
dotnet build Soup.sln
dotnet test Soup.sln --no-build --logger "console;verbosity=normal"
dotnet run --project tools/Soup.LoadTest -- http://localhost:8080 10
```

The UI test uses installed Microsoft Edge on Windows. On Linux/macOS, install Playwright Chromium
once after the build with
`pwsh tests/Soup.Api.Tests/bin/Debug/net10.0/playwright.ps1 install chromium`. Set
`SOUP_BROWSER_CHANNEL` to override the browser channel.

The E2E tests are intentionally black-box: they call the containerized HTTP API and real
PostgreSQL rather than replacing storage with an in-memory fake. The load command runs the stated
profile of 50 runs × 10 events/second for 10 seconds. See [TEST_RESULTS.md](TEST_RESULTS.md) for
captured output.

## API in one minute

`POST /projects` is the local/demo bootstrap and returns an API key once. Every other application
route requires `X-Api-Key`. `/api/...` aliases exist for the browser UI.

| Method | Route | Purpose |
|---|---|---|
| `POST` | `/projects` | Create project and first API key |
| `POST` | `/runs` | Create a run in the authenticated project |
| `GET` | `/runs` | List only that project's runs |
| `GET` | `/runs/{id}` | Read one project-scoped run |
| `POST` | `/runs/{id}/metrics` | Atomically ingest one idempotent batch |
| `GET` | `/runs/{id}/metrics?afterSequence=N` | Durable catch-up read |
| `GET` | `/runs/{id}/events?afterSequence=N` | Live SSE plus reconnect catch-up |
| `POST` | `/api-keys/rotate` | Revoke the presented key and return its replacement once |

Example metric body:

```json
{
  "batchId": "6c18c66d-8723-4810-8420-9cd3d206e57a",
  "events": [
    {
      "eventId": "de30f8c7-4dc4-4fc6-9d42-50105e8d9230",
      "metric": "loss",
      "step": 12,
      "value": 0.42,
      "recordedAt": "2026-11-12T00:00:00Z"
    }
  ]
}
```

Exact batch retries return `200` and the original result. Reusing a `batchId` with another payload,
or an `eventId` with different content, returns `409`. A foreign run ID returns `404`, so the API
does not reveal whether another project owns it.

## Repository map

- `DESIGN.md` — design written before implementation: ordering, idempotency, reconnect, isolation.
- `src/Soup.Api/Program.cs` — composition root and healthcheck mode.
- `src/Soup.Api/Data/` — EF Core entities, constraints, PostgreSQL context, startup initialization.
- `src/Soup.Api/Data/Migrations/` — versioned initial PostgreSQL schema.
- `src/Soup.Api/Auth/` — one-way API-key hashing and project authentication context.
- `src/Soup.Api/Api/` — project, run, metric, and SSE endpoints.
- `src/Soup.Api/wwwroot/` — dependency-free single-page UI and reconnecting SSE client.
- `tests/Soup.Api.Tests/EndToEndTests.cs` — consistency, concurrent retry, and isolation E2E tests.
- `tests/Soup.Api.Tests/SilentFailureTests.cs` — partial-write, rotation, slow-stream, and leakage tests.
- `tests/Soup.Api.Tests/UiConsistencyTests.cs` — real browser offline/recovery; compares UI, API, and DB.
- `tools/Soup.LoadTest/` — reproducible stated-workload measurement.
- `NOTES.md` — short engineering note and ship decision.
- `AI_USAGE.md` — AI assistance disclosure.

## A useful mental model

`step` belongs to the experiment and can arrive out of order. `sequence` belongs to storage and is
the monotonic delivery cursor. A reconnect asks for `sequence > lastAppliedSequence`; therefore a
late step is not lost, and a repeated SSE item is harmless because the browser deduplicates by
`eventId`.
