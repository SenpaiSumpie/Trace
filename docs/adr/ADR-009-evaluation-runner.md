# ADR-009: The evaluation runner is a tool that drives the production pipeline

Status: Accepted · 2026-11-23

## Context

Milestone M1 replaces "a handful of runs someone looked at" with numbers that can be re-run
after any change (handoff Section 11). That needs a way to ask a fixed set of labeled
questions and keep everything about each answer. Three things had to be decided: how the
runner relates to the code that answers questions for users, where it runs its questions, and
where results go. How to use it is in [docs/evaluation.md](../evaluation.md).

## Decision

- **The runner calls the production `QuestionInvestigator`; it does not reimplement it.**
  `tools/Trace.Evals` registers services with the same `AddTraceInfrastructure` the API and
  worker use, creates each investigation with `CreateAsync` and runs it with `RunAsync` as a
  single final job attempt, then reads back the stored investigation and its events. A
  measurement of a copy of the pipeline would say nothing about the pipeline, and a copy would
  be a second place where the tool allow-list and citation checks could be got wrong.
- **One attempt, no retry, no job queue.** The worker retries a model outage up to three
  times, each from the beginning. In an evaluation a retry would be a second, unrecorded model
  run with its own cost, so the runner passes `maxAttempts: 1` and records the failure. The
  queued job row that `CreateAsync` writes is never picked up; the database is discarded.
- **A project under `tools/`, not a third host.** It composes the application like a host
  does, so it may reference Application, Infrastructure and Domain. It serves nobody and is
  not deployed. The dependency test now covers `tools/`: every tool needs a rule, a tool may
  reference only the layers (never a host or another tool), and no project under `src/` may
  reference anything outside `src/`.
- **Each run gets its own database on the configured server**, named `trace_eval_<random>`,
  migrated, filled by indexing the source directory, and dropped at the end. The configured
  database is only connected to in order to issue `CREATE DATABASE` and `DROP DATABASE`.
  Considered and rejected: running against the development database (a run would add
  investigations to it and count against its daily budget, and a run's results would depend
  on what was already there), and starting a container from the tool (a second way to get
  PostgreSQL, when the server a developer or CI already has is enough).
- **The tool's environment is `Evaluation`, never `Production`.** Production is the one
  environment that refuses the deterministic fakes, to keep them from answering users. The
  runner has no users and its default is the fakes, so that a run costs nothing unless real
  providers are asked for.
- **Results are files, one per run, in a git-ignored directory.** A file can be diffed,
  attached and compared without a server, and it quotes source text, which must not be
  committed for a private repository. The handoff's `eval_runs` table is not created.
- **Prices are configuration** (`Eval:Prices:<model id>`). A model without prices gets a
  `null` cost and a warning, not a guess; a half-configured price stops the run. Cost is an
  estimate for comparing runs, computed from the token counts the pipeline already stores.
- **A question's ending never ends the run.** Failures the pipeline knows (model error,
  refusal, unusable output, budget) are read from the investigation. A timeout or an
  unexpected exception is given its final state through `AbandonAsync`, as the job queue would
  do, and the run continues. Stopping the run cancels the question in progress, marks the rest
  `not_run`, and still writes the file.
- **A dataset may name the revision it was labeled against**, and a run on a different
  revision is refused unless explicitly allowed: a line label on the wrong revision is a wrong
  label that looks like a model failure.

## Consequences

- Any change to the prompt, tools, retrieval or validation is measured automatically, because
  there is only one implementation.
- The production spending limits apply to a run. The daily token budget (3,000,000) counts
  every question of the run, so a large real-model run can end in `budget_exhausted` unless
  the limit is raised for it. That is also the run's spending cap.
- The database user needs the right to create databases. A killed process leaves its database
  behind; the documentation says how to find and drop it.
- With a real embedding provider every run embeds the source again. Embedding tokens are not
  recorded or priced.
- Only local directories can be evaluated. Their revision is a content-derived ID, not a Git
  commit.
- Result files have no retrieved locations yet, only result counts per tool call. Recall at 10
  (step M1.2) needs more than that.
- Anyone running the tool with real provider keys sends the source to those providers, exactly
  as the product does.
