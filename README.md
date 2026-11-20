# TRACE

*Understand the code. Investigate the problem. Engineer the solution.*

TRACE is a planned AI engineering intelligence platform: evidence-backed investigations that
link source code, history and issues, with every code-specific claim pinned to a commit and
line range. The full specification is in
[docs/TRACE_ENGINEERING_HANDOFF.md](docs/TRACE_ENGINEERING_HANDOFF.md).

## Status

**Scaffold only.** What exists today:

- .NET solution with the modular-monolith project layout ([ADR-001](docs/adr/ADR-001-modular-monolith-and-worker.md)).
- API with a `GET /health` endpoint and ProblemDetails error responses.
- Worker host (no jobs yet).
- React + TypeScript web shell that displays API health.
- Local PostgreSQL + pgvector via Docker Compose (not yet used by the application).
- Unit, integration and security test projects; CI for build, lint, format and tests.

Not implemented: indexing, retrieval, investigations, authentication, GitHub integration.
Next task: schema and migrations (handoff Section 15, Task 2).

## Prerequisites

- .NET SDK 10 (see `global.json`)
- Node.js 24
- Docker with Compose

## Local setup

```sh
cp .env.example .env
docker compose --env-file .env -f infra/docker-compose.yml up -d

dotnet build
dotnet run --project src/Trace.Api --launch-profile http     # http://localhost:5120/health
dotnet run --project src/Trace.Worker

cd apps/web
npm ci
npm run dev                                                  # http://localhost:5173
```

The web dev server proxies `/api` and `/health` to the API (`TRACE_API_URL`, default
`http://localhost:5120`).

## Checks

```sh
dotnet format --verify-no-changes
dotnet test

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
