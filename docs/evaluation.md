# Evaluation

How TRACE's answers are measured: a dataset of labeled questions, a runner that puts every
question through the real investigation pipeline, and one result file per run.

This page covers the dataset format, the runner, the result file, the
[metrics](#metrics) computed from a result file and [comparing two runs](#compare-two-runs).

**Status:** everything here has only ever been run with the fake providers on the fixture
repository. It has not been run against Claude or Voyage. With the fake chat model the
outcomes say that the plumbing works, not that answers are good: that model only points at
passages sharing words with the question. With the fake embeddings recall says nothing about
retrieval with a real embedding model.

Three commands:

| Command | What it does | Needs |
|---|---|---|
| `run` | Asks a dataset's questions and writes a result file | The database |
| `report` | Prints the metrics of one result file | Nothing but the file |
| `compare` | Prints what changed between two result files | Nothing but the files |

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
- After each investigation the runner makes one search of its own to measure retrieval
  ([Retrieval measurement](#retrieval-measurement)). It is the only thing the runner does
  that the product does not.

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
| `questions[].expected` | if answerable | The **relevant locations**: source a correct answer must rest on, each a `path` (repository-relative, forward slashes, spelled exactly as in the repository), `startLine`, `endLine` (1-based, inclusive). Label the smallest range that settles the question, and every place a correct answer needs, not every place that mentions the topic. Recall at 10 and "cites a labeled location" are measured against these. |
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

One JSON file per run, `schemaVersion` 2. Version 2 added each question's `retrieval`;
`report` and `compare` still read version 1 files, for which recall is not measured. Result files quote the evaluated source (evidence
excerpts and search queries), which is why they are written to a git-ignored directory by
default. Put results for a private repository under `eval/local/`.

Per run:

| Field | Meaning |
|---|---|
| `schemaVersion` | 2. Changes with any change to this format. |
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
| `retrieval` | What the search returned for the question itself ([Retrieval measurement](#retrieval-measurement)): `limit` (10), `hits` in rank order (`rank` from 1, `path`, `startLine`, `endLine`, and `lexicalRank` and `vectorRank`, the hit's rank in each of the two searches alone or `null`), and `errorCode` (`null`, or why the search could not be made: `invalid_argument`, `embedding_model_mismatch`, `snapshot_not_found`, `snapshot_not_ready`, `timed_out`, `retrieval_failed`). The whole field is `null` when retrieval was not measured. No source text. |
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

## Retrieval measurement

Recall at 10 needs to know what the search returned, so for every question the pipeline
accepted the runner also asks the production search (`search_code`, the same code the model's
tool calls reach) for **the question itself, word for word, with a limit of 10**, and records
where the hits are ([ADR-010](adr/ADR-010-evaluation-metrics.md)).

- It is a measurement of the retriever (chunking, full-text search, embeddings, rank fusion),
  not of the model. It is made after the investigation, is never shown to the model, and its
  time is not part of `durationMs`.
- It is **not** what the model searched for. During an investigation the model writes its own
  queries, and those tool calls are still recorded with result counts only.
- Only locations are recorded (path, lines, ranks), no source text: overlap with a label needs
  nothing else.
- It is made only after the pipeline has accepted the question, because that is where the
  repository's visibility and the snapshot are checked. A question the pipeline refused, a
  question the run was stopped on, and every question of a version 1 file have no retrieval.
- A search that could not be made (for example a question longer than the 1,000 characters
  the search accepts) is recorded with an `errorCode` and no hits. That counts as "not
  measured", never as a miss.
- With a real embedding provider this is one more query embedding per question.

## Metrics

`report` computes these from one result file and nothing else. Each is given for all
questions, for the development split (`dev`) and for the held-out split (`test`).

A metric with nothing to measure is `n/a` (`null` in JSON), never 0% or 100%.

Three words used below:

- A question **ran to an end** if its outcome is `answered`, `insufficient_evidence` or
  `failed`. `cancelled` and `not_run` questions were stopped from outside.
- A **relevant location** is one entry of the question's `expected` list in the dataset: a
  file and a line range that a correct answer must rest on, written by a person reading the
  source.
- A passage or citation **overlaps** a relevant location if it is in the same file (paths
  compared exactly, as indexed) and shares at least one line with it. Sharing one line is
  enough because passages are cut by the chunker, not by the labeler; it also means a large
  passage finds a small label easily, so recall depends on the chunk size (40 lines today).

| Metric | Definition |
|---|---|
| **Retrieval recall at 10** | Relevant locations overlapped by one of the 10 best hits of the question's retrieval, divided by all relevant locations, summed over the questions that have relevant locations and a recorded retrieval. Every location counts once, so a question with three labels weighs three times one with a single label. The report also gives how many questions had every location found and how many answerable questions were not measured. |
| **Citation validity** | Citations that became evidence, divided by all citations the model made (`evidence` count plus `rejectedCitations`), over every question. A citation is valid only if the pipeline accepted it: the range exists in the snapshot, its lines were returned to the model during the same investigation, and it is not only blank lines. This is stricter than the handoff's "resolves to the indexed snapshot". It does not say the cited lines support the claim. |
| **Abstention precision** | Correct abstentions divided by all abstentions. An abstention is a question that ended as `insufficient_evidence`. It is **correct** when the dataset marks the question `"answerable": false`. |
| **Abstention recall** | Correct abstentions divided by the unanswerable questions that ran to an end. An unanswerable question that was answered, or that failed, was owed an abstention and did not get one. |
| **Answers citing a labeled location** | Answerable questions that ran to an end and have at least one piece of evidence overlapping a relevant location, divided by answerable questions that ran to an end. Not a handoff metric: it is the nearest thing to "the answer rests on the right place" that can be computed without a judge. It says nothing about whether the answer's text is right. |
| **Cost** | Over questions that ran to an end and have an `estimatedCostUsd`: the total, the median and the 95th percentile per question. An estimate from token counts and configured prices, not a bill. |
| **Time** | Over questions that ran to an end: the median and the 95th percentile of `durationMs`. A failure counts with the time it took, a timeout included. |

The median of an even number of values is the mean of the two middle ones. The 95th
percentile is the nearest rank: the smallest value that at least 95% of the values do not
exceed. With fewer than 20 questions that is the largest value.

Known limits of these definitions:

- Citation validity can read slightly low. When the model says the evidence is insufficient
  but attaches citations anyway, the pipeline counts the ones it discards and does not record
  the ones that would have passed.
- Recall at 10 says nothing about the queries the model writes, and a hit counts whether or
  not its text was ever shown to the model.
- There is no measure of whether an answer's text is correct (the handoff's "factual
  grounding"). That needs a person or a judge model.
- The fixture has no unanswerable questions yet, so both abstention numbers are `n/a` for it
  (step M1.3 adds some).

## Report

```sh
dotnet run --project tools/Trace.Evals -- report eval/results/<result file>.json
```

Prints a Markdown report and writes it, with the same content as JSON, next to the result
file: `<result file name>.report.md` and `.report.json`. `--output <directory>` puts them
somewhere else. It needs no database and no provider.

The report has the run's versions, notes about what limits the numbers (fake providers, a
stopped run, a revision mismatch, missing prices), the table of metrics, and one row per
question: outcome, recall, whether its evidence overlaps a label, valid citations, the files
of its evidence, cost and time. It contains question IDs and file paths but never a question,
an answer or source text. File paths of a private repository are still private: keep its
reports under `eval/local/` with its results.

The JSON (`reportVersion` 1) has `run`, `notes`, `metrics` (three objects with `scope`
`all`, `dev`, `test`, each with `questions`, `outcomes`, `recallAt10`, `citationValidity`,
`abstention`, `labeledEvidenceCited`, `cost`, `duration`) and `questions` (the per-question
values the metrics were computed from). A test asserts these fields (`EvalMetricsTests`).

Exit code 0 when the report was written, 1 when the arguments are wrong or the file is not a
result file this version reads (`schemaVersion` 1 or 2).

## Compare two runs

```sh
dotnet run --project tools/Trace.Evals -- compare eval/results/<baseline>.json eval/results/<candidate>.json
```

Prints what changed, question by question, and writes `compare-<baseline run>-<candidate
run>.md` and `.json` next to the candidate file (or into `--output <directory>`). Questions
are matched by `id`.

Each question gets one status, and they are listed in this order:

| Status | Meaning |
|---|---|
| `regressed` | At least one of the four things below got worse, whatever else changed. |
| `improved` | At least one got better and none got worse. |
| `changed` | Something else differs: another failure reason, other evidence files, recall measured in only one run. |
| `only_in_baseline`, `only_in_candidate` | The question is in one file only. |
| `unchanged` | None of the above. |

The four things that can get worse or better:

1. **Outcome.** The outcome the label calls for is `answered` for an answerable question and
   `insufficient_evidence` for an unanswerable one. Losing it is a regression, gaining it an
   improvement. A change between two other outcomes is only `changed`.
2. **Recall at 10** for the question: found locations divided by labeled ones, when both runs
   measured it.
3. **Citing a labeled location**: from yes to no is a regression.
4. **Valid citations** as a share of the citations made, when the model cited something in
   both runs.

Cost and time are shown for both runs and as a difference, but never decide a status: time
varies between identical runs, and whether a cost change is worth it is a judgment. A
question listed as `unchanged` can therefore have a different cost and time.

The comparison starts with both runs' versions side by side (anything that differs is
marked), then warnings when the runs are not like for like: different dataset or dataset
version, different split, different source revision, a stopped run, a version 1 file, or a
question whose text or labels differ between the files. It also has both runs' full metric
tables. The JSON is `comparisonVersion` 1 (`EvalComparisonTests` asserts its fields).

Exit code 0 even when questions regressed: a regression is a finding, not a failure of the
tool. Nothing fails a build on a regression yet.

## What is not there yet

- **No baseline.** The numbers have only been computed for runs with the fake models, where
  they show that the plumbing works. A baseline is a real-model run (step M1.5).
- **Never run with the real models.** The cost estimate has only been checked against
  hand-computed numbers in tests. Embedding tokens are not recorded or priced, and with a real
  embedding provider every run indexes the source again, which costs money each time.
- **No record of what the model's own searches returned**: tool calls carry result counts
  only.
- **No CI gate.** Nothing runs the evaluation or fails on a regression in CI (step M1.4).
- **Local directories only.** A repository indexed from GitHub cannot be evaluated yet; check
  it out and pass the directory.
- **No history table.** The handoff's `eval_runs` table does not exist; a run is a file.
