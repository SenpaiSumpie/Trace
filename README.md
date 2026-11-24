# TRACE

*Understand the code. Investigate the problem. Engineer the solution.*

TRACE is a planned AI engineering intelligence platform: evidence-backed investigations that
link source code, history and issues, with every code-specific claim pinned to a commit and
line range. The full specification is in
[docs/TRACE_ENGINEERING_HANDOFF.md](docs/TRACE_ENGINEERING_HANDOFF.md).

## Status

**The first vertical slice works end to end in a browser, on a fixture repository, driven by fake models.** What exists today:

- .NET solution with the modular-monolith project layout ([ADR-001](docs/adr/ADR-001-modular-monolith-and-worker.md)).
- Domain model and PostgreSQL schema for repositories, index snapshots, source chunks,
  investigations, evidence and jobs, with snapshot immutability and citation integrity
  enforced in the database ([ADR-004](docs/adr/ADR-004-snapshot-immutability-and-citations.md)).
- Indexer that reads an allowlisted local repository directory, splits files into line-exact
  chunks, embeds them and publishes an immutable snapshot. Re-indexing unchanged content is a no-op.
- A synthetic fixture repository (`tests/fixtures/sample-repo`, a small task service with
  deliberate defects) that is indexed on startup in Development.
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
- Model providers behind two small interfaces: Anthropic Claude for answers and Voyage AI for
  embeddings, plus deterministic fakes for tests and local use (refused in Production). The fakes
  are what runs by default locally: the chat fake only points at passages matching the question's
  words. **The real adapters have not yet been run against the live APIs.**
- Sign in with GitHub and private repositories through a read-only GitHub App: access is taken
  from GitHub at sign-in, every endpoint applies one visibility rule, webhooks revoke access and
  delete data, and files that look like they contain credentials are never indexed
  ([ADR-002](docs/adr/ADR-002-github-app-auth-and-authorization.md)). **Not yet tried against the
  real GitHub: the app has to be registered first** ([setup](docs/github-app-setup.md)).
- Reading GitHub issues, read-only: list a repository's open issues and fetch one with its
  comments, for a repository the caller can see, with size limits whose every cut is flagged
  and pull requests left out. Issue text is fetched on demand and not stored, never reaches a
  log or a span, and reaches a model only in a plan run, as quoted data. The demo repository serves invented fixture
  issues from files ([ADR-015](docs/adr/ADR-015-reading-github-issues.md)). **Never run against
  real GitHub issues: the GitHub App first needs a read-only Issues permission that only its
  owner can approve** ([how](docs/github-app-setup.md#adding-the-issues-permission-to-an-existing-app)).
- Storage for implementation plans: a `plans` table tied to one
  issue-to-plan investigation, the issue it was made from (number, title and a hash of its
  text; the text itself is not stored), a versioned result document
  ([docs/plan-result-schema.md](docs/plan-result-schema.md)) whose citations are stored
  evidence rows, and a reviewed timestamp. The database refuses a plan for the wrong
  investigation, a citation that is not evidence of the plan's own investigation, and any
  change to a finished plan other than marking it reviewed once
  ([ADR-016](docs/adr/ADR-016-plan-storage.md)).
- Issue-to-plan investigations: a durable job reads the
  issue, shows it to the model as quoted data, lets the model explore with the same two
  read-only tools and limits as a question, and stores a plan made only of what the server
  could verify of the model's proposal. A step, risk, verification entry or changed file is
  removed unless every citation on it is in the snapshot and was retrieved in the run; a file
  must exist in the snapshot unless it is marked new; a proposal with no valid citation is
  stored as insufficient evidence, not as a plan; what was removed is counted and listed in
  the investigation's events ([ADR-017](docs/adr/ADR-017-issue-to-plan-validation.md)).
  **A real model has made plans only in one evaluation run, on the fixture and its invented
  issues (2026-11-24): five good plans, one correct refusal, and two of eight runs that
  failed because the model's reply could not be used. No real GitHub issue has been read by a
  run, and the application itself has only ever run with the fake model**, whose plan is a
  list of passages matching the issue's title.
- Plans over HTTP ([ADR-018](docs/adr/ADR-018-plan-api-and-markdown-export.md)):
  `POST /api/v1/repositories/{id}/issues/{number}/plans` queues a plan (202, then poll;
  idempotency key and rate limit as for a question), `GET /api/v1/plans/{id}` returns its
  state, the issue's number and title, the pinned commit, the model and prompt version, the
  plan with its evidence, and what validation removed; `?checkIssue=true` also says whether
  the issue has changed since. `POST /api/v1/plans/{id}/review` records that a person read the
  plan: one timestamp in TRACE, nothing anywhere else. `GET /api/v1/plans/{id}/export` returns
  the plan as a Markdown file in which every piece of text from the issue, the model or the
  repository is quoted in a code span or a fenced block, so it cannot add a heading, a link,
  HTML or a forged "Reviewed" line; items without evidence are marked as the model's
  unsupported statements. A plan of a repository the caller cannot see is a 404 on all four.
  **Tested with the fake model, scripted hostile models and a stub of GitHub only; no export
  has been pasted into the real GitHub.**
- Deployment artifacts: production images for web, API and worker, a compose file that runs the
  whole stack from them, and an Azure template (Container Apps + PostgreSQL). **Nothing has been
  deployed**; see [docs/deployment.md](docs/deployment.md).
- Spending guards: a per-client rate limit on requests that start work, a token limit per
  investigation and a shared daily token budget, all failing closed.
- HTTP API under `/api/v1`: list repositories, queue an investigation (202, then poll), read an
  investigation and its events, queue a re-index, read and cancel jobs, list a repository's
  issues (paged by cursor) and fetch one, queue a plan for an issue, read it, mark it as
  reviewed and export it as Markdown, and list your own past investigations
  (`GET /api/v1/investigations`, paged by cursor, filterable by repository and type); plus
  `GET /health` and `GET /health/ready`.
  Anonymous callers see only the public demo repository; in production, asking questions,
  asking for a plan and marking one as reviewed need sign-in.
- Web UI: pick the indexed repository, ask a question, read the answer, the evidence as source
  listings with real line numbers, what was not settled, and every step the investigation took.
- Web UI, issue to plan: switch from the question form to "Plan from an issue", pick a
  repository, page through its open issues, read one as plain text (with notes where it was
  cut), start a plan, watch its steps, then read the plan: summary, ordered steps, files to
  change, risks, verification and unknowns, each with a control that shows the cited source
  lines, plus what validation removed, the pinned commit, model and prompt. A sentence the
  server verified nothing for is labelled "Not backed by evidence". The plan can be marked as
  reviewed (an acknowledgement inside TRACE), downloaded as the server's Markdown export, and
  checked for a changed issue. The plan is rendered from its data as ordinary text; nothing
  from an issue, the source or the model is ever treated as markup, Markdown or a link, and
  tests feed hostile strings into every such field. **Only ever shown fake-model plans of the
  fixture issues.**
- History and addresses ([ADR-020](docs/adr/ADR-020-investigation-history.md),
  [ADR-021](docs/adr/ADR-021-web-addresses.md)): every investigation has the address
  `/investigations/{id}` and every plan `/plans/{id}`. Asking a question or starting a plan
  moves the browser to that address, so a reload shows the same result, an unfinished run
  opened by its address shows progress until it finishes, and the address can be given to
  anyone who may see the repository. An address that names nothing, or something the reader
  may not see, shows one "Page not found" page that does not say which. `/history` lists
  **what you asked yourself**, newest first, with filters and "Load more": a signed-in user
  sees the investigations that user asked for, in repositories still visible to them;
  someone who is not signed in sees what was asked from the same browser (a random,
  `HttpOnly` visitor cookie whose hash is stored with the investigation) and nothing else.
  Sharing a repository does not share history, and investigations from before this existed
  that have no recorded user are in nobody's list, though still reachable by address. The web
  app uses a small router of its own on the History API; no dependency was added.
- Durable background jobs in PostgreSQL: investigations and indexing are queued and run by the
  worker with leases, retries with backoff, takeover when a worker dies, and cancellation
  ([ADR-005](docs/adr/ADR-005-durable-jobs-and-idempotency.md)).
- OpenTelemetry traces and metrics, and a correlation ID that follows a question from the
  request through its job, model calls and tool calls
  ([ADR-008](docs/adr/ADR-008-telemetry-and-correlation.md)). **Not yet sent to a real
  collector.**
- An evaluation runner (`tools/Trace.Evals`): one command puts a dataset of labeled questions
  through the same investigation pipeline the API uses and writes one result file per run with,
  per question, the outcome, answer, checked evidence, tool calls, tokens, estimated cost and
  time ([docs/evaluation.md](docs/evaluation.md), [ADR-009](docs/adr/ADR-009-evaluation-runner.md)).
  A `report` command computes the question metrics from a result file, overall and per split
  (retrieval recall at 10, citation validity, abstention precision and recall, cost and time),
  and a `compare` command lists what changed between two runs question by question,
  regressions first ([ADR-010](docs/adr/ADR-010-evaluation-metrics.md)).
  The fixture has 34 labeled questions (15 development, 19 held-out), 7 of which the
  repository cannot answer ([ADR-011](docs/adr/ADR-011-fixture-question-set.md)).
  CI runs six of the development questions with the fake models on every push and fails the
  build when a `check` command finds a written expectation unmet
  ([ADR-012](docs/adr/ADR-012-evaluation-smoke-check.md)); that guards the plumbing, not
  answer quality.
  A baseline with the real models exists for the fixture
  ([docs/eval/2026-11-23-fixture-baseline.md](docs/eval/2026-11-23-fixture-baseline.md)):
  on the held-out questions every citation was valid (68 of 68) and every labeled location
  was in the top 10 (16 of 16). **It is one run of 34 questions on a 27-passage fixture, so it
  shows the pipeline works with Claude and Voyage, not how good retrieval is on a real
  repository.**
  A repository indexed through the application can be measured too: `run --snapshot` evaluates
  a copy of its indexed snapshot without re-indexing it and without writing to the database it
  lives in ([ADR-014](docs/adr/ADR-014-evaluating-an-indexed-snapshot.md)). One private
  repository has a draft set of 19 questions and one real-model baseline (2026-11-23); both
  are kept out of this repository, and **the labels are drafts its owner has not reviewed
  yet**.
- Plan evaluation ([ADR-019](docs/adr/ADR-019-plan-rubric.md)): eight of the fixture's issues
  are labeled (four development, four held-out; two of them cannot be planned from the
  repository, one is a prompt-injection attempt) with the files a correct plan must touch,
  what it must say, the risks it must mention and what its verification must cover.
  `plan-run` has the production pipeline plan them and `plan-report` scores the result on
  four dimensions (completeness, correct files, safety, verification), each with a part that
  is computed and a part a person rates from a checklist; no keyword matching and no judge
  model stand in for the person. One baseline with the real models exists
  ([docs/eval/2026-11-24-fixture-plan-baseline.md](docs/eval/2026-11-24-fixture-plan-baseline.md)):
  **73.2% against the handoff's 80% target (74.7% on the computed part alone). The target is
  missed because two of the eight runs failed with `invalid_model_output`, for a reason that
  is not yet known; the six that completed score 97.6%. The rated part was rated by the agent
  that built the evaluation, not by the owner or an independent reviewer.**
- Unit, integration and security tests, web component tests, and Playwright browser tests of
  the whole flow; CI runs all of them.

Not implemented: a cure or even a diagnosis for the plan runs that fail on a real model's
reply (2 of 13 so far), plans from real GitHub issues, running work shown again after a
reload anywhere but at its own address, deleting an investigation, a telemetry backend with
dashboards or alerts, a deployment pipeline. Retrieval has been measured on the 27-passage fixture and, with draft labels that
still need the owner's review, on one private repository whose numbers are not published here.
Assumptions and deferred decisions are tracked in [docs/open-questions.md](docs/open-questions.md).
What is left before v0.1, and when Azure comes in, is in [docs/milestones.md](docs/milestones.md).
The six tasks of the first sprint (handoff Section 15) are done. The P0 follow-ups (durable jobs, model providers, GitHub sign-in, deployment artifacts) are built; what remains is trying them for real with your keys, GitHub App and Azure subscription.

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
dotnet run --project src/Trace.Worker                        # optional locally, see below

cd apps/web
npm ci
npm run dev                                                  # http://localhost:5173
```

Open http://localhost:5173, type a question about the demo repository and press Ask.

In the Development environment the API applies pending migrations, seeds the public demo
repository (`trace-demo/sample-repo`) and indexes the fixture directory on startup, using the
fake embedding provider. Demo seeding and the fake models are refused in Production.

In Development the API also runs background jobs itself (`Jobs:ProcessInThisHost`), so the
worker is optional. To run them the production way, start the API with
`Jobs__ProcessInThisHost=false` and run the worker; any number of workers can share the queue.

Ask the fixture a question (bash, with `jq`):

```sh
repo=$(curl -s localhost:5120/api/v1/repositories | jq '.[0]')
curl -s localhost:5120/api/v1/investigations -H 'Content-Type: application/json' -d "$(jq -n \
  --argjson r "$repo" '{repositoryId: $r.id, snapshotId: $r.latestSnapshot.id, type: "question",
  question: "Which requests are let through without an API key?"}')" | jq
```

The response is `202` with a queued investigation; fetch `/api/v1/investigations/<investigationId>`
until its `state` is `completed`.

List the demo repository's issues and fetch one (they are invented, read from
`tests/fixtures/sample-repo.issues/`; no GitHub call is made):

```sh
id=$(curl -s localhost:5120/api/v1/repositories | jq -r '.[0].id')
curl -s "localhost:5120/api/v1/repositories/$id/issues?limit=3" | jq     # { items, nextCursor }
curl -s "localhost:5120/api/v1/repositories/$id/issues/3" | jq           # body, comments, truncation flags
```

The answer comes from the extractive fake model: it lists matching passages as evidence and
says so; it is not a model-written explanation.

The web dev server proxies `/api` and `/health` to the API (`TRACE_API_URL`, default
`http://localhost:5120`).

## Using the real models

Set these for the API and the worker (environment variables shown; never commit keys):

```sh
export Chat__Provider=anthropic        ANTHROPIC_API_KEY=...   # optional: Chat__Model, Chat__Effort
export Embeddings__Provider=voyage     VOYAGE_API_KEY=...
```

Snapshots record the embedding model they were built with, so after switching embedding
provider, index again (the Index again button, or `POST /api/v1/repositories/<id>/index-jobs`) before asking
questions. With real providers, indexed source text is sent to Voyage and retrieved passages to
Anthropic.

What it costs: on one small repository a question measured about $0.12-$0.14 with
`claude-opus-5-5` (two questions, one run each; see row 64 of
[docs/open-questions.md](docs/open-questions.md)). Each result shows the tokens it used.

## Evaluation

Run the fixture's labeled questions through the investigation pipeline with the fake models
(needs the database from Local setup; costs nothing):

```sh
dotnet run --project tools/Trace.Evals -- run --dataset tests/fixtures/sample-repo.questions.json --source tests/fixtures/sample-repo
```

It indexes the fixture into a database of its own, which it drops afterwards (your development
data is not touched), and writes `eval/results/sample-repo-<time>-<run id>.json` (git-ignored).
Then compute its metrics, or compare two runs:

```sh
dotnet run --project tools/Trace.Evals -- report eval/results/<result file>.json
dotnet run --project tools/Trace.Evals -- compare eval/results/<baseline>.json eval/results/<candidate>.json
```

`report` prints retrieval recall at 10, citation validity, abstention precision and recall,
cost and time, for all questions and for each split. `compare` lists every question with what
changed, regressions first. Both also write Markdown and JSON next to the result file.

CI runs a fixed smoke subset of six questions the same way and fails the build if the result
does not meet the expectations in `tests/fixtures/sample-repo.smoke.json`. To do the same
locally (`check` exits 3 when an expectation is not met):

```sh
dotnet run --project tools/Trace.Evals -- run --dataset tests/fixtures/sample-repo.questions.json --source tests/fixtures/sample-repo --expectations tests/fixtures/sample-repo.smoke.json
dotnet run --project tools/Trace.Evals -- check eval/results/<result file>.json --expectations tests/fixtures/sample-repo.smoke.json
```

The dataset format, the result file, the definition of every metric and every option are in
[docs/evaluation.md](docs/evaluation.md). With the fake models the numbers show that the
plumbing works, not that answers or retrieval are good: on the fixture's 34 questions, recall
at 10 is 30 of 31 labeled locations with the bag-of-words fake embeddings, and the fake chat
model answers all 7 questions that cannot be answered, because it does not read.

With the real models (one run, 2026-11-23): 31 of 31 labeled locations in the top 10, 130 of
130 citations valid, 6 of the 7 unanswerable questions declined, and an estimated $1.62 for
the 34 questions (median $0.048 and 10.9 seconds each). The prices behind that estimate are
not confirmed, and the answers were read by hand rather than scored. The full record and its
limits are in
[docs/eval/2026-11-23-fixture-baseline.md](docs/eval/2026-11-23-fixture-baseline.md);
[docs/evaluation.md](docs/evaluation.md#running-with-the-real-models) says how to run it
again, which costs money.

Plans are measured with two more commands:

```sh
dotnet run --project tools/Trace.Evals -- plan-run --labels tests/fixtures/sample-repo.plan-labels.json --issues tests/fixtures/sample-repo.issues --source tests/fixtures/sample-repo
dotnet run --project tools/Trace.Evals -- plan-report eval/results/<plan result file>.json [--ratings <ratings file>]
```

`plan-run` plans the fixture's eight labeled issues through the production pipeline (with the
fake model by default, which costs nothing). `plan-report` prints the rubric score twice:
from the computed checks alone, and with a person's ratings once a ratings file is given;
without one it says "needs a human" rather than guessing. It also writes a rating sheet and
an empty ratings file next to the result. With the real models (one run, 2026-11-24): 73.2%
with the agent's own ratings against a target of 80%, two of eight runs failed, an estimated
$0.76 for the eight plans. The record, its limits and the ratings are in
[docs/eval/2026-11-24-fixture-plan-baseline.md](docs/eval/2026-11-24-fixture-plan-baseline.md);
the rubric is in [docs/evaluation.md](docs/evaluation.md#plan-evaluation).

To measure a repository of your own that the application has indexed, without committing
anything about it:

```sh
dotnet run --project tools/Trace.Evals -- run --dataset eval/local/<name>/<name>.questions.json --snapshot <snapshot id>
```

The dataset, the result files and the reports stay under the git-ignored `eval/local/`.
[docs/evaluation.md](docs/evaluation.md#a-private-repository) has the whole workflow: finding
the snapshot ID, writing the questions, running, comparing, and checking that nothing leaks
into a commit.

## Telemetry

Every response carries an `X-Correlation-ID` header (send your own, or one is generated). The
same value is on every log line of the request and of the job it queued, and the web app shows
it next to an error as "Reference".

Traces and metrics stay inside the process unless you name a collector:

```sh
export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317   # for the API and the worker
```

Spans carry IDs, counts, durations and outcome names only: never questions, source text,
model output or keys.

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
npm test               # component tests (no servers needed)

# Browser tests. Need the database running; they start the API and the web app themselves.
npx playwright install chromium   # once
npm run test:e2e
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
tests/fixtures/sample-repo.issues  Invented issues for it (not indexed)
tools/Trace.Evals/         Evaluation runner (command line; not deployed)
infra/                     Local Docker Compose
docs/                      Handoff, ADRs, evaluation, open questions
```
