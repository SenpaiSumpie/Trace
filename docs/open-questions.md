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
| 9 | Embeddings and full-text search | `source_chunks.embedding` and the FTS column/indexes are not created yet. The vector dimension depends on the embedding model, which is chosen in Task 3; the `vector` extension is enabled in that migration. | Choose embedding model/dimension in Task 3. |
| 10 | Chunk size cap | `source_chunks.text` is capped at 16,000 characters (domain constant and check constraint). | Revisit when the chunker is written. |
| 11 | `jobs.tenant_scope` | Stored as an opaque required string (e.g. `repository:<id>`), not a foreign key, so jobs are not removed when a repository is deleted. | Decide its exact form with the job infrastructure. |
| 12 | Development connection string | `appsettings.Development.json` contains a connection string for the loopback-only compose database, using the placeholder password from `.env.example`. It is not a secret and is never used outside Development. | Change both if you change `.env`. |
| 13 | Startup migrations | The API applies migrations and seeds the demo repository on startup only when `Database:ApplyMigrationsOnStartup` / `Database:SeedDemoData` are `true` (they are in Development). Seeding is refused in Production. How production migrations are run is undecided. | Decide with the deployment task. |
| 14 | Demo repository identity | The seed is found by owner/name (`trace-demo/sample-repo`); `repositories` has no uniqueness on owner/name because that depends on the installation model. | Revisit with GitHub App integration. |
