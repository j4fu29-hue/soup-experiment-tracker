# AI Usage Disclosure

This take-home was developed with assistance from OpenAI Codex.

## Assistance log

- 2026-11-12: Helped translate the task requirements into the pre-code architecture in
  `DESIGN.md`, including batch conflict semantics, durable SSE cursors, and project-isolation
  invariants.
- 2026-11-12: Assisted with scaffolding, implementation, tests, documentation, and explaining the
  trade-offs. Generated work was built and exercised locally; claims in the note are tied to raw
  output.
- 2026-11-12: Generated and iterated on the ASP.NET Core/EF Core implementation, API-key project
  scoping, idempotent ingestion, SSE catch-up client/server, dependency-free UI, Docker Compose,
  black-box xUnit tests, and load-test tool.
- 2026-11-12: Interpreted a failed `SERIALIZABLE` load run, proposed the `READ COMMITTED` plus unique
  constraint/reread approach, and reran the full E2E and 50 × 10 events/second checks. The failure
  and final raw summaries are preserved in `TEST_RESULTS.md`.
- 2026-11-12: Drafted `README.md`, `NOTES.md`, and this disclosure. Codex did not publish the work,
  contact anyone, or use a paid external service.
- 2026-11-12: Added API-key rotation, atomicity/slow-client tests, a real Edge offline/reconnect test
  comparing UI/API/PostgreSQL, and an EF Core migration. The browser test exposed and drove a fix
  for a false-live SSE state.

## Human responsibility

The candidate remains responsible for understanding every submitted file and decision, reviewing
the implementation, running the tests, and presenting limitations honestly. No code or data is
published or sent to an external service as part of this work.
