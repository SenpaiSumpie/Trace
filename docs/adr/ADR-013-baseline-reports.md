# ADR-013: A baseline is a committed, dated report; the result file and the keys stay local

Status: Accepted · 2026-11-23

## Context

Step M1.5 ran the fixture's question set with the real models for the first time. The run
produces a result file that quotes the evaluated source and the model's answers, and the tool's
`report` command produces a Markdown table of metrics that deliberately contains no question,
answer or source text ([ADR-010](ADR-010-evaluation-metrics.md)). Neither is a baseline someone
can read later and trust: the first is git-ignored, the second cannot say whether the answers
were right, what the run cost in total, or what limits the numbers.

Three further things had to be settled for a run that spends money: where the price of the
real model comes from, how the keys reach the tool, and what happens to a run that stops
partway.

## Decision

- **A baseline is a dated Markdown file under `docs/eval/`**, named
  `<date>-<what was run>.md`. It is written by hand from the result file and from reading the
  answers, and states the commit, versions, every metric per split, every question's outcome,
  tokens, cost, everything spent including probes, a reading of the unanswerable questions and
  of a sample of answers, and the limits of the result. A baseline is never edited afterwards;
  a new run gets a new file.
- **The result file is not committed**, also for the public synthetic fixture. One rule for
  every repository is safer than an exception, and the file is large and of no use to a
  reader. It is kept on the machine that ran it so that `compare` can use it. If it is lost,
  the committed report still holds every number, but a question-by-question comparison needs a
  new run.
- **The baseline report may quote the fixture**, because the fixture is invented and public.
  A baseline for a private repository goes under the git-ignored `eval/local/` instead.
- **The price of `claude-opus-5-5` ships in `evalsettings.json`** with a note that it is
  unconfirmed, rather than being supplied on each run. A run without a price records `null`
  costs, and a baseline without a cost is not one. A wrong price is corrected in one place and
  every stored estimate can be recomputed from the token counts in its result file.
- **Keys come from the process environment only.** The tool has no option that takes a key and
  never reads `.env` itself; whoever runs it loads the two variables into the environment. No
  key can end up in a command line, a settings file or a result file that way.
- **No resume and no merge were built.** The pass finished in one go, so there was nothing to
  resume. A subset can be run through an expectations file (development questions only) or a
  temporary dataset file, and combining two result files would be done by hand and described
  in the report.

## Consequences

- The numbers in a baseline report are copied by a person or an agent, so they can be
  mistyped. The report names the result file and the commands, and anyone holding the file
  can recompute them with `report`.
- Reading answers by hand does not scale past a few dozen questions and is one reader's
  judgment. A judged metric is still an open decision
  ([open question 74](../open-questions.md)).
- Held-out results are published in the report. They must still never be used for tuning;
  nothing but the rule prevents it.
- Cost estimates made with the shipped price are wrong by exactly as much as the price is,
  until the owner confirms it.
- When resume or merge is needed, it has to be built, with tests, before that run.
