# ADR-012: The evaluation smoke run in CI is gated by fixed expectations, not by a comparison

Status: Accepted · 2026-11-23

## Context

The evaluation tool ([ADR-009](ADR-009-evaluation-runner.md)) drives the production
investigation pipeline, and its `run` command exits 0 whatever the questions' outcomes are: a
failed question is a measurement. Until now nothing ran it in CI, so a change could break the
runner, or the pipeline as the runner uses it, without failing a build.
[ADR-010](ADR-010-evaluation-metrics.md) left one question to this step: should a regression
found by `compare` fail a build?

CI can only use the fake providers: it must need no key and spend nothing. The fake chat model
does not read. It cites the first passages its one search returns and abstains only when the
search returns nothing, so it answers every unanswerable question of the fixture.

How the commands are used is in [docs/evaluation.md](../evaluation.md#the-smoke-check).

## Decision

- **A new `check` command decides pass or fail.** It holds one result file against an
  expectations file and exits 3 when an expectation is not met, listing each one with the
  question's ID and the reason. `run` keeps its meaning (exit 0 when the run finished), and
  `report` and `compare` keep theirs. The gate is code in the tool with tests, not shell logic
  in the workflow.
- **`compare` still exits 0 on regressions, and CI does not use it.** A comparison needs a
  baseline result file. Committing one for the fake models would be a second copy of the
  expectations that has to be regenerated on every intended change, and it quotes source. For
  the real models a build gate on `compare` would be wrong for a different reason: one run per
  side cannot tell a real change from run-to-run variation (ADR-010). A real-model regression
  is for a person to read. If a baseline ever becomes stable enough to gate on, that is a new
  decision.
- **The subset is an explicit list of question IDs in a file of its own**,
  `tests/fixtures/sample-repo.smoke.json`, which also holds the expectations.
  `run --expectations <file>` asks only the listed questions. A question added to the dataset
  does not join the smoke run unless someone lists it, and a listed ID that the dataset no
  longer has stops the run with an error, so the subset cannot drift silently in either
  direction.

  Considered and rejected:
  - *A tag on questions in the dataset.* It would put a CI concern into the label format that
    private datasets share, and the expectations would still need a home.
  - *The first N questions, or the whole development split.* Both change when the dataset
    does. The whole split would also pin today's fake-model behaviour for 15 questions, one of
    which does not cite its label, for no extra protection.
  - *A second small dataset file.* The same questions would exist twice with two sets of
    labels to keep in step.
- **Expectations may only name development questions.** Both `run --expectations` and `check`
  refuse a question of the held-out `test` split. An expectation is written by looking at a
  question's result, and the held-out split exists so that its results shape nothing. With the
  fake models that would do little harm, but the same file format will be used with real ones,
  and a rule with an exception for "harmless" cases is a rule nobody can check.
- **What is expected**, per listed question:
  - always: the question is in the result file, and every citation the model made passed
    validation (no discarded citation);
  - `outcome`, when stated: the question ended with exactly that outcome. When it is not
    stated the question must still end as an answer or an abstention, not as a failure;
  - `labelsRetrieved`: every labeled location is among the 10 best hits of the search;
  - `citesLabel`: the validated evidence overlaps a labeled location.

  And for the run: it completed, it is a run of the dataset the expectations name, and the
  source was the revision the labels were written against.
- **The smoke subset is six development questions**: five answerable ones, chosen so that their
  labels are in different kinds of file (C# source, a decision record, the operations
  document) and so that two of them have more than one labeled location, each
  expected to be answered with every label retrieved and cited; and one unanswerable question
  with **no expected outcome**. The fake chat model answers it, a real model should abstain,
  and an expectation either way would be a claim this check cannot support. It is there so
  that the `answerable: false` path runs.
- **The expectations are today's fake-model behaviour, written down as such.** They were
  chosen after looking at a development-split run. They say that indexing, search, the tool
  loop, citation validation, storage, the runner and the report still work together. They do
  not say an answer is right.
- **A run limited by an expectations file records its split as `subset`.** Nothing else in the
  result file changes and no field was added, so the schema version stays 2; a report of such
  a run says that its numbers are about the subset.
- **CI runs it as a job of its own** (`eval-smoke`) beside the others, against a PostgreSQL
  service container: build the tool, `run`, `report`, `check`, then upload the result file and
  the report as the artifact `eval-smoke` whether or not the check passed. The job sets no
  provider and no key; the tool's defaults are the fakes. The artifact quotes the evaluated
  source, which is fine here because the fixture is synthetic and already public.

## Consequences

- A change that breaks the runner, or stops the pipeline answering, retrieving or validating
  for these six questions, fails the build with the question and the reason in the log.
  Integration tests run the same three commands, so the failure is also caught locally.
- A deliberate change to search, chunking or the fake models can break an expectation. Then
  the expectation is updated in the same commit, with the reason; that is the intended cost of
  a pinned check. Only development questions are affected.
- The check is absolute, not relative: it cannot notice that a number got worse while staying
  inside what the expectations ask. The integration tests that pin recall floors remain.
- Nothing about a real model is checked in CI. Abstention in particular is not: no fixture
  question makes the fake model abstain.
- The job builds the tool again (the `backend` job builds everything too). Running in parallel
  it adds no wall-clock time to a build, at the cost of some runner minutes.
- The artifact is kept for 30 days and is readable by anyone who can read the repository's
  workflow runs.
