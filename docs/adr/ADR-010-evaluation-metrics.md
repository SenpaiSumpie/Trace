# ADR-010: What the evaluation metrics measure, and what "retrieval" means for recall at 10

Status: Accepted · 2026-11-23

## Context

The handoff (Section 11) names the metrics for questions: retrieval recall at 10 against
labeled locations, citation validity, correct abstention on unanswerable questions, cost and
time. [ADR-009](ADR-009-evaluation-runner.md) gave us a result file per run, but it recorded
only how many results each tool call returned, so recall could not be computed from it.

Recall at 10 presupposes one ranked list per question. An investigation has none: the model
writes its own queries, up to eight tool calls, each search returning at most eight hits, the
best five in full. Something had to be chosen as "the retrieval".

How the metrics are used is in [docs/evaluation.md](../evaluation.md), which also holds the
exact definitions.

## Decision

- **Recall at 10 measures the retriever, asked the question itself.** For every question the
  pipeline accepted, the runner calls the production `search_code` implementation once with
  the dataset question word for word and a limit of 10, and records where the hits are. Recall
  is computed from that list.

  Considered and rejected:
  - *The first search the model makes.* It returns five hits by default and at most eight, so
    it is never a top 10, and it measures the model's first guess at a query.
  - *Everything the model's searches returned during the run.* Up to 64 passages, not 10; the
    number rises with every extra search, so a model that searches more scores better for
    the same retriever. It would also be meaningless with the fake chat model and would cost
    a full model run to measure a change to chunking or ranking.

  What the choice costs: the number does not say whether the model found the evidence, only
  whether the search could have. It is one extra query embedding per question with a real
  provider. And it is the one thing the runner does that the product does not, which ADR-009
  otherwise forbids; it is a measurement beside the pipeline, made after the investigation
  and never shown to the model, so the investigation itself is still exactly production's.
- **The runner does not decide who may search.** The retrieval is made only for a question
  whose investigation the pipeline created, because creating it is where the repository's
  visibility and the snapshot are checked. A refused question has no retrieval.
- **Locations only, no source text.** A hit is recorded as path, line range, rank and its
  rank in each of the two underlying searches. Overlap with a label needs nothing else, and
  the file stays smaller and quotes less of the source.
- **The result format is version 2.** `report` and `compare` read version 1 files and say that
  recall is not measured for them, rather than refusing them or reporting 0%.
- **A relevant location is found by overlap**: a hit in the same file sharing at least one line
  with the label. This is the rule the retrieval tests already used. Containment would punish
  a label that straddles two passages; the price is that large passages find small labels
  easily, so recall is tied to the chunk size and must be re-read when the chunker changes.
- **Recall is counted per labeled location, not per question**, as the handoff words it. The
  report also says how many questions had every location found.
- **Citation validity is the pipeline's own verdict**: evidence divided by evidence plus
  discarded citations. No second validator is written for the evaluation, for the reason
  ADR-009 gives for not copying the pipeline.
- **A correct abstention is `insufficient_evidence` on a question labeled unanswerable.**
  Precision is over all abstentions. Recall is over unanswerable questions that ran to an end,
  so a failure on an unanswerable question counts against recall: the user got an error, not
  an honest "not enough evidence". Questions stopped from outside are left out.
- **Nothing to measure is `n/a`, never 0% or 100%**, and a retrieval that could not be made is
  "not measured", never a miss. An all-failed run must not look like a perfect or a hopeless
  one by accident of division.
- **Cost and time are a median and a nearest-rank 95th percentile** over questions that ran to
  an end. With under 20 questions the 95th percentile is the maximum, which is the honest
  reading of so few values.
- **One number is added that the handoff does not name**: the share of answerable questions
  whose evidence overlaps a labeled location. Without it nothing in the report responds to an
  answer resting on the wrong file, and a comparison could not flag it.
- **In a comparison, a question regressed if** it lost the outcome its label calls for, a
  labeled location left the top 10, its evidence stopped overlapping a label, or a smaller
  share of its citations was valid. Regressions are listed first; a question that got better
  in one way and worse in another is a regression. **Cost and time never decide**: time
  differs between identical runs, and a cost change is a trade-off for a person.
- **`compare` exits 0 on regressions.** Whether a regression should fail a build belongs to
  the step that puts a smoke run in CI.
- **Reports are Markdown plus JSON, written next to the result file**, and contain IDs, file
  paths and numbers only. Values taken from a result file are written as code spans with
  backticks, pipes and line breaks removed, because a result file can be edited by anyone
  and paths come from the evaluated repository.

## Consequences

- A change to chunking, ranking or embeddings can be measured without a chat model.
- A change to the prompt cannot move recall at 10. Its effect shows in outcomes, citation
  validity, the labeled-evidence number, cost and time.
- The searches the model makes are still recorded with counts only. If real-model runs show
  good recall and poor evidence, the model's own hits will have to be recorded too; that
  needs a change to what the pipeline stores per tool call.
- Answer text is never judged. "Factual grounding" in the handoff's sense is not measured.
- A single pair of real-model runs cannot separate a real change from run-to-run variation;
  `compare` does not try to.
- Every number so far comes from the fake providers on the 11 fixture questions.
