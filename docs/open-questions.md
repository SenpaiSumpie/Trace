# Open questions and assumptions

Assumptions made while implementing, pending an owner decision. See also Section 18 of the
[engineering handoff](TRACE_ENGINEERING_HANDOFF.md).

| # | Topic | Working assumption | Needs |
|---|---|---|---|
| 1 | License | No `LICENSE` file yet; the repository is "all rights reserved" until one is chosen. | Owner picks a license before the repo is made public. |
| 2 | .NET version | .NET 10 (current LTS), SDK pinned in `global.json`. | — |
| 3 | `Trace.Evals` project | Not created in Task 1: there is nothing to evaluate until retrieval exists. Add with the eval fixture/runner. | — |
| 4 | Health endpoint | `GET /health` is unversioned, unauthenticated and returns only the aggregate status. Dependency checks (PostgreSQL) are added with the schema task. | — |
| 5 | Web linting | The Vite template's default `oxlint` is kept instead of ESLint. | Revisit if a needed rule is missing. |
| 6 | Local database port | Compose binds PostgreSQL to `127.0.0.1` only; host port from `POSTGRES_PORT`, default 54320 to avoid clashing with a native PostgreSQL on 5432. | — |
