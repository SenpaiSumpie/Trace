# TRACE

*Understand the code. Investigate the problem. Engineer the solution.*

TRACE is a planned AI engineering intelligence platform: evidence-backed investigations that
link source code, history and issues, with every code-specific claim pinned to a commit and
line range. The full specification is in
[docs/TRACE_ENGINEERING_HANDOFF.md](docs/TRACE_ENGINEERING_HANDOFF.md).

## Status

**Scaffold and database schema only.** What exists today:

- .NET solution with the modular-monolith project layout ([ADR-001](docs/adr/ADR-001-modular-monolith-and-worker.md)).
- Domain model and PostgreSQL schema for repositories, index snapshots, source chunks,
  investigations, evidence and jobs, with snapshot immutability and citation integrity
  enforced in the database ([ADR-004](docs/adr/ADR-004-snapshot-immutability-and-citations.md)).
- API with `GET /health` (liveness) and `GET /health/ready` (database) endpoints and
  ProblemDetails error responses. No product endpoints yet.
- Worker host (no jobs yet).
- React + TypeScript web shell that displays API health.
- Unit, integration and security tests; CI for build, lint, format and tests.

Not implemented: indexing, embeddings, retrieval, investigations, authentication, GitHub
integration. Nothing reads or writes the tables yet except the demo-repository seed.
Assumptions and deferred decisions are tracked in [docs/open-questions.md](docs/open-questions.md).
Next task: deterministic fixture indexer (handoff Section 15, Task 3).

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

In the Development environment the API applies pending migrations and seeds the public demo
repository (`trace-demo/sample-repo`) on startup. Demo seeding is refused in Production.

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
infra/                     Local Docker Compose
docs/                      Handoff, ADRs, open questions
```
