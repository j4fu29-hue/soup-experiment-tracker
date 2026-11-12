# Raw verification output

Environment: Docker Desktop 29.9.3 (Linux containers), .NET SDK 10.0.401, Microsoft Edge
headless, local machine, 2026-11-12 Asia/Irkutsk.

## Clean database and migration

The Compose volume was deleted, the stack rebuilt, and the API started successfully. Direct query:

```text
         MigrationId
------------------------------
 20261112002341_InitialCreate
(1 row)
```

## Final build

```text
Soup.LoadTest -> tools/Soup.LoadTest/bin/Debug/net10.0/Soup.LoadTest.dll
Soup.Api -> src/Soup.Api/bin/Debug/net10.0/Soup.Api.dll
Soup.Api.Tests -> tests/Soup.Api.Tests/bin/Debug/net10.0/Soup.Api.Tests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
```

## Final E2E tests

Command:

```text
dotnet test Soup.sln --no-build --no-restore --logger "console;verbosity=normal"
```

Output:

```text
Passed UiConsistencyTests.Browser_recovers_after_disconnect_and_matches_api_and_database [8 s]
Passed SilentFailureTests.Conflicting_batch_is_all_or_nothing [65 ms]
Passed SilentFailureTests.Rotated_key_stops_new_requests_and_closes_existing_stream [1 s]
Passed SilentFailureTests.Slow_sse_client_can_disconnect_and_catch_up_without_event_loss [476 ms]
Passed SilentFailureTests.Foreign_and_unknown_ids_have_indistinguishable_errors [36 ms]
Passed EndToEndTests.Concurrent_exact_batch_retries_commit_once_and_replay_once [67 ms]
Passed EndToEndTests.Project_isolation_covers_read_write_stream_guessing_and_cache_regression [75 ms]
Passed EndToEndTests.Consistency_survives_out_of_order_duplicates_late_events_and_reconnect [76 ms]

Test Run Successful.
Total tests: 8
     Passed: 8
 Total time: 11.0756 Seconds
```

The first test drives real Edge, forces it offline, ingests out-of-order/duplicate/late events,
restores connectivity, and compares event IDs rendered in the DOM with the authenticated API and a
direct PostgreSQL query.

## Final stated-workload run

Command:

```text
dotnet run --project tools/Soup.LoadTest/Soup.LoadTest.csproj --no-build -- http://localhost:8080 10
```

Output:

```text
PROFILE runs=50 events_per_run_per_second=10 duration_seconds=10
TARGET http://localhost:8080/
SETUP created_runs=50
RESULT requests=500 errors=0 elapsed_seconds=10.006
LATENCY_MS p50=64.7 p95=100.0 p99=108.4 max=130.5
THROUGHPUT accepted_events_per_second=499.7
CONSISTENCY expected_events=5000 durable_events=5000 lost_events=0
```

This profile measures 50 concurrent run writers and durability; it does not open 50 simultaneous SSE dashboard clients.

Post-run snapshot (counts also include the eight tests):

```text
database_size | metric_events | metric_batches | runs
--------------+---------------+----------------+-----
10 MB         |          6510 |            509 |   59

soup-tracker-api-1        152.8 MiB
soup-tracker-postgres-1   150.5 MiB
```

After the final production-configuration restart, the API log contains the expected "database is
already up to date" and healthy startup messages, with no exception, HTTP 500, or failed command.

## Failed strengthening run retained

While strengthening the browser proof to compare every event field, the first Playwright run passed
7/8 tests and failed only in test-result conversion:

```text
PlaywrightException: Return type mismatch. Expecting UiEvent, got Object
MissingMethodException: No parameterless constructor defined.
```

The application and the new active-stream rotation test passed. The test DTO was changed from a
positional record to a parameterless property DTO; the browser test and then the full suite passed.

## Failure retained because it changed the design

The first load run used `SERIALIZABLE` for every batch:

```text
RESULT requests=500 errors=480 elapsed_seconds=10.003
LATENCY_MS p50=74.4 p95=248.1 p99=260.9 max=273.9
CONSISTENCY expected_events=5000 durable_events=200 lost_events=4800
PostgreSQL: 40001 could not serialize access due to read/write dependencies among transactions
```

The final implementation uses `READ COMMITTED` for atomic batch writes, database uniqueness for
same-ID races, and explicit rollback/reread reconciliation. The final tests and benchmark above were
captured after that change.
