# TRACE

*Understand the code. Investigate the problem. Engineer the solution.*

TRACE is a planned AI engineering intelligence platform: evidence-backed investigations that
link source code, history and issues, with every code-specific claim pinned to a commit and
line range. The full specification is in
[docs/TRACE_ENGINEERING_HANDOFF.md](docs/TRACE_ENGINEERING_HANDOFF.md).

## Status

**A grounded question API over a fixture repository, driven by a fake model; no UI for it yet.** What exists today:

- .NET solution with the modular-monolith project layout ([ADR-001](docs/adr/ADR-001-modular-monolith-and-worker.md)).
- Domain model and PostgreSQL schema for repositories, index snapshots, source chunks,
  investigations, evidence and jobs, with snapshot immutability and citation integrity
  enforced in the database ([ADR-004](docs/adr/ADR-004-snapshot-immutability-and-citations.md)).
- Indexer that reads an allowlisted local repository directory, splits files into line-exact
  chunks, embeds them and publishes an immutable snapshot. Re-indexing unchanged content is a no-op.
- A synthetic fixture repository (`tests/fixtures/sample-repo`, a small task service with
  deliberate defects) that is indexed on startup in Development.
- Embedding provider interface with one implementation: a deterministic fake (hashed bag of
  words) for tests and local demos. It has no semantic understanding and is refused in Production.
- Hybrid retrieval inside PostgreSQL: full-text search plus exact vector search, fused by
  reciprocal rank ([ADR-003](docs/adr/ADR-003-postgres-pgvector-hybrid-search.md)), exposed as two
  bounded read-only tools, `search_code` and `read_file_range`.
- Server-side citation validation: a claimed path and line range is accepted only if it
  resolves inside the pinned snapshot.
- Question investigations: a bounded tool loop in which the model may only use those two tools,
  citations become evidence only if they are real and were retrieved in the same run, and an
  ungrounded answer is replaced by an insufficient-evidence result. Every step is stored as an
  event ([ADR-006](docs/adr/ADR-006-llm-provider-adapter-and-tool-contracts.md),
  [ADR-007](docs/adr/ADR-007-injection-and-untrusted-source-boundary.md)).
- Chat model interface with one implementation: a deterministic **extractive fake** that points
  at passages matching the question's words. It does not understand questions or code and is
  refused in Production. No real language model is integrated.
- HTTP API: `GET /api/v1/repositories`, `POST /api/v1/investigations`,
  `GET /api/v1/investigations/{id}` and `.../events`, plus `GET /health` and `GET /health/ready`.
  **Unauthenticated**; it only exposes repositories marked public (the demo fixture).
- Worker host (no jobs yet).
- React + TypeScript web shell that displays API health.
- Unit, integration and security tests; CI for build, lint, format and tests.

Not implemented: real embedding or chat providers (so no model-written answers yet),
authentication, GitHub integration, issue-to-plan, background jobs, secret scanning, rate
limiting. Retrieval has only been measured with the fake embeddings on the 17-chunk fixture.
Assumptions and deferred decisions are tracked in [docs/open-questions.md](docs/open-questions.md).
Next task: minimal UI and browser tests (handoff Section 15, Task 6).

## Prerequisites

- .NET SDK 10 (see `global.json`)
- Node.js 24
- Docker with Compose (also required to run the integration tests)

## Local setup

```sh
cp .env.example .env
docker compose --env-file .env -f infra/docker-compose.yml up -d

dotnet build
dotnet run --project src/Trace.Api --launch-profile http     # http://localhost:5120/health/ready
dotnet run --project src/Trace.Worker

cd apps/web
npm ci
npm run dev                                                  # http://localhost:5173
```

In the Development environment the API applies pending migrations, seeds the public demo
repository (`trace-demo/sample-repo`) and indexes the fixture directory on startup, using the
fake embedding provider. Demo seeding and fake embeddings are refused in Production.

Ask the fixture a question (bash, with `jq`):

```sh
repo=$(curl -s localhost:5120/api/v1/repositories | jq '.[0]')
curl -s localhost:5120/api/v1/investigations -H 'Content-Type: application/json' -d "$(jq -n \
  --argjson r "$repo" '{repositoryId: $r.id, snapshotId: $r.latestSnapshot.id, type: "question",
  question: "Which requests are let through without an API key?"}')" | jq
```

The answer comes from the extractive fake model: it lists matching passages as evidence and
says so; it is not a model-written explanation.

The web dev server proxies `/api` and `/health` to the API (`TRACE_API_URL`, default
`http://localhost:5120`).

## Database migrations

Migrations live in `src/Trace.Infrastructure/Persistence/Migrations`.

```sh
dotnet tool restore

# After changing the model:
dotnet ef migrations add <Name> --project src/Trace.Infrastructure --output-dir Persistence/Migrations

# Apply or roll back manually (bash syntax; the connection string is read from the environment):
export ConnectionStrings__Trace="Host=localhost;Port=54320;Database=trace;Username=trace;Password=<from .env>"
dotnet ef database update --project src/Trace.Infrastructure
dotnet ef database update <PreviousMigration> --project src/Trace.Infrastructure
```

## Checks

```sh
dotnet format --verify-no-changes
dotnet test            # integration tests start a disposable PostgreSQL container

cd apps/web
npm run lint
npm run build
npm test
```

## Layout

```text
apps/web/                  React + Vite + TypeScript
src/Trace.Api/             Endpoints, auth, request validation
src/Trace.Application/     Use cases, orchestration contracts
src/Trace.Domain/          Entities, domain rules
src/Trace.Infrastructure/  Database, GitHub, LLM adapters
src/Trace.Worker/          Background jobs
src/Trace.Contracts/       Shared API DTOs
tests/                     Unit, integration and security tests
tests/fixtures/sample-repo Synthetic repository that TRACE indexes (not built or run)
infra/                     Local Docker Compose
docs/                      Handoff, ADRs, open questions
```
