# ADR-004: Snapshot immutability and citation integrity are enforced in the database

Status: Accepted · 2026-11-20

## Context

Every TRACE answer cites a path and line range at a specific commit. A citation is only
trustworthy if the indexed content it points to cannot change afterwards, cannot come from a
different snapshot than the one the investigation was pinned to, and cannot come from another
repository. Application code will have several writers over time (indexer, orchestrator,
retention jobs), and a bug in any one of them must not be able to corrupt this.

## Options

1. **Domain model only** — factories and state checks in `Trace.Domain`. Clear errors, but any
   raw SQL, bulk operation or future code path can bypass them.
2. **Database only** — constraints and triggers. Cannot be bypassed, but failures surface late
   and as database errors.
3. **Both** — the domain model rejects invalid operations early; the database is the last line
   of defence.

## Decision

Option 3.

- **Pinned to a commit.** `index_snapshots.commit_sha` must be a full 40-character lowercase SHA
  (check constraint and domain validation).
- **Lifecycle.** A snapshot is `building`, then `completed` or `failed`. `completed_at` is set
  exactly when the status is `completed`.
- **Immutable once completed.** Triggers reject any `UPDATE` of a completed snapshot and any
  `INSERT`/`UPDATE`/`DELETE` of its chunks. The chunk trigger locks the snapshot row, so a chunk
  write racing a publish either commits first or is rejected.
- **Atomic publication.** Publishing is the single status change to `completed`; readers must
  only use completed snapshots. Earlier completed snapshots are untouched by a newer one.
- **Idempotent indexing.** A partial unique index allows one non-failed snapshot per
  (repository, commit, parser version, embedding model); a failed attempt can be retried.
- **No cross-repository or cross-snapshot citations.** Composite foreign keys require an
  investigation's snapshot to belong to its repository, and an evidence row's chunk to belong
  to the investigation's pinned snapshot.
- **Deletion.** Deleting a repository cascades to all of its data. A snapshot that
  investigations cite cannot be deleted on its own.

Each rule has an integration test in `tests/Trace.IntegrationTests/SchemaInvariantTests.cs`
that bypasses the domain model with raw SQL.

## Consequences

- A completed snapshot can never be repaired in place; fixing an index means building a new
  snapshot (for example with a new parser version).
- Triggers are hand-written SQL in a migration and are invisible to the EF Core model; changes
  to them need their own migration.
- Only a completed snapshot can be pinned, enforced in the domain model. The database does not
  check this, because the snapshot row cannot be un-completed once pinned anyway.

## Revisit trigger

Retention requirements that need partial deletion inside a completed snapshot (for example
removing an accidentally indexed secret), or index sizes where per-row triggers measurably slow
bulk chunk loading.
