# Orientation for an agent doing one plan step

You have been given one step of `docs/plan/plan.json`. Do that step only. This page is what
you need to know before touching anything.

## What TRACE is

A web app that answers questions about a GitHub repository with evidence checked against an
indexed snapshot of the source. Spec: `docs/TRACE_ENGINEERING_HANDOFF.md`. What is left to
build: `docs/milestones.md`. Decisions: `docs/adr/`. Assumptions and known gaps (the honest
list): `docs/open-questions.md`.

## Layout

- `src/Trace.Domain` — entities and rules; depends on nothing
- `src/Trace.Application` — use cases and ports (interfaces); depends on Domain
- `src/Trace.Infrastructure` — PostgreSQL (EF Core), GitHub, Claude, Voyage, job queue
- `src/Trace.Api`, `src/Trace.Worker` — the two hosts; `src/Trace.Contracts` — API types
- `apps/web` — React + TypeScript + Vite
- `tests/Trace.UnitTests`, `tests/Trace.IntegrationTests` (real PostgreSQL through
  Testcontainers; Docker must be running), `tests/Trace.SecurityTests`
- `tests/fixtures/sample-repo` — the synthetic repository used for tests and evaluation;
  `tests/fixtures/sample-repo.questions.json` — its labeled questions;
  `tests/fixtures/sample-repo.issues/` — its invented issues, served for the demo repository.
  They sit next to the fixture, not inside it: everything inside is indexed and changes the
  revision the evaluation baseline is pinned to
- `tools/Trace.Evals` — the evaluation tool (`docs/evaluation.md`): `run` asks a dataset's
  questions, `report` computes the metrics of a result file, `compare` diffs two, `check` fails
  when a result file does not meet an expectations file (CI runs it on the smoke subset
  `tests/fixtures/sample-repo.smoke.json`). A command-line tool, not a host. Result files go
  to the git-ignored `eval/results/`. `run --snapshot <id>` evaluates a snapshot the
  application already indexed, from a copy; everything about a private repository (dataset,
  results, reports) lives under the git-ignored `eval/local/` and nowhere else
- `infra/` — compose files and the Azure template (never deployed)

A unit test enforces the dependency direction by reading the project files. Do not add a
reference that points outward. A project under `tools/` may reference the layers but never a
host, and nothing under `src/` may reference a tool.

## Commands (run from the repository root)

```powershell
docker compose --env-file .env -f infra/docker-compose.yml up -d   # database on port 54320
dotnet build                                   # warnings are errors
dotnet test --no-build                         # build first: --no-build runs stale code if the build failed
dotnet format --verify-no-changes              # CI fails on formatting; run `dotnet format` to fix
dotnet ef migrations add <Name> --project src/Trace.Infrastructure --output-dir Persistence/Migrations
```

```powershell
# in a subshell so the working directory does not change
(cd apps/web; npm run lint; npm test; npm run build)
```

Quiet test output:
`dotnet test --no-build 2>&1 | Select-String 'Passed!|Failed!|^\s+Failed |Error Message|Expected|Actual'`

If a build fails with "file is locked by Trace.Api", a local API is running: stop it with
`Get-Process Trace.Api | Stop-Process -Force`.

## How a step is finished

1. Every acceptance item of the step passes, with a command and its output as evidence.
2. `dotnet build`, all three test projects, and `dotnet format --verify-no-changes` pass. If
   you touched `apps/web`: lint, tests and build pass too.
3. Docs are updated in the same commit: `README.md` status, `docs/open-questions.md` (add or
   edit rows; never delete a limitation that is still true), `docs/milestones.md` (tick only
   boxes that are fully true), and an ADR for any decision someone would later ask "why".
4. Commit to `main` with a conventional message and push. End the message with
   `Co-Authored-By: Claude Fable 5.2 <noreply@anthropic.com>`.
5. Watch CI to the end: `gh run list --repo SenpaiSumpie/Trace --limit 1`, then
   `gh run watch <id> --repo SenpaiSumpie/Trace --exit-status`. A red run is not done: read
   `gh run view <id> --repo SenpaiSumpie/Trace --log-failed` and fix it.
6. Report: what changed, commands and key output, the commit hash, anything deferred, and
   every action the owner must take.

## Rules that are not negotiable

- **This repository is public.** Never commit secrets, the owner's private repository names,
  paths or code, or anything from an employer. `.env` is git-ignored and holds real keys:
  list key names only, never print values. Anything about the owner's own repositories goes
  under the git-ignored `eval/local/`.
- GitHub push protection rejects realistic-looking keys even in tests. Build sample keys by
  concatenating pieces. Never bypass it.
- The model is untrusted. It may only use the allow-listed read-only tools; the server binds
  the repository and snapshot; a citation counts only if it exists in the snapshot and was
  retrieved in the same run. Nothing TRACE does may write to GitHub.
- Telemetry and logs carry identifiers, counts, durations and fixed outcome names only:
  never questions, source text, model output, tokens or keys.
- Do not weaken, skip or delete a test to make it pass. If a test is wrong, say why in the
  commit message.
- Do not create anything in Azure or any other paid service. Do not register accounts.
- Say what was not verified. "Built but never run against the real service" is a required
  sentence when it is true.
- Throwaway files go in the git-ignored `tmp/` and are deleted when the check has passed.
  One verification build at a time. No backup copies of folders.

## Spending the owner's money

Claude and Voyage calls cost real money. Use the fake providers (the default in Development
and in tests) unless the step says to use the real ones. When it does:

- Keys are read from `.env` (`ANTHROPIC_API_KEY`, `VOYAGE_API_KEY`). If one is missing, do
  not ask for it: finish the rest and report it as an owner action.
- Stay under the limit the step names. One question costs roughly $0.12 to $0.25.
- Record tokens and estimated cost for everything you ran.

## Things that have already cost time

- The lease heartbeat must not share a `DbContext` with a running job handler.
- Settings read at startup are invisible to test overrides: read them when first needed.
- `plainto_tsquery` ANDs its words; the search query is relaxed to OR on purpose.
- PostgreSQL text cannot contain NUL; tool input is cleaned before it reaches SQL.
- JSON sent to the model uses the relaxed encoder so code is not turned into `\uXXXX`.
- An interpolated raw SQL string containing `'{}'::jsonb` needs the `{"{}"}` form.
- Labeled evaluation questions are written before looking at retrieval results, and tuning
  is done on the development split only, never the held-out one.
- A web test that mocks polling must return every step the UI waits for.
- GitHub reports a refused sign-in as HTTP 200 with an `error` field.
