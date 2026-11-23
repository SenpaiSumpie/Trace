# ADR-016: A plan is stored with its investigation, cites stored evidence, and freezes when finished

Status: Accepted · 2026-11-23

## Context

An issue-to-plan investigation (milestone M2) ends in an implementation plan. The handoff's
data model (Section 4) has a `plans` table (`investigation_id`, `schema_version`,
`json_result`, `approved_at`) and an `issues` table with a `body_snapshot`.
[ADR-015](ADR-015-reading-github-issues.md) read issues without storing anything and left
"what a plan was made from" to this step.

This step adds the storage and the contract only. Nothing creates a plan yet: the workflow is
M2.3, the API is M2.4. So the decisions here are the ones the later steps have to build on,
and the ones the database has to hold even if a later step gets something wrong:

1. a plan belongs to exactly one investigation;
2. a plan's citations refer to the investigation's snapshot;
3. reviewing a plan changes nothing but the review.

The investigation type `issue_to_plan` has existed in the domain and in the type column's
check constraint since the first migration, so no existing row or constraint changes.

## Decision

### One row per plan investigation, created with it

`plans` holds one row for an issue-to-plan investigation. The row is created together with
the investigation, when only the issue number is known, and is **unfinished** until the
investigation completes and gives it a result. A plan row therefore also answers "which issue
is this running investigation about", which the handoff's `investigations.issue_id` was for.

| Column | |
|---|---|
| `id` | UUID (v7, from the domain) |
| `investigation_id`, `snapshot_id`, `repository_id` | one composite foreign key to `investigations (id, snapshot_id, repository_id)`, cascade on delete; unique |
| `issue_number` | 1 or more |
| `issue_title` | up to 256 characters, one line; null until the issue has been read |
| `issue_content_hash` | 64 lowercase hex characters; null until the issue has been read |
| `schema_version` | up to 20 characters; null until finished |
| `result_json` | `jsonb`; null until finished |
| `reviewed_at` | null until reviewed |
| `created_at`, `updated_at` | as everywhere |

The snapshot and repository are copied from the investigation and the foreign key covers all
three columns, so a plan cannot name a snapshot or repository other than its investigation's.
The repository of the issue is the investigation's repository: there is no second way to say
it. The key's uniqueness gives an investigation at most one plan. A trigger refuses a plan
for an investigation that is not of the type `issue_to_plan`.

The plan is deleted with its investigation, and so with its repository: uninstalling the
GitHub App still removes everything ([ADR-002](ADR-002-github-app-auth-and-authorization.md)).

### What is kept of the issue: number, title, a hash. Not the text

There is still no `issues` table and no `body_snapshot`.

- **Stored:** the number; the title as `IssueReader` returns it (cleaned to one line, at most
  256 characters, the same limit as [ADR-015](ADR-015-reading-github-issues.md)); and a
  SHA-256 over the text the plan was made from.
- **Not stored:** the body and the comments.

Why not the text. It is written by whoever can open an issue, it can be 60,000 characters of
a private repository's discussion, and nothing needs to read it back: the plan's own content
is in its result, and the issue stays on GitHub, where the reader can open it. What a reader
does need is to be told when the issue has changed since the plan was made, and a hash
answers that without keeping a copy. The title is kept because a list of plans is unreadable
without one and one bounded line is a small thing to hold. It is data: every reader must show
it as text, never as markup, and it is never an instruction to anything.

The cost: once an issue is edited or deleted on GitHub, TRACE can say *that* the plan was
made from something else, not *what*. If that turns out to matter, a body snapshot is one
nullable column away, and it then needs a retention rule.

**What the hash covers** is fixed in code so that every step computes it the same way:
`IssueContentHash.Compute(IssueDetail)` in `Trace.Application/Issues`. It is the lowercase
hex SHA-256 of the UTF-8 bytes of this text, where every piece of issue text is preceded by
its length in UTF-16 characters:

```
trace-issue-v1
<number>
<length>:<title>
<length>:<body>
<number of returned comments>
<comment id>            (for each returned comment, in order)
<length>:<comment body>
```

Each line ends with a line feed. The input is the issue **as `IssueReader` returns it**,
after cleaning and after the size limits, because that is all a plan run is given.

- Covered: number, title, body, and the ID and body of each returned comment.
- Not covered on purpose: state, labels, authors, dates, the comment count. Closing or
  relabeling an issue should not make its plan look outdated.
- Not covered because it cannot be: anything the limits cut off (a body past 20,000
  characters, a comment past 4,000, comments after the 30th). An edit there goes unnoticed.

Rule for M2.3: a plan run may show the model nothing of an issue beyond what the hash covers
(labels and author names included), or it must extend the hash under a new first line. The
stored value does not say which layout produced it; if a second layout ever exists, a column
has to be added then, and rows without it are `trace-issue-v1`.

The title and hash are recorded when the run reads the issue, not when the plan is requested,
and may be recorded again until the plan is finished: a retried job reads the issue again and
the last reading is the one the plan was made from.

### The result is a versioned document; citations are stored evidence rows

The result is the JSON document described in [plan-result-schema.md](../plan-result-schema.md),
version `plan-v1` (`PlanResult` in `Trace.Application/Plans`): a summary, ordered steps, files
to change, risks, verification steps and unknowns, each carrying a list of citations, plus the
list of evidence those citations point into. The version is in the document and in its own
column, and a check constraint refuses a row where they differ.

**Citations reuse `evidence_refs`.** A citation in a plan is the ID of an `evidence_refs` row
of the plan's investigation: the table question answers already use, whose two composite
foreign keys already guarantee that a cited passage belongs to the snapshot the investigation
is pinned to ([ADR-004](ADR-004-snapshot-immutability-and-citations.md)). The plan document
lists each cited row once under `evidence` (ID, path, lines, excerpt) and every item cites by
ID. There is no second table of plan citations and no path-and-line pair in the document that
the database cannot check.

What ties the JSON to the rows is a trigger that runs when the transaction commits. It
collects every string under a `citations` key anywhere in the document and every
`evidence[].id`, and refuses the plan unless each is the ID of a code evidence row of the
same investigation. It runs at commit because the investigation's completion, its evidence
rows and the plan's result are written in one transaction, in no fixed order. A second
trigger refuses to change or delete an evidence row of a finished plan on its own, so what a
plan cites stays what it cited. Deleting the investigation still removes everything.

The database does not validate the rest of the document. That an item which needs a citation
has one, that limits hold, that nothing unknown is in it: `PlanResult` checks all of it when
writing and when reading, and refuses both ways. Whether a cited location is real and was
retrieved during the run is the workflow's job (M2.3), exactly as for questions
([ADR-007](ADR-007-injection-and-untrusted-source-boundary.md)).

Only code can be cited in `plan-v1`. The issue is not evidence.

### Finished means frozen; reviewing is the one thing left

A plan is finished when it has a result. The database then holds:

- **A result needs a completed investigation.** Checked at commit by the same trigger as the
  citations.
- **A finished row does not change**, except `reviewed_at` (and `updated_at`, which is
  bookkeeping). The trigger compares the old and new row as JSON without those two columns,
  so a column added later is frozen without anyone remembering to add it.
- **`reviewed_at` is set once.** Moving it or clearing it is refused.
- **An unfinished plan cannot be reviewed** (check constraint): there is nothing to review.
- **A plan never moves**: its investigation, snapshot, repository and issue number cannot be
  changed, finished or not.
- A finished plan can still be deleted, by itself or with its investigation. Retention needs
  that; freezing is about meaning, not about keeping rows forever.

The domain (`Plan`) enforces the same rules first, with clearer errors.

**Marking reviewed twice is not an error.** The first time stands; the second call reports
that nothing changed and writes nothing. A reviewer who clicks twice, or two tabs, should not
get a failure for something that is already true, and the first acknowledgement is the fact
worth keeping. **Reviewing an unfinished plan is an error** in the domain and in the database,
whatever state its investigation is in: queued, running, failed, cancelled, or completed
without having given the plan a result.

Reviewed is one timestamp. Who reviewed is not recorded: any signed-in user with access to a
repository can already see and cancel everything on it (open question 47), and a reviewer
column would need rules for a deleted user that nothing asks for yet. The handoff calls the
column `approved_at`; it is `reviewed_at` here because the handoff's own note says it "only
acknowledges review", and "approved" promises more. Reviewing writes one column of one row in
TRACE's database and nothing anywhere else.

## Consequences

- **Nothing writes a plan yet.** No endpoint creates, returns or reviews one, and no store
  method was added for one: the rules are proven by tests that go through the domain model and
  through raw SQL. M2.3 and M2.4 add the code that uses them and may find a rule awkward.
- M2.3 must create the plan row with the investigation, record the issue when it reads it,
  and complete the investigation, add its evidence rows and finish the plan in one
  transaction. A plan investigation that fails or is cancelled leaves an unfinished plan
  behind, which says which issue it was about.
- What `investigations.result_json` holds for a plan investigation is left to M2.3; the plan
  is in `plans.result_json`. `Investigation.Question` is still required and M2.3 has to decide
  what a plan investigation puts there.
- The schema is strict (no unknown fields, no optional fields, every change a new version).
  That is cheap now and will cost a version bump for the first thing M2.3 finds missing. A
  finished plan cannot be rewritten, so every version ever stored needs a reader for as long
  as its rows exist.
- The commit-time trigger looks for citations by the names `citations` and `evidence[].id`.
  A new schema version that names them differently must change the trigger with it.
- The database does not stop SQL from moving an investigation out of `completed` after its
  plan was finished, or from changing an investigation's type; the domain has no such
  transition. It also checks citations only when the result is written, and protects the
  evidence rows afterwards, not the passages: those are protected by snapshot immutability.
- A change to the issue that the size limits hide is not detected, and nothing detects a
  change at all until M2.4 or M2.5 compares the hash with a fresh reading.
- The limits in the schema (30 steps, 50 files, 2,000 characters a step, and so on) are first
  guesses, never measured against a real plan.
