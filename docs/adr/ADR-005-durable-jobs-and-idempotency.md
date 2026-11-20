# ADR-005: Durable jobs in PostgreSQL with leases, retries and idempotent handlers

Status: Accepted · 2026-11-20

## Context

Indexing and investigations take seconds to minutes and call external services. They must
not run inside an HTTP request, must survive a worker being killed, must not run twice at the
same time, and must be cancellable (handoff Sections 2 and 9). The handoff rules out a
message broker until throughput demands one (Section 3).

## Options

1. **In-memory background tasks in the API.** Lost on restart; no coordination between instances.
2. **A job table in PostgreSQL** claimed by workers with row locks and leases.
3. **A message broker** (RabbitMQ, a cloud queue). Purpose-built, but a second stateful
   system, and enqueueing could no longer share a transaction with the data it refers to.
4. **A job library** (for example Hangfire). Less code, but its storage schema and retry
   semantics become ours to understand and secure.

## Decision

Option 2, implemented in `EfJobQueue` (queue) and `JobProcessor` (one attempt).

- **Atomic transitions.** Every state change is a single `UPDATE` whose `WHERE` states its
  precondition. A claim selects the next row with `FOR UPDATE SKIP LOCKED`, so workers never
  block each other and no job is claimed twice.
- **Leases.** A claim sets `status = running`, a `lease_until` and a fresh `lease_token`. The
  worker renews the lease while the handler runs. A running job whose lease has expired is
  claimable again: that is how a killed worker's job is recovered.
- **Fencing.** Renewing, finishing, failing or retrying requires the claim's `lease_token`. A
  worker that lost its lease cannot change the job, and stops its handler.
- **Retries.** Any handler exception is retried with capped exponential backoff
  (5 s, 10 s, 20 s, … up to 5 min) until `max_attempts` (default 3). `JobFailedException` marks
  an error as permanent. A job whose worker died on its last attempt is failed as
  `attempts_exhausted`. An attempt longer than 10 minutes is cancelled and retried.
- **At-least-once.** A handler may run again after a partial attempt, so handlers are
  idempotent: an investigation run starts over and only stores evidence on completion; an
  index job clears builds left by dead attempts and the indexer is a no-op for an unchanged
  revision.
- **Final state reaches the work.** When a job fails for good or is cancelled, its handler's
  `OnAbandonedAsync` puts the investigation (or snapshot build) into a matching final state,
  so nothing is left "running" forever.
- **Idempotent enqueue.** `(kind, dedupe_key)` is unique. An investigation and its job are
  inserted in one transaction, so neither exists without the other.
- **Cancellation is cooperative.** A queued job is cancelled immediately; a running job is
  flagged and its handler's token is cancelled at the next heartbeat.
- **Shutdown.** A stopping worker hands its job back to the queue for immediate pickup.
- **Who runs jobs.** `Trace.Worker` always does. The API does only when
  `Jobs:ProcessInThisHost` is true (Development), so local use and browser tests need one
  process. An in-process signal wakes idle workers on enqueue; other processes poll every 2 s.

## Consequences

- No new infrastructure, and enqueueing is transactional with domain data.
- Times come from each instance's clock, not the database: instances need synchronized
  clocks, and a clock far ahead could take over a live lease (the fencing token then stops the
  original worker from recording a result, so the job runs twice, never corrupts).
- A cancelled or lease-lost handler stops only as fast as it observes its cancellation token.
- Polling adds up to 2 s of latency when the API and worker are separate processes.
- Finished jobs are never deleted; there is no operator view of failed jobs yet.
- Throughput is bounded by PostgreSQL and by `Jobs:Concurrency` (default 2) per worker.

## Revisit trigger

Measured queue latency or database load from polling; a need for scheduled or prioritized
work; or more than a few thousand jobs per minute — then consider `LISTEN/NOTIFY` for
wake-ups first, and a broker only if that is not enough.
