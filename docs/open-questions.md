# Open questions and assumptions

Assumptions made while implementing, pending an owner decision. See also Section 18 of the
[engineering handoff](TRACE_ENGINEERING_HANDOFF.md).

| # | Topic | Working assumption | Needs |
|---|---|---|---|
| 1 | License | No `LICENSE` file yet; the repository is "all rights reserved" until one is chosen. | Owner picks a license before the repo is made public. |
| 2 | .NET version | .NET 10 (current LTS), SDK pinned in `global.json`. Packages stay on 10.x even though 11.x exists. | — |
| 3 | `Trace.Evals` project | Not created yet: there is nothing to evaluate until retrieval exists. Add with the eval fixture/runner. | — |
| 4 | Health endpoints | `GET /health` (liveness, no dependencies) and `GET /health/ready` (database reachable). Both unversioned, unauthenticated, and return only the aggregate status. | — |
| 5 | Web linting | The Vite template's default `oxlint` is kept instead of ESLint. | Revisit if a needed rule is missing. |
| 6 | Local database port | Compose binds PostgreSQL to `127.0.0.1` only; host port from `POSTGRES_PORT`, default 54320 to avoid clashing with a native PostgreSQL on 5432. | — |
| 7 | Tables not created yet | Only the six tables of handoff Task 2 exist. `users`, `github_installations`, `repository_access`, `issues`, `investigation_events`, `plans` and `eval_runs` are added by the tasks that first use them. | — |
| 8 | Columns deferred with those tables | `repositories.installation_id`, `investigations.user_id` and `investigations.issue_id` do not exist yet. **There is no per-user authorization in the schema until the auth increment**; only the public demo repository may be stored before then. | Must land before any private repository is connected. |
| 9 | Embedding dimension | `source_chunks.embedding` is `vector(1024)`. 1024 was chosen because code-oriented embedding models commonly offer it; no production provider is chosen or implemented yet. Changing it needs a migration and a re-index (the model ID is part of the snapshot key, so that is routine). Vector and full-text indexes are added with retrieval (Task 4). | Owner chooses the production embedding provider/model. |
| 10 | Chunk size cap | `source_chunks.text` is capped at 16,000 characters (domain constant and check constraint). The line chunker closes a chunk early rather than exceed it. | — |
| 11 | `jobs.tenant_scope` | Stored as an opaque required string (e.g. `repository:<id>`), not a foreign key, so jobs are not removed when a repository is deleted. | Decide its exact form with the job infrastructure. |
| 12 | Development connection string | `appsettings.Development.json` contains a connection string for the loopback-only compose database, using the placeholder password from `.env.example`. It is not a secret and is never used outside Development. | Change both if you change `.env`. |
| 13 | Startup migrations | The API applies migrations and seeds the demo repository on startup only when `Database:ApplyMigrationsOnStartup` / `Database:SeedDemoData` are `true` (they are in Development). Seeding is refused in Production. How production migrations are run is undecided. | Decide with the deployment task. |
| 14 | Demo repository identity | The seed is found by owner/name (`trace-demo/sample-repo`); `repositories` has no uniqueness on owner/name because that depends on the installation model. | Revisit with GitHub App integration. |
| 15 | Embedding provider | Only `deterministic-fake` exists (hashed bag of words; no semantic understanding). It is selected by `Embeddings:Provider`, refused in Production, and there is deliberately no default, so a deployment without a real provider fails loudly instead of indexing garbage. | Implement a real adapter once a provider is chosen. |
| 16 | Revision ID of the local fixture | The fixture is a plain directory, not a Git repository, so `commit_sha` holds a content-derived SHA-1 over the indexed files (path + content, line endings normalized). Same content gives the same ID on every platform. GitHub ingestion will store real commit SHAs. | — |
| 17 | Indexing limits | 256 KB per file, 2,000 files, 20 MB total, 20 directory levels, 2,000 characters per line, 40 lines per chunk. Exceeding a repository-wide limit fails the run; an unreadable file is skipped with a reason. Values are first guesses. | Tune against real repositories. |
| 18 | File allowlist | Only `.cs`, `.ts`, `.tsx`, `.js`, `.jsx`, `.md`, `.json`, `.yml`, `.yaml` are indexed; dot-files, dot-directories, dependency/build directories and symbolic links never are. **There is no content-level secret scanning or redaction yet**, so a secret committed inside an allowlisted file would be indexed. | Add secret scanning before any non-fixture repository is indexed. |
| 19 | Interrupted index runs | A run that throws marks its snapshot `failed` and can be retried. A process killed mid-run leaves a `building` snapshot that blocks re-indexing the same revision until it is removed by hand. | Resolved by job leases in the durable job increment. |
| 20 | Chunks of failed snapshots | They are left in place; nothing reads them. | Clean up with the retention policy. |
| 21 | Skipped files | The source reader reports skipped files and reasons, but the indexer does not persist or surface them yet. | Surface with index health in the dashboard task. |
| 22 | Indexing trigger | The only caller is the Development startup seed, which indexes `tests/fixtures/sample-repo` inline. There is no API endpoint or job for indexing yet. | Arrives with the job infrastructure. |
| 23 | pgvector EF package | `Pgvector.EntityFrameworkCore` 0.3.0 declares EF Core 9 as its dependency; it works with EF Core 10 in all tests here. | Upgrade when a release targeting EF Core 10 exists. |
