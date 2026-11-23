# ADR-014: A repository indexed through the application is evaluated from a copy of its snapshot

Status: Accepted · 2026-11-23

## Context

The evaluation runner could only index a local directory ([ADR-009](ADR-009-evaluation-runner.md)).
A real repository reaches TRACE another way: through the GitHub App, indexed by a job, into the
database the application runs on. Measuring answers on such a repository (milestone M1) should
not need a checkout of it, a second embedding bill, or its name anywhere in this public
repository.

A repository from GitHub is visible only to a signed-in user who holds an access row for it,
while its installation is active (`RepositoryVisibilityQueries`,
[ADR-002](ADR-002-github-app-auth-and-authorization.md)). The runner has no user: it asks its
questions as the anonymous caller, who sees public local repositories only. So the question
was how the runner gets at an indexed private snapshot without weakening that rule and
without leaving traces in the owner's data.

## Decision

- **`run --snapshot <snapshot id>` copies one completed snapshot into the run's throwaway
  database and evaluates the copy.** The snapshot row and its passages, with their stored text,
  line ranges and embeddings, are read from the configured database
  (`ConnectionStrings:Trace`) and written into the `trace_eval_<random>` database the run
  already creates. There they belong to a new repository row that is public, not from GitHub,
  and named after the dataset. From that point a snapshot run is the same as a directory run.
- **The configured database is only read.** The copy uses connections on which PostgreSQL
  keeps every transaction read-only (`default_transaction_read_only`), so a write would be
  refused by the server, not merely avoided by the code. No investigation, event, evidence or
  job row is added to it, so there is nothing to clean up and nothing appears in the owner's
  history in the application. Everything the run stores goes into its own database, which is
  dropped at the end.
- **Nothing but the snapshot is copied.** Not the repository row (owner and name stay behind),
  not the installation, user or access rows, not another snapshot or repository. Whatever a
  model asks for during the run, the database it is answered from holds one snapshot.
- **The production pipeline and its checks still run, unchanged.** `QuestionInvestigator`
  checks that the caller may see the repository, as always; the copy passes because it is a
  public local repository in the run's own database, the same kind the runner has always
  created. No setting, user or code path was added that lets a caller see a repository they
  otherwise could not.
- **The copy is made by the tool, with the database's own credentials.** That is the
  privilege of whoever operates the database, who can read every row anyway. The code lives
  in `tools/Trace.Evals`, which no project under `src/` may reference (the dependency test
  enforces it), so the API cannot reach it.
- **The snapshot is chosen by its ID on the command line.** An ID identifies nothing to
  someone without the database. The dataset format is unchanged; its `revision` is checked
  against the snapshot's commit as before.
- **A run stops before its first question when the snapshot was indexed with another
  embedding model** than the one the run is configured for. Otherwise every search would fail
  and the run would spend model calls to measure nothing.
- **Results of a snapshot run go to `eval/local/results/` by default**, not `eval/results/`.
  Both are git-ignored, but `eval/local/` is the one place for anything about a private
  repository, and the default should be the safe one.
- **The copy is written the way an index is**: a building snapshot, its passages, then
  completed, because the schema refuses passages for a snapshot that is already complete.

Considered and rejected:

- *Running inside the configured database as the owner* (an option naming a user ID). It
  needs no copy, but every question would add an investigation, its events, its evidence and
  a queued job to the owner's data, the run would share the application's daily token budget,
  a worker running at the same time could pick the jobs up, and an option that says "act as
  this user" is exactly the kind of switch that should not exist near the access check.
- *Marking the repository public for the run.* It changes the owner's data and, while the run
  lasts, what the API serves.
- *Cloning from GitHub or reading a checkout.* It needs a token or a path on the owner's
  disk, embeds the source a second time, and can pick up a different commit than the one the
  labels were written against.
- *Naming the snapshot in the dataset file.* An ID belongs to one database; the dataset
  describes a repository at a revision and should stay usable against any database that holds
  it.

## Consequences

- A private repository can be measured at no embedding cost, and the measurement is of the
  same index the application serves, not of a re-index that might differ.
- The run's token budget starts from zero in its own database, as for a directory run: it
  does not count what the application spent that day, and the application does not count the
  run.
- The source database must have the same schema version as the tool. An older database fails
  with a query error before any question is asked.
- Query embeddings are still made during the run, so the real embedding provider and its key
  are needed for a snapshot it indexed, and the questions are sent to that provider and to
  the chat model, as in the product.
- Whoever runs the tool can copy any snapshot of the database they point it at. That is not a
  new power: they hold the database password.
- The result file of a snapshot run contains the commit SHA, file paths and source excerpts of
  the repository. It never contains the repository's name, but the paths and excerpts are
  just as private. It must stay under `eval/local/`.
- With `--keep-database` a copy of the snapshot's source stays on the server until that
  database is dropped.
