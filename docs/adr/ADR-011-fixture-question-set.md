# ADR-011: How the fixture's question set is grown, and what its held-out split is worth

Status: Accepted · 2026-11-23

## Context

The fixture's labeled set had 11 questions, all answerable, over 14 files and 17 passages.
With 17 passages a top 10 contains most of the repository, so recall at 10 said little, and
with no unanswerable question both abstention metrics were `n/a`. Its held-out split had also
been looked at once while a ranking change was tried and reverted
([open question 25](../open-questions.md)).

Milestone M1 asks for at least 25 questions, at least 5 of them unanswerable, in a development
and a held-out split. How the questions are written decides whether the numbers computed from
them mean anything, so the method is recorded here. The rules for a contributor are in
[docs/evaluation.md](../evaluation.md#adding-questions).

## Decision

- **Version 2 of the set has 34 questions**: 27 answerable (31 labeled locations) and 7
  unanswerable; 15 in `dev`, 19 in `test`. The 11 old questions keep their IDs, wording,
  labels and splits.
- **The fixture grew to make new questions possible, not to make old ones easier.** Seven
  invented files were added (a title rule, a webhook notifier, a request throttle, a second
  ADR, an operations page, a second test class, a second web component) and three existing
  files were extended. Lines that old labels point at did not move. The fixture is now 21
  files and 27 passages.
- **Labels before results.** Every new question, label and split was written from reading the
  fixture source, and committed to the dataset file, before the search or the runner was run
  on it. Afterwards nothing was changed: no label, wording or split, and no search setting.
- **Fresh held-out questions.** 13 of the 19 `test` questions are new in version 2 and have
  never been used to choose anything. The 6 older ones are kept, with their history.
- **An unanswerable question asks for a fact the repository does not hold** (where it is
  deployed, its license, the .NET version, its CI system, a latency target, who is on call,
  how passwords are hashed). Each was checked by searching the fixture text for the terms a
  real answer would use. Questions whose honest answer is "the code shows there is no such
  thing" were avoided: whether that counts as an answer or an abstention is a judgment, and a
  label should not rest on one.
- **Several of the new questions sit next to a distractor on purpose**: two places that talk
  about API keys, a 429 next to a 401 and a 400, a limit set in code next to one set in
  configuration. One held-out question is answered by the file that carries the fixture's
  prompt injection.
- **A test checks the set against the fixture** (`EvalDatasetTests`): at least 25 questions,
  5 unanswerable and 8 held-out, unique IDs and wordings, both kinds of question in both
  splits, and every labeled path and line range present in the fixture as checked in.
- **The retrieval floors were restated, not relaxed.** They are measured baselines with the
  fake embeddings. The corpus and the set both changed, so the old counts (9 of 11 questions
  with every location in the top 5, 10 of 11 in the top 10) became 24 of 27 and 26 of 27.
  One old held-out question (`client-api-key`) moved from the top 5 to rank 8 because new
  files also talk about API keys. Nothing was tuned to move it back.

## Consequences

- Recall at 10 on the fixture is still easy: 27 passages of at most 40 lines, and one shared
  line counts as found. It is a regression check, not a measure of retrieval quality.
- The abstention metrics can now be computed, and with the fake chat model abstention recall
  is 0 of 7: that model cites whatever its search returns and abstains only when the search
  returns nothing. This is the expected reading for a model that does not read, and the
  reason the number has to come from a real-model run (step M1.5).
- Adding or changing a question means a new `datasetVersion`, and results across versions
  are not compared like for like (`compare` warns).
- Changing a fixture file changes the content-derived revision, which is pinned in the
  dataset, in `FixtureIndexingTests`, `EvalDatasetTests`, `docs/evaluation.md` and the browser
  tests (`apps/web/e2e/ask.spec.ts`). Tests that pin counts or line numbers of fixture files
  (`RetrievalTests`, `EvalRunnerTests`, `CitationValidationTests`) need the same care.
- 34 questions is still a small set: one question is 3 points of a split's percentage.
