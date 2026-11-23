# Evaluation

How TRACE's answers are measured: a dataset of labeled questions, a runner that puts every
question through the real investigation pipeline, and one result file per run.

This page covers the dataset format, [how questions are added](#adding-questions), the
[fixture's question set](#the-fixtures-question-set), the runner, the result file, the
[metrics](#metrics) computed from a result file, [comparing two runs](#compare-two-runs) and
[the smoke check](#the-smoke-check) that CI runs.

**Status:** everything here has only ever been run with the fake providers on the fixture
repository. It has not been run against Claude or Voyage. With the fake chat model the
outcomes say that the plumbing works, not that answers are good: that model only points at
passages sharing words with the question. With the fake embeddings recall says nothing about
retrieval with a real embedding model.

Four commands:

| Command | What it does | Needs |
|---|---|---|
| `run` | Asks a dataset's questions and writes a result file | The database |
| `report` | Prints the metrics of one result file | Nothing but the file |
| `compare` | Prints what changed between two result files | Nothing but the files |
| `check` | Fails when a result file does not meet an expectations file | Nothing but the files |

## Run it

From the repository root, with the local database running
(`docker compose --env-file .env -f infra/docker-compose.yml up -d`):

```sh
dotnet run --project tools/Trace.Evals -- run --dataset tests/fixtures/sample-repo.questions.json --source tests/fixtures/sample-repo
```

This indexes the fixture into a new database, asks its 34 questions with the fake providers,
writes `eval/results/sample-repo-<UTC time>-<run id>.json` and drops the database. It costs
nothing and takes a few seconds.

| Option | Meaning |
|---|---|
| `--dataset <file>` | The labeled questions (format below). Required. |
| `--source <directory>` | The repository to index, as a local directory. Required. |
| `--output <directory>` | Where the result file goes. Default `eval/results/` in the repository root, which is git-ignored. |
| `--split dev\|test` | Run only one split. Default: every question. |
| `--expectations <file>` | Run only the questions an expectations file lists ([the smoke check](#the-smoke-check)). Cannot be combined with `--split`. |
| `--allow-revision-mismatch` | Run even though the source is not the revision the dataset names (see below). |
| `--keep-database` | Leave the run's database in place to look at afterwards. |

Exit code 0 means the run finished, **whatever the questions' outcomes were**: a failed
question is a measurement, not an error of the tool. 1 means the run could not start or
complete (bad arguments, invalid dataset or expectations file, database unreachable, revision mismatch). 2 means it
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
  "datasetVersion": "2",
  "description": "Free text.",
  "revision": "4a07032f452579445ba5be2a05d574028b69754d",
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

Where a dataset lives:

- Keep the file **outside** the directory that gets indexed, so the labels are never
  retrieved as evidence.
- A dataset about a private repository goes under `eval/local/` (git-ignored), and so does
  anything else naming it. This repository is public.

### Adding questions

The order matters more than anything else here: a label written after looking at what the
search returns measures the labeler's agreement with the search, not the search.

1. **Read the source first.** If the repository cannot support the question you want to ask,
   and it is the fixture, add realistic invented content to it before writing the question
   (see [changing the fixture](#changing-the-fixture)).
2. **Write the question, its labels and its split** into the dataset file:
   - Ask it the way a person who has not read the code would. Avoid copying identifiers or
     whole phrases from the lines you are about to label: a question that shares its label's
     wording is found by word matching alone.
   - Label the smallest range that settles the question, and every place a correct answer
     needs, not every place that mentions the topic. Take line numbers from the file as
     checked in.
   - An unanswerable question (`"answerable": false`) asks for a fact the repository does not
     hold, and that someone could plausibly expect it to hold. Search the source for the
     terms a real answer would use and make sure nothing settles it. Say in `notes` why it
     cannot be answered. Avoid questions whose honest answer is "the code shows there is no
     such thing": whether that is an answer or an abstention is a judgment.
   - Choose the split now. `dev` questions may be looked at while tuning search, chunking or
     prompts. `test` questions are held out: never tune on them, and never move a question
     between splits after seeing a result for it.
   - A new question must not repeat an existing one in other words.
3. **Change `datasetVersion`.** Results are only compared like for like within one version.
4. **Only now run** the search, the runner or the tests that use the labels.
5. **After that, leave the question alone.** Do not change its wording, labels or split
   because of a result. The one exception is a labeling mistake, such as a wrong line number:
   fix it, change `datasetVersion` again, and say in the commit message which label changed
   and why.
6. For the fixture, update the counts that tests pin (`EvalDatasetTests`, `EvalRunnerTests`,
   `RetrievalTests`) and write the reason next to them. A retrieval floor is a measured
   baseline: restate it for the new set, never lower it to hide a regression.

`EvalDatasetTests` checks the fixture's set on every build: at least 25 questions, at least
5 unanswerable and at least 8 held-out, unique IDs and wordings, both kinds of question in
both splits, and every labeled path and line range present in the fixture.

### Changing the fixture

`tests/fixtures/sample-repo` is synthetic. Everything in it is invented for TRACE; nothing may
be copied from a real or private project, and a sample key must be built from obviously fake
pieces.

Add content rather than editing it: a line inserted above a labeled range moves the range.
Any change to an indexed file changes the fixture's content-derived revision, which is pinned
in the dataset (`fixtureRevision`), in `FixtureIndexingTests` and `EvalDatasetTests`, in the
example above and in the browser tests (`apps/web/e2e/ask.spec.ts`, which run against the
indexed fixture). `FixtureIndexingTests` also lists every indexed file, and
`CitationValidationTests` pins the length of the fixture's README.

### The fixture's question set

`tests/fixtures/sample-repo.questions.json`, version 2
([ADR-011](adr/ADR-011-fixture-question-set.md)):

| | All | `dev` | `test` (held out) |
|---|---|---|---|
| Questions | 34 | 15 | 19 |
| Answerable | 27 | 12 | 15 |
| Unanswerable | 7 | 3 | 4 |
| Labeled locations | 31 | 15 | 16 |

The fixture is 21 files and 27 passages. Version 1 had 11 questions, all answerable; they are
unchanged in version 2. The 13 new `test` questions have never been used to choose anything.
The 6 older ones were looked at once ([open question 25](open-questions.md)).

With the fake providers (2026-11-23, dataset version 2):

| Metric | All | `dev` | `test` |
|---|---|---|---|
| Retrieval recall at 10 | 96.8% (30 of 31) | 100.0% (15 of 15) | 93.8% (15 of 16) |
| Citation validity | 100.0% (102 of 102) | 100.0% (45 of 45) | 100.0% (57 of 57) |
| Abstention precision | n/a (0 abstentions) | n/a | n/a |
| Abstention recall | 0.0% (0 of 7) | 0.0% (0 of 3) | 0.0% (0 of 4) |
| Answers citing a labeled location | 81.5% (22 of 27) | 91.7% (11 of 12) | 73.3% (11 of 15) |

**These are fake-model numbers.** The fake chat model does not read: it cites the first
passages its one search returns and abstains only when the search returns nothing, which is
why it answered all 7 unanswerable questions. The fake embeddings are a bag of words. The
table shows that the set can be run and scored, and gives the regression tests their floors.
It is not a baseline; that needs the real models (step M1.5).

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
| `dataset` | `name`, `version`, `revision`, `revisionMatchesSnapshot` (`null` if the dataset states no revision), `split` (`all`, `dev` or `test`, or `subset` when the run was limited by `--expectations`), `questionCount`. |
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
- With the fake chat model the abstention numbers say nothing about a real model: the fake
  abstains only when its search returns nothing
  ([the fixture's question set](#the-fixtures-question-set)).

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
tool. `compare` does not gate a build and CI does not run it; the smoke check below does
([ADR-012](adr/ADR-012-evaluation-smoke-check.md) says why).

## The smoke check

CI runs six fixture questions through the pipeline with the fake providers on every push and
pull request, and fails the build when the result is not what is written down
([ADR-012](adr/ADR-012-evaluation-smoke-check.md)). The same three commands work locally:

```sh
dotnet run --project tools/Trace.Evals -- run --dataset tests/fixtures/sample-repo.questions.json --source tests/fixtures/sample-repo --expectations tests/fixtures/sample-repo.smoke.json
dotnet run --project tools/Trace.Evals -- report eval/results/<result file>.json
dotnet run --project tools/Trace.Evals -- check eval/results/<result file>.json --expectations tests/fixtures/sample-repo.smoke.json
```

`run --expectations` asks only the questions the file lists. `check` reads the result file
and the expectations file, writes nothing, and prints either

```text
Check passed: 6 question(s) of 'sample-repo' meet their expectations.
```

or every expectation that was not met, by question ID, for example:

```text
Check FAILED: 2 expectation(s) not met for the 6 question(s) of 'sample-repo'.
  - pagination-offset: expected the outcome 'answered' but it was 'failed (model_error)'.
  - pagination-offset: no validated evidence overlaps a labeled location.
```

Exit code 0 when every expectation is met, **3 when at least one is not**, 1 when the
arguments are wrong or a file cannot be used. The output never contains a question, an answer
or source text.

### The expectations file

```json
{
  "dataset": "sample-repo",
  "description": "Free text.",
  "questions": [
    { "id": "pagination-offset", "outcome": "answered", "labelsRetrieved": true, "citesLabel": true },
    { "id": "on-call-contact", "notes": "Free text." }
  ]
}
```

| Field | Required | Meaning |
|---|---|---|
| `dataset` | yes | The name of the dataset the questions belong to. A dataset or result file with another name is refused. |
| `description` | no | Free text. |
| `questions[].id` | yes | A question of that dataset. It must be in the `dev` split. |
| `questions[].outcome` | no | The outcome the question must end with: `answered`, `insufficient_evidence` or `failed`. Left out, the question must end as an answer or an abstention, whichever, and not fail. |
| `questions[].labelsRetrieved` | no | `true`: every labeled location must be among the 10 best hits of the question's [retrieval](#retrieval-measurement). |
| `questions[].citesLabel` | no | `true`: the validated evidence must overlap a labeled location. |
| `questions[].notes` | no | Free text. |

Unknown properties are an error, so a misspelt expectation cannot be silently ignored.

Checked for every listed question whatever the file says: it is in the result file, and none
of its citations was discarded by validation. Checked for the run: it completed, it is a run
of the named dataset, and the source was the revision the labels were written against.
Questions in the result file that the expectations do not list are not looked at.

Rules:

- **Development questions only.** `run --expectations` and `check` both refuse a question of
  the held-out `test` split. An expectation is written from a question's result, and held-out
  results must not shape anything.
- **The list is the subset.** A question added to the dataset is not part of the smoke run
  until it is listed. A listed ID the dataset does not have stops `run` before it starts.
- **An expectation states what the pipeline does today, not what would be right.** With the
  fake chat model an unanswerable question is answered, so `on-call-contact` has no expected
  outcome: demanding `insufficient_evidence` would fail, and demanding `answered` would write
  a wrong behaviour down as the goal.
- When a deliberate change to search, chunking or a fake model breaks an expectation, change
  the expectation in the same commit and say why in the message. Do not remove a question to
  get a green build.

### The fixture's smoke subset

`tests/fixtures/sample-repo.smoke.json`: `pagination-offset`, `page-size-rationale`,
`title-max-length`, `webhook-attempts` and `api-key-rotation`, each expected to be answered
with every labeled location retrieved and cited (7 locations in C# source, a decision record
and the operations document), and the unanswerable `on-call-contact` with no expected
outcome. `EvalExpectationsTests` pins the list.

A green smoke check means that indexing, search, the tool loop, citation validation, storage,
the runner and the report still work together on six questions. It says nothing about answer
quality, about abstention, or about the real models.

### In CI

The `eval-smoke` job of `.github/workflows/ci.yml` starts a PostgreSQL service container,
builds the tool and runs the three commands above. It sets no provider and no key: the tool's
defaults are the fake providers, and it calls no outside service. The result file and the
report (`report/<name>.report.md` and `.report.json`) are uploaded as the artifact
`eval-smoke`, also when the check failed, and kept for 30 days. They quote the fixture, which
is synthetic and public.

## What is not there yet

- **No baseline.** The numbers have only been computed for runs with the fake models, where
  they show that the plumbing works. A baseline is a real-model run (step M1.5).
- **Never run with the real models.** The cost estimate has only been checked against
  hand-computed numbers in tests. Embedding tokens are not recorded or priced, and with a real
  embedding provider every run indexes the source again, which costs money each time.
- **No record of what the model's own searches returned**: tool calls carry result counts
  only.
- **CI checks six fake-model questions and nothing more.** The smoke check is absolute: it
  cannot see a number getting worse inside what the expectations allow, it never runs a real
  model, and nothing fails a build on a regression between two runs.
- **Local directories only.** A repository indexed from GitHub cannot be evaluated yet; check
  it out and pass the directory.
- **No history table.** The handoff's `eval_runs` table does not exist; a run is a file.
