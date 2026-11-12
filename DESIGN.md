# Soup Mini Experiment Tracker — one-page design before code

## Invariants and event order

The system stores projects, hashed API keys, runs, metric batches, and metric events. Every read,
write, live subscription, identifier lookup, and future cache key is scoped by the authenticated
project. A foreign resource is returned as `404`, not `403`, so its existence is not disclosed.

Ingestion is `POST /runs/{runId}/metrics` with `{ batchId, events[] }`. Each event contains
`eventId`, `metric`, `step`, `value`, and `recordedAt`. The client-generated `eventId` is unique
within a project. `step` is logical experiment order; `recordedAt` is descriptive client time and
may be late or skewed. PostgreSQL assigns monotonic `sequence` when an event is first accepted.
Reads and SSE use sequence as their durable cursor. Events inside one atomic batch have no promised
relative sequence order; charts use `(metric, step)`.

## Atomic and idempotent batches

A batch is identified by `(projectId, batchId)`. The server normalizes the complete payload and
stores its SHA-256 hash, result counts, and all new events in one `READ COMMITTED` transaction.
Unique constraints are the final concurrency guard. If requests race, the loser rolls back and
rereads the winner.

- First delivery atomically inserts the batch and new events, then returns `201`.
- Exact retry returns the stored result with `200`; it inserts and emits nothing again.
- Same `batchId` with another run or payload returns `409 batch_payload_conflict`.
- Existing `eventId` with identical content is counted as deduplicated.
- Existing `eventId` with different content rejects the complete batch with `409`.
- Validation or database failure accepts none of the batch and never returns early success.

## Live delivery and reconnect

The run page uses SSE at `GET /runs/{runId}/events?afterSequence=N`; every item has
`id: <sequence>`. First connect and reconnect query durable rows where `sequence > cursor`. The
browser advances the cursor only after applying an item and deduplicates by `eventId`. A browser
`offline` event aborts the active request so the reconnect loop cannot remain falsely "live"; after
`online`, the same durable cursor closes the gap.

PostgreSQL remains the source of truth. The stream polls durable catch-up and waits up to one
second when current. This chooses bounded latency over an in-memory notification gap and works
across API instances without a broker. Later, PostgreSQL `LISTEN/NOTIFY` can be only a wake-up
optimization while periodic cursor catch-up remains the correctness path.

## Project isolation and storage

API keys are random 256-bit secrets; only SHA-256 hashes are stored. Authentication resolves one
project context. Every run/metric query includes both its ID and `projectId`; composite foreign
keys preserve ownership in PostgreSQL. Run IDs never authorize access. SSE authenticates before
catch-up and retains only its authorized project/run scope. The first version has no shared response
cache; regression tests still warm an authorized read before attempting the same ID from another
project. API-key rotation revokes the presented key and inserts its replacement in one transaction.

Tables are `projects`, `api_keys`, `runs`, `metric_batches`, and `metric_events`. Important unique
constraints are `(project_id, batch_id)`, `(project_id, event_id)`, and `(run_id, sequence)`.

## Verification and ship bar

One black-box consistency test covers out-of-order, duplicate and late events, exact/concurrent
retry, disconnect, new writes, reconnect, and durable convergence. Isolation tests cover read,
write, SSE, guessed IDs, and cache regression with two projects. Claim SHIP for 50 runs × 10
events/second only after containerized raw tests report latency, errors, durable count, database
growth, and limitations; before that evidence, the decision is **DON'T SHIP**.
