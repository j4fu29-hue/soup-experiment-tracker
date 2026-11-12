# Engineering note

## What I optimized for

The most dangerous failure is a client timing out after the server committed: the client cannot
know whether retrying will duplicate data. The second invariant is project isolation: possession of
a run UUID is never authorization. The implementation therefore makes PostgreSQL the durable source
of truth and keeps the delivery protocol small enough to explain and inspect.

## Consistency and reconnect

Clients supply `batchId` and `eventId`. One `READ COMMITTED` transaction stores a canonical SHA-256
payload hash, the batch result, and all new events. An exact retry returns the recorded result. A
changed payload under an existing batch/event ID returns `409`, and the entire conflicting batch is
rolled back. Unique constraints settle concurrent same-ID races; the loser rereads the winner.

`step` and client time are experiment data, not delivery order. PostgreSQL assigns `sequence`, and
both catch-up reads and SSE use `sequence > cursor`. The browser advances its cursor only after
applying an event and also deduplicates by `eventId`. On browser `offline`, it aborts the current
stream; its loop keeps retrying and catches up after `online`. Polling durable rows costs queries but
avoids an in-memory gap and works across instances. `LISTEN/NOTIFY` would be my first latency
optimization, only as a wake-up signal; cursor catch-up remains the correctness path.

I initially used `SERIALIZABLE`. Under 50 concurrent writers PostgreSQL rejected 480/500 independent
batches with `40001`. That was stricter but worse. `READ COMMITTED` plus atomic transactions and
unique constraints preserves the required invariants without coupling unrelated runs.

## Isolation and silent-failure checks

Only SHA-256 hashes of random 256-bit API keys are stored. Authentication resolves a project and key
ID. Every run, event, stream, and lookup query includes the authenticated project. Composite foreign
keys preserve ownership. Foreign and random IDs deliberately return the same status and body.
Production logging does not log headers/bodies or parameter values. The same indexed query shape is
used for foreign and missing IDs, reducing timing differences, but this is not constant-time; an
Internet service would need rate limits, tracing review, and statistical timing tests at its real
edge/proxy. There is no shared response cache; a regression test still warms an authorized read
before a cross-project attempt.

| Silent failure | How it is checked |
|---|---|
| Lost reconnect event | Real Edge is forced offline; events arrive; after online, UI IDs equal API and direct PostgreSQL IDs. |
| Duplicate batch/event | Exact retry, concurrent retry, and duplicate event assertions leave one durable copy. |
| Slow-client overflow | A client leaves SSE unread while 1,500 events commit, disconnects, then catches all 1,500 up by cursor. The stream is not an authoritative queue. |
| Partial write | A batch containing one new event and one conflicting event returns `409`; the new event is absent. |
| Old key after rotation | Rotation revokes the authenticated key and creates its replacement in one transaction; old read/write return `401`, new key reads successfully. |

The browser test would **not** catch a crash between PostgreSQL's physical commit and sending the
HTTP response, multi-instance proxy buffering, a multi-hour memory leak, backup corruption, or a
subtle timing side channel. The transaction/idempotency design addresses the first case, but those
remaining cases require fault injection, soak tests, proxy staging, and restore drills.

## Evidence, verdict, and concerns

On a clean migrated database, all 8 black-box tests passed. The stated 50 runs × 10 events/second for
10 seconds produced 500 batches and 5,000 durable events with zero request errors or loss: 499.8
events/second, p50 67.3 ms, p95 140.7 ms, p99 149.2 ms, max 170.8 ms. Therefore: **SHIP for the
explicit take-home workload on one API instance; DON'T SHIP as a public production service yet.**

The first change for sustained growth would be durable notification-assisted SSE plus measured query
and connection-pool tuning, followed by a real soak test. Before public use I would also add TLS at a
reverse proxy, authenticated project administration, rate limits, retention, observability, backup
and restore drills, and multi-instance tests.

The biggest surprise was that the "safer" isolation level destroyed throughput for independent
writes, and that a browser offline switch did not itself close an established streaming response.
Both were found by executable tests and changed the implementation. My main concern is not the
measured 500 events/second; it is operational evidence that a ten-second local run cannot provide.
