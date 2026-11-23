# Evaluation

How TRACE's answers are measured: a dataset of labeled questions, a runner that puts every
question through the real investigation pipeline, and one result file per run.

This page covers the dataset format, the runner and the result file. The metrics computed
from a result file, and comparing two runs, are not built yet (plan step M1.2).

**Status:** the runner has only ever been run with the fake providers on the fixture
repository. It has not been run against Claude or Voyage. With the fake chat model the
outcomes say that the plumbing works, not that answers are good: that model only points at
passages sharing words with the question.

## Run it

From the repository root, with the local database running
(`docker compose --env-file .env -f infra/docker-compose.yml up -d`):

```sh
dotnet run --project tools/Trace.Evals -- run --dataset tests/fixtures/sample-repo.questions.json --source tests/fixtures/sample-repo
```

This indexes the fixture into a new database, asks its 11 questions with the fake providers,
writes `eval/results/sample-repo-<UTC time>-<run id>.json` and drops the database. It costs
nothing and takes a few seconds.

| Option | Meaning |
|---|---|
| `--dataset <file>` | The labeled questions (format below). Required. |
| `--source <directory>` | The repository to index, as a local directory. Required. |
| `--output <directory>` | Where the result file goes. Default `eval/results/` in the repository root, which is git-ignored. |
| `--split dev\|test` | Run only one split. Default: every question. |
| `--allow-revision-mismatch` | Run even though the source is not the revision the dataset names (see below). |
| `--keep-database` | Leave the run's database in place to look at afterwards. |

Exit code 0 means the run finished, **whatever the questions' outcomes were**: a failed
question is a measurement, not an error of the tool. 1 means the run could not start or
complete (bad arguments, invalid dataset, database unreachable, revision mismatch). 2 means it
was stopped early (Ctrl+C); the result file is still written.

The output lists each question by ID with its outcome, counts and time. It never prints a
question, an answer or source text.

### Settings

Everything else comes from `tools/Trace.Evals/evalsettings.json`, overridden by environment
variables (`Section__Key`):

| Setting | Default | Meaning |
|---|---|---|
| `ConnectionStrings:Trace` | the local compose database | The PostgreSQL **server** to create the run's database on (see below). |
| `Chat:Provider`, `Embeddings:Provider` | `deterministic-fake` | The same settings the API uses. The fakes are the default so that a run costs nothing unless you ask for the real models. |
| `Chat:MaxTokensPerInvestigation`, `Chat:DailyTokenBudget` | 300,000 and 3,000,000 | The production spending limits. They apply to a run too: the daily budget counts every question of the run. |
| `Eval:QuestionTimeoutSeconds` | 600 | A question still running after this long is stopped and recorded as failed with `timed_out`. |
| `Eval:Prices:<model id>` | only `fake-extractive-v1`, at 0 | US dollars per million tokens: `InputPerMillion`, `OutputPerMillion`, `CacheReadPerMillion`, `CacheWritePerMillion`. |

Prices are configuration, never code. If the chat model has no entry, the run prints a
warning and records every cost as `null` rather than guessing; an entry with a missing or
invalid number stops the run. No price for a real model is shipped: before a real run, add
an entry with all four values for the model you use, taken from the provider's current price
list, under `Eval:Prices` in `evalsettings.json` (prices are not secrets).

### The run's database

Each run creates a database named `trace_eval_<random>` on the server that
`ConnectionStrings:Trace` points at, applies the migrations, indexes the source directory
into it, runs the questions, and drops it at the end (also after an error or Ctrl+C).

The database named in the connection string, by default your development database `trace`,
is used only as the place to connect in order to issue `CREATE DATABASE` and `DROP DATABASE`.
Nothing in it is read or written, and only a database the run created itself is ever dropped.
The database user must be allowed to create databases (the compose user is).

If the process is killed outright, the database is left behind. List and remove leftovers with:

```sh
docker exec trace-postgres-1 psql -U trace -d trace -c "SELECT datname FROM pg_database WHERE datname LIKE 'trace_eval_%'"
docker exec trace-postgres-1 psql -U trace -d trace -c "DROP DATABASE <name> WITH (FORCE)"
```

### What a run does with a question

The runner has no investigation logic of its own
([ADR-009](adr/ADR-009-evaluation-runner.md)). For each question it calls the same
`QuestionInvestigator` the API and worker use, built from the same service registration: the
request is validated and stored, then run as one final job attempt would run it. The same
prompt, the same two read-only tools, the same limits and the same citation checks apply, and
the repository and snapshot are fixed by the run, never by the question or the model. The
runner then reads back what the pipeline stored.

- One attempt per question, no retry. A model outage is recorded as `failed` with
  `model_error` at once (the worker would retry up to three times).
- Questions run one after another.
- Whatever one question does, the run goes on to the next.

## Dataset format

A dataset is one JSON file of questions about one repository at one revision. The format
extends `tests/fixtures/sample-repo.questions.json`, which is a valid dataset. Unknown
properties are an error, so a misspelt label cannot be silently ignored.

```json
{
  "name": "sample-repo",
  "datasetVersion": "1",
  "description": "Free text.",
  "revision": "ee02b2da366aa000b30541463014b895d5cebb67",
  "questions": [
    {
      "id": "pagination-offset",
      "split": "dev",
      "question": "Why does the first page of the task list skip the first results?",
      "expected": [{ "path": "src/TaskBoard.Api/Tasks/TaskRepository.cs", "startLine": 20, "endLine": 29 }]
    },
    {
      "id": "deployment-region",
      "split": "test",
      "question": "Which cloud region is the service deployed to?",
      "answerable": false,
      "notes": "Nothing in the repository says where it runs."
    }
  ]
}
```

| Field | Required | Meaning |
|---|---|---|
| `name` | no | The dataset's name: 1-64 characters of `a-z 0-9 . _ -`. Defaults to the file name without `.questions.json`. It starts the result file's name. **Added by this format.** |
| `datasetVersion` | yes | Change it whenever a question or label changes, so results are compared like for like. |
| `description` | no | Free text. |
| `revision` | no | The revision the labels were written against: 40 lowercase hex characters. `fixtureRevision` is accepted as the older name for the same thing. **`revision` added by this format.** |
| `questions[].id` | yes | Unique in the file; 1-100 characters of `a-z 0-9 . _ -`. It is what two runs are compared by. |
| `questions[].split` | yes | `dev` (may be looked at while tuning) or `test` (held out; never tune on it). |
| `questions[].question` | yes | The question, exactly as it is asked. At most 4,000 characters. |
| `questions[].expected` | if answerable | Source locations a correct answer must rest on: `path` (repository-relative, forward slashes), `startLine`, `endLine` (1-based, inclusive). |
| `questions[].answerable` | no | Default `true`. `false` marks a question the repository cannot answer: the right result is "not enough evidence", and `expected` must be absent or empty. **Added by this format.** |
| `questions[].notes` | no | Free text for whoever maintains the labels. **Added by this format.** |

Rules for writing one:

- Write the question and its labels from reading the source, before running retrieval or the
  runner on it. Do not edit a label after seeing a result, except to fix a labeling mistake.
- Keep the file **outside** the directory that gets indexed, so the labels are never
  retrieved as evidence.
- A dataset about a private repository goes under `eval/local/` (git-ignored), and so does
  anything else naming it. This repository is public.

### The revision check

For a local directory the snapshot's revision is a content-derived ID over the indexed files
(open question 16), not a Git commit. If the dataset states a `revision` and the indexed
source has a different one, the run stops before asking anything: line labels would point at
the wrong lines. `--allow-revision-mismatch` runs anyway and records the mismatch in the file.
A dataset without a `revision` is never checked.

## Result file

One JSON file per run, `schemaVersion` 1. Result files quote the evaluated source (evidence
excerpts and search queries), which is why they are written to a git-ignored directory by
default. Put results for a private repository under `eval/local/`.

Per run:

| Field | Meaning |
|---|---|
| `schemaVersion` | 1. Changes with any change to this format. |
| `runId` | A random ID for this run. |
| `status` | `completed`, or `cancelled` if the run was stopped early. |
| `traceCommit`, `traceCommitDirty` | The Git commit of TRACE that ran the questions, and whether the working tree had uncommitted changes. `unknown` and `null` if Git could not be asked. |
| `dataset` | `name`, `version`, `revision`, `revisionMatchesSnapshot` (`null` if the dataset states no revision), `split` (`all`, `dev` or `test`), `questionCount`. |
| `snapshot` | `commitSha` (the revision every question was pinned to), `branch`, `chunkerVersion`, `embeddingProvider`, `embeddingModel`: the index version. |
| `chat` | `provider`, `model`, `promptVersion`. |
| `prices` | The prices used for the estimates (`currency`, the four per-million values), or `null`. |
| `startedAt`, `endedAt` | UTC, ISO 8601. |
| `totals` | Counts per outcome, summed tokens and summed estimated cost. Counts only: quality metrics are computed from the file, not stored in it. |
| `questions` | One entry per question, in dataset order. |

Per question:

| Field | Meaning |
|---|---|
| `id`, `split`, `question`, `answerable`, `expected` | Copied from the dataset, so the file can be scored on its own. |
| `outcome` | `answered`, `insufficient_evidence`, `failed`, `cancelled`, or `not_run` (the run was stopped before this question started). |
| `errorCode` | Why a `failed` question failed, as the pipeline recorded it: `model_error`, `model_refused`, `invalid_model_output`, `budget_exhausted`, `timed_out`, `internal_error`; or `request_rejected` if the pipeline refused the request before running anything. Otherwise `null`. |
| `investigationId` | The investigation in the run's database (useful with `--keep-database`); `null` if none was created. |
| `answer` | The stored answer. For `insufficient_evidence` it is the pipeline's fixed sentence, never the model's text. `null` unless the investigation completed. |
| `evidence` | The citations that passed validation: `path`, `startLine`, `endLine`, `supports`, `excerpt`. Each exists in the snapshot and was retrieved during the same investigation. |
| `unknowns`, `limitations` | As stored on the result. |
| `toolCalls` | Every tool request in order: `seq`, `tool`, `status` (`called`, `failed`, or `rejected` when it was refused without running), `resultCount` (search hits or lines returned), `latencyMs`, `outputChars`, `errorCode`, `summary`. `tool` is `null` when the model asked for a tool that does not exist. |
| `rejectedCitations` | How many citations the model claimed that validation discarded. |
| `tokens` | `input` (all input tokens, including both cached kinds), `output`, `cacheRead`, `cacheWrite`. |
| `estimatedCostUsd` | From `tokens` and `prices`: cached tokens at their own rates, the rest of the input at the input rate. `null` when no prices are configured. An estimate, not a bill. |
| `durationMs` | Wall-clock time from creating the investigation to its final state. |

A test asserts this schema field by field (`EvalResultTests`), and another asserts it on a
file written by a real run (`EvalRunnerTests`).

## What is not there yet

- **No metrics and no comparison of runs** (step M1.2). Recall at 10 needs the locations each
  search returned; the file records only how many results each tool call returned, so that
  step will have to add them or measure retrieval separately.
- **Never run with the real models.** The cost estimate has only been checked against
  hand-computed numbers in tests. Embedding tokens are not recorded or priced, and with a real
  embedding provider every run indexes the source again, which costs money each time.
- **Local directories only.** A repository indexed from GitHub cannot be evaluated yet; check
  it out and pass the directory.
- **No history table.** The handoff's `eval_runs` table does not exist; a run is a file.
