# ADR-018: Plans over HTTP; a Markdown export that quotes untrusted text instead of escaping it

Status: Accepted · 2026-11-23

## Context

[ADR-016](ADR-016-plan-storage.md) stores plans and [ADR-017](ADR-017-issue-to-plan-validation.md)
makes them. Until this step nothing over HTTP could ask for a plan, read one, mark it as
reviewed or take it out of TRACE. This ADR is the four endpoints that do, and the one piece of
them that is a security decision: the Markdown export.

An export puts three kinds of text into one document that somebody will open elsewhere, paste
into an issue, or render:

- the issue's title, written by whoever can open an issue;
- the model's sentences (summary, steps, reasons, risks, verification, unknowns) and the
  paths it names, which a hostile issue or a hostile repository can steer;
- source lines from the repository, which may be Markdown or HTML themselves.

All three are attacker-influenced. None of them may be able to change what the document says
about itself: add a heading, a link, a script, a "Reviewed" line, an evidence entry.

**Everything here has run only with the deterministic fake model, scripted test models, the
fixture repository and invented issues, against a stub of GitHub.** No real model produced a
plan that was exported, and no export was pasted into the real GitHub.

## Decision

### Routes

| Method | Route | Answer |
|---|---|---|
| `POST` | `/api/v1/repositories/{id}/issues/{number}/plans` | 202, the plan (unfinished), `Location: /api/v1/plans/{planId}` |
| `GET` | `/api/v1/plans/{id}` | 200, the plan; `?checkIssue=true` adds `issueCheck` |
| `POST` | `/api/v1/plans/{id}/review` | 200, the plan with `reviewedAt` |
| `GET` | `/api/v1/plans/{id}/export` | 200, `text/markdown; charset=utf-8` |

The handoff proposed creating a plan through `POST /api/v1/investigations` with
`type: "issue_to_plan"`. It is created under the repository's issue instead, because that is
what it is about, and because the question endpoint's request and validation then stay exactly
as they are (the evaluation baseline depends on question runs not changing). The plan run is
still an investigation: the create response carries `investigationId` and `jobId`, and
`GET /investigations/{id}`, its `/events` and `GET`/`POST /jobs/{id}[/cancel]` work for it as
for a question. A client can poll either the plan or the investigation.

Plans are addressed by their own ID, not the investigation's: review and export are things
done to a plan.

There is no list of plans (M3.1), no deletion (M3.5) and no `PUT`, `PATCH` or `DELETE`.

**Create** takes `{ "snapshotId": "...", "idempotencyKey": "..."? }`. It behaves as creating
a question does: it needs a signed-in user unless `Auth:AllowAnonymousWrites` is on; it shares
the per-client write rate limit (`RateLimits:WritesPerMinute`) with questions and index jobs;
the idempotency key is optional, lives in the body, is scoped to the repository and shares one
namespace with questions, so repeating a request returns the first plan (202 again) and using
the key for a different issue, snapshot or kind of investigation is 409. A repository the
caller cannot see and an unknown snapshot are 404; a snapshot that is not completed is 409; a
missing `snapshotId` or an empty key is a 400 validation problem.

Creating a plan does **not** check that the issue exists. That would cost a call to GitHub on
the request path for something the run finds out anyway: the plan then ends `failed` with
`errorCode` `issue_not_found`.

**Read** returns one `state` for the plan:

| `state` | Meaning | `plan` |
|---|---|---|
| `queued`, `running` | unfinished; poll | null |
| `planned` | a plan with at least one step | the document |
| `insufficient_evidence` | finished, but nothing could be grounded | a document with no steps |
| `failed` | `errorCode` has the fixed outcome name (`issue_not_found`, `issues_permission_missing`, `invalid_model_output`, ...) | null |
| `cancelled` | | null |

With it: the issue's number and its title as stored, the pinned commit, the model and prompt
version, token usage, the `plan-v1` document, `trimmed` (what validation removed, which
[ADR-017](ADR-017-issue-to-plan-validation.md) keeps in the investigation's result and not in
the document), `limitations` (the server's fixed sentences about the outcome) and
`reviewedAt`. Each evidence entry has its ID, path, line range, commit and the cited text read
from the snapshot, as a question's evidence does.

The plan and its investigation are two rows read by two queries. A run can finish between
them; the read notices (investigation completed, plan without a result) and reads the plan
again, so a response never reports an outcome from half of each.

### Has the issue changed? Only when asked

`GET /plans/{id}?checkIssue=true` reads the issue again and compares its fingerprint with the
one stored when the plan was made (`IssueContentHash`: number, title, body, the returned
comments). The answer is `issueCheck.status`: `unchanged`, `changed`, or `unavailable` with a
fixed `code` (`issue_not_found`, `issues_permission_missing`, `github_unavailable`,
`issue_not_recorded`, ...).

- It is opt-in because it costs GitHub calls (a token and two GETs) on a read.
- It never fails the read: whatever goes wrong with the issue is `unavailable`, inside a 200
  with the plan.
- It is never done for a caller who cannot see the repository: the plan is a 404 before
  anything is fetched, and `IssueReader` checks the caller's access once more.
- Closing or relabeling an issue is not a change (the hash does not cover state or labels),
  and it cannot see edits beyond what the size limits cut off.

The export does not include it: an export is made from stored data only.

### Review

`POST /plans/{id}/review` records that a person read the plan. It needs a signed-in user under
the same rule as every write. It is idempotent: the first time stands and later calls answer
200 with the same `reviewedAt`. The mark is one conditional statement
(`UPDATE plans SET reviewed_at = ... WHERE id = ... AND result_json IS NOT NULL AND reviewed_at IS NULL`
and the caller can see the repository), so of requests arriving together exactly one marks the
plan and the others report its time. Reading the plan, changing it and saving it was tried
first and was wrong: the database's "reviewed once" trigger compares timestamps, so two
requests whose clocks read the same instant both passed it and both reported having marked
the plan.

**Only a `planned` plan can be reviewed.** Anything else is 409 with `code`
`plan_not_reviewable` and the plan's `state`:

- `queued`, `running`, `failed`, `cancelled`: there is no document.
- `insufficient_evidence`: there is a document, and the domain would allow marking it, but
  there is no plan in it to acknowledge, and a "Reviewed: yes" line on it would read as
  somebody having approved steps that do not exist.

Reviewing writes `reviewed_at` (and `updated_at`) of one row and nothing else: a test compares
the rest of that row and a digest of every investigation, evidence, event, job, repository,
snapshot and other plan row before and after. It calls nothing outside TRACE. Who reviewed is
still not recorded.

### Export

`GET /plans/{id}/export` returns the plan as a Markdown file:

- `Content-Type: text/markdown; charset=utf-8`
- `Content-Disposition: attachment; filename=trace-plan-issue-<number>-<32 hex digits of the plan ID>.md`.
  The name is made of fixed words, an integer and hexadecimal digits. Never of issue, model or
  repository text.
- `X-Content-Type-Options: nosniff` and `Cache-Control: no-store`.

A plan with no document (`queued`, `running`, `failed`, `cancelled`) is 409 with `code`
`plan_not_finished`. An `insufficient_evidence` result **is** exported: it has a document, and
the export says in its record that TRACE could not ground a plan, lists no steps, and shows
the unknowns.

The document is the same every time for the same stored plan: fixed section order, entries in
stored order, `\n` line endings, one line break at the end, and no time except the stored
"requested at" and "reviewed at", both in UTC. Reviewing a plan changes one line of it.

It contains: the repository's name, the issue number and title, the pinned commit, the
outcome, the model and prompt version, the schema version, the plan and investigation IDs,
the reviewed state; a fixed "how to read this" section; the summary; every step with its
evidence labels; files to change; risks; verification; unknowns; what validation removed; and
every evidence entry with its path, line range and cited text. Evidence is labeled `E1`,
`E2`, ... in stored order, and every entry says either `(evidence: E1, E3)` or
`(not backed by evidence: the model's unsupported statement)`. The second applies to unknowns
and to the reason of a new file, which are stored without citations by design.

### Untrusted text is quoted, not escaped

The export never tries to neutralise untrusted text character by character and leave it in
the flow of the document. Every piece of text that came from the issue, the model, the
repository or configuration is placed inside Markdown's own literal constructs:

- **One-line fields** (title, sentences, paths, names, IDs) go in an **inline code span**.
  The delimiter is a run of backticks one longer than the longest run in the text, with a
  space of padding when the text begins or ends with a backtick or a space. Every kind of
  line break (CR, LF, CRLF, vertical tab, form feed, NEL, U+2028, U+2029) and every tab
  becomes a space, because a code span ends at a blank line.
- **Cited source** goes in a **fenced code block** at the top level, with no info string. The
  fence is backticks, at least three and one more than the longest run in the text, so no
  line of the text can close it; a run of tildes cannot close a backtick fence at all. Line
  breaks of every kind become `\n`; nothing is trimmed.
- In both, other control characters (NUL and ESC included) become U+FFFD, a lone surrogate
  becomes U+FFFD, and invisible format characters are removed: the bidirectional controls,
  zero-width space, byte order mark, soft hyphen and the tag characters. The zero-width joiner
  and non-joiner stay, because scripts and emoji need them.
- Text that is empty once cleaned becomes the fixed word `(empty)`, outside any quoting.

Everything outside code formatting is written by the exporter from fixed sentences, integers,
dates it formats itself and labels it assigns. So the rule for a reader, stated at the top of
every export, is simple: **text in code formatting is quoted and untrusted; everything else is
TRACE's.**

Why not backslash-escaping, which would let the model's sentences render as ordinary prose:

- It needs a rule per construct and per position (a `#` matters at the start of a block, a
  `1.` after digits, `---` on a line of its own), and a list item's content is parsed as
  blocks again. Each rule is a place to be wrong.
- It cannot stop what renderers do after parsing. GitHub turns bare URLs, `www.` hosts and
  e-mail addresses into links, `@name` into a notification and `#12` into a reference, and a
  backslash does not prevent the last three. A plan pasted into the issue it was made from
  could ping a team because the issue told the model to write `@org/team`. Code spans and
  fences are exempt from all of it.
- Quoting has one way out, the closing run of backticks, and one rule that closes it.

The cost is appearance: rendered, the model's sentences are in a monospaced face. That is
also honest about whose words they are.

`MarkdownText` is the quoting, about 150 lines with no dependency. `PlanMarkdown` is the
document. Both live in the application layer and know nothing of HTTP.

### How this is tested

- **A table of 73 hostile inputs** for the quoting alone: block starts (headings, lists,
  quotes, tables, setext underlines, thematic breaks, indented code, footnotes, definition
  lists, task lists), every way to close a span or a fence (backtick runs of each length,
  backticks at either end, tilde fences, a closing fence with indentation and trailing
  spaces), HTML, comments, scripts, images, inline, reference-style and autolinks,
  `javascript:` and `data:` URLs, entities, each kind of line break, NUL, ESC, bidirectional
  controls, zero-width and tag characters, lone surrogates, and forged "Reviewed", evidence
  and section lines.
- **An independent parser is the judge.** Each input is put where the export puts such text,
  and Markdig (a CommonMark parser TRACE does not otherwise use, with its extensions on:
  bare-URL autolinks, tables, footnotes, raw HTML and more) must see *the same document as for
  the word "harmless"*: the same tree of headings, lists, paragraphs and text outside code,
  with the input inside the span or fence and nowhere else, and no HTML, link, image,
  autolink, link definition, table or stray code block at all. One test puts the same attacks
  in a document unquoted and checks that the parser does find them, so the judge is known to
  be looking.
- **A golden file** (`tests/Trace.UnitTests/Golden/plan-export.md`) for a full export.
- **A plan with every field hostile** exports to a document with the same skeleton, the same
  text outside code and the same number of lines outside fences as its harmless twin.
- **Through the API:** the prompt-injection fixture issue planned by the fake model, and a
  plan whose issue title, model output (a scripted hostile model) and cited source (a hostile
  Markdown file indexed for the test) are all written to break the document. The export of
  the second has exactly TRACE's twelve headings, one review line, and nothing hostile outside
  code formatting.

### Nothing writes to GitHub

- The source check from [ADR-015](ADR-015-reading-github-issues.md) (the GitHub client only
  POSTs for the two authentication calls; no other method anywhere) now also treats plan and
  Markdown files as read-only code, and a second check reads the plan code, the plan store and
  the plan endpoints: none names an HTTP client or the GitHub client, and the plan service
  calls the issue reader in exactly one place, the optional issue check.
- A test runs create (and a replay), read, read with `checkIssue`, a plan for a missing issue,
  review twice, export twice and their failure paths against the GitHub stub and asserts:
  every request about the repository was a GET; the only requests that were not are the two
  authentication POSTs; review and export made no request at all; every installation token
  was asked for the one repository with `read` permissions only; and the API answers 405 or
  404 to `PUT`, `PATCH`, `DELETE`, a plan list and invented "comment" and "pull request" routes.

### Telemetry

Three new spans, each with the plan's ID and a fixed outcome: `plan.review`
(`marked`, `already_reviewed`, `not_reviewable`, `not_found`), `plan.export` (`exported` with
the number of steps, evidence entries and bytes; `not_finished`; `not_found`) and
`plan.issue_check` (`unchanged`, `changed` or the fixed reason it is unavailable). One log
line, `Plan {PlanId} was marked as reviewed`, when a review marks a plan. A test captures every
span of every source and every log line at trace level during a hostile plan's create, read,
review and export, and finds no text of the issue, the plan or the source in any of it.

## Consequences

- **What the export guarantees.** For a CommonMark or GitHub-flavoured renderer, and for a
  person reading the raw file: untrusted text cannot start or end a heading, list, table,
  quote or code block; cannot add HTML, a comment, a script, an image, a link of any kind or a
  link definition; cannot add a line; cannot hide or reorder text with control characters;
  and cannot produce anything outside code formatting, so it cannot forge a "Reviewed" line,
  an evidence label or a section.
- **What it does not guarantee.**
  - *That the quoted text is true or harmless to follow.* A step can still say something
    wrong or dangerous; it is shown as the model's words, with or without evidence.
  - *What is inside a fence.* Cited source keeps its own lines, so a fenced block can contain
    a line that reads `- Reviewed: yes`. It is inside the block of an evidence entry.
  - *A renderer that executes or links content inside code blocks*, or one that is not
    CommonMark-like (a wiki dialect, a chat client with its own syntax). The export is meant
    to be read as Markdown source or rendered by a sanitizing renderer such as GitHub's. It is
    not HTML, and nobody should turn it into HTML with a renderer that passes raw HTML through
    and call that safe because of this ADR, although the export gives such a renderer nothing
    to pass through.
  - *Look-alike characters.* Homoglyphs and odd but visible Unicode are left alone.
  - *The words inside a span.* A sentence can contain the text `(evidence: E9)`. It is inside
    the backticks; the real label follows outside them.
- **Rendered, the model's prose is monospaced** and line breaks inside a sentence are gone
  (they became spaces). A model that writes a step as several paragraphs gets one long line.
- **Markdig is a test-only dependency.** The product does not render Markdown.
- The export is rebuilt on every request from stored rows; nothing is cached or stored.
- `checkIssue` makes a `GET` cost GitHub calls. It is not rate limited, like the issue
  endpoints it mirrors ([ADR-015](ADR-015-reading-github-issues.md)).
- An idempotency key means "this request" across questions and plans of a repository. A
  client must not reuse one key for both.
- A stored plan of a schema version this build cannot read answers 500 with `code`
  `plan_unreadable`. There is only one version today, so this has never happened.
- Review and export are not rate limited: they cost one or two queries and no model call.
