# ADR-015: Issues are read from GitHub on demand, bounded, and never stored or written

Status: Accepted · 2026-11-23

## Context

The second half of the v0.1 journey starts from a GitHub issue (milestone M2). Before a plan
can be made from one, TRACE has to list a repository's issues and fetch one with its comments.

Three things make this more than two GET requests:

- **Issue text is written by strangers.** Anyone who can open an issue or comment on one
  controls the title, the body and every comment. It is untrusted input in the same sense as
  source code ([ADR-007](ADR-007-injection-and-untrusted-source-boundary.md)), and a later step
  will put it in front of a model.
- **TRACE must not be able to change anything on GitHub** (handoff Section 10). Reading issues
  needs a new permission on the GitHub App, and the app's owner has to approve it; the code
  could not be tried against the real GitHub in this step.
- **GitHub's issue endpoints also return pull requests**, redirect an issue that was moved to
  another repository, and fail in several distinct ways a user can do something about.

## Decision

### Not stored: fetched when asked for

There is no `issues` table yet. `GET` on the API fetches from GitHub (or from the demo
repository's fixture files) every time and returns the result; nothing about an issue is
written to the database, a cache or a log.

The handoff's data model has an `issues` table with a `body_snapshot`. That snapshot exists to
record *what a plan was made from*, so it belongs to the step that creates plans (M2.2 and
M2.3), where the row has something to be tied to. Storing issue text now would mean keeping
attacker-controlled text of private repositories with no reader, no retention rule and no
deletion path. Until then the only copy of an issue is on GitHub.

### Read-only by construction

- The port (`IIssueSource`) has two members, list and get. There is nothing to call that
  would create, edit, close or comment.
- `GitHubClient` makes GET requests for issues, comments and the repository's settings. Its
  only POST requests are the two GitHub's authentication requires: exchanging the sign-in code
  and asking for an installation token. A unit test reads the source and fails if another HTTP
  method appears there or in any issue code, or if a third POST is added; an integration test
  records every request the stub receives and asserts the same at run time.
- The installation token for issue calls is asked for `issues: read` and `metadata: read`,
  for the one repository, and nothing else. The token used for indexing still asks for
  `contents: read` and `metadata: read` only. Each call gets the least it needs, so a token
  that reads issues cannot read source and the other way round.
- The API maps only `GET` on the two issue routes.

### Visibility is checked first

`IssueReader` asks the one visibility rule ([ADR-002](ADR-002-github-app-auth-and-authorization.md))
before it resolves a source. A repository the caller may not see is a plain 404, identical to
a repository that does not exist, and GitHub is not called at all for it.

### Limits, with every cut flagged

Applied in one place (`IssueReader.Bound`), to GitHub and fixture issues alike, after cleaning:

| What | Limit |
|---|---|
| Issue body | 20,000 characters |
| Each comment | 4,000 characters |
| Comments returned | the first 30, oldest first |
| Body and returned comments together | 60,000 characters |
| Title | 256 characters |
| Labels | 20, each 100 characters; author login 100 characters |
| One response from GitHub | 8 MB; a larger one is refused, not read |
| List page | 1 to 100 issues, default 30 |

A cut body or comment carries `bodyTruncated: true`; comments that exist but are not returned
(count limit, total limit, or more on GitHub than were fetched) set `commentsTruncated: true`,
and `commentCount` says how many there are. Cleaning removes NUL, which PostgreSQL text cannot
hold, and replaces a lone surrogate with U+FFFD; a cut never splits a surrogate pair. Titles,
labels and logins are single lines: control characters in them become spaces.

The values are first guesses sized for a prompt (60,000 characters is roughly 15,000 tokens),
not measured against real issues.

### Pull requests, moved issues and odd numbers

- A list entry or a fetched issue that carries GitHub's `pull_request` marker is left out. A
  number that belongs to a pull request answers 404 `issue_not_found`, the same as a number
  that does not exist: to TRACE it is not an issue.
- A redirect is never accepted, whether the HTTP handler followed it or not. GitHub redirects
  a transferred issue to its new repository, which the caller was not checked against.
- An answer whose `number` is not the one asked for is treated as not found.

### Errors a person can act on

Every failure has a fixed `code` in the problem response, and the same name in telemetry:

| Situation | Status | `code` |
|---|---|---|
| Repository does not exist or is not visible to the caller | 404 | (none) |
| No such issue, or the number is a pull request, or the issue was moved or deleted | 404 | `issue_not_found` |
| Bad `cursor` | 400 | `invalid_cursor` |
| The installation has not granted read access to issues | 409 | `issues_permission_missing` |
| Issues are switched off for the repository | 409 | `issues_disabled` |
| The repository has no source of issues (demo without fixture files) | 409 | `issues_unavailable` |
| GitHub is rate limiting | 503 | `github_rate_limited` |
| GitHub failed, could not be reached, or sent something unreadable or too large | 502 | `github_unavailable` |

The missing permission is 409 rather than 403 or 404: the caller is allowed to see the
repository and did nothing wrong; the repository's connection to GitHub is in a state its
owner can change. It is recognized two ways, both from GitHub's documentation: the token
request is refused with 422 when it asks for a permission the installation has not granted,
and an issue call is refused with 403 when the token lacks it.

"Issues switched off" is not guessed from a status code. On a 404 or 410 from an issue
endpoint, TRACE reads the repository's `has_issues` setting and reports `issues_disabled`
only when that is false.

### Paging by cursor

No list endpoint was paged before this one. Issues use `?limit=<1..100>&cursor=<opaque>` and
answer `{ "items": [...], "nextCursor": "..." | null }`. A page may hold fewer items than the
limit (even none) while `nextCursor` is set; the list ends when `nextCursor` is null.

A cursor was chosen over page numbers because filtering pull requests out of GitHub's pages
makes "page 3 of our list" impossible to compute without reading everything before it, and
there is no total to offer. The cursor holds a position in GitHub's own paging (page and
offset); one request reads at most three of GitHub's pages of 50. The same envelope works for
database lists with a keyset cursor, so step M3.4 (OpenAPI and paging) can adopt it for
investigations and repositories instead of inventing a second convention.

### The demo repository has fixture issues, beside the fixture

`tests/fixtures/sample-repo.issues/` holds invented issues, one JSON file each, served for the
demo repository when `Demo:IssuesPath` points at it. They go through the same limits. One is a
prompt-injection attempt for later steps to test against; one stands for a pull request.

The directory is a sibling of `tests/fixtures/sample-repo`, not inside it, because everything
inside is indexed and hashed into the fixture's revision ([ADR-011](ADR-011-fixture-question-set.md)).
An issue file in there would change the revision the evaluation dataset and its baseline are
pinned to. A unit test keeps the two apart.

### Nothing about an issue's text in telemetry

Spans `issues.list` and `issues.get` carry the repository ID, the issue number, the source
(`github` or `fixture`), counts, whether anything was cut, and the outcome code. The one log
line on failure carries the repository ID and the code. A test fetches issues with marker
strings in title, body, comment, label and author and asserts that no span of any source in
the process and no log line at any level contains them ([ADR-008](ADR-008-telemetry-and-correlation.md)).

### No model sees an issue yet

Nothing in this step passes issue text to a chat or embedding model. That begins with the
plan workflow (M2.3), which has to treat it as quoted data.

## Consequences

- **Never run against real GitHub issues.** Everything is proven against a stub that answers
  in GitHub's documented formats, and the fixture. The app does not have the Issues permission
  until its owner approves it (`docs/github-app-setup.md`). The 422 on the token request, the
  403 on an issue call, the `pull_request` marker, the 410 for a deleted issue and the redirect
  for a moved one are all taken from documentation and may differ in detail.
- Every request costs GitHub calls: one token request plus one to three GETs for a list, two
  for an issue with comments. Tokens are not reused between requests and nothing is cached.
  There is no rate limit of TRACE's own on these reads; a signed-in user can spend the
  installation's GitHub rate limit.
- Only open issues are listed; a closed issue can be fetched by number. There is no search,
  no filter by label, and comments beyond the first 30 are not reachable.
- A list is a moving target: an issue opened between two pages shifts the rest by one, so an
  issue can appear twice or be skipped. Good enough for picking an issue by hand.
- Because nothing is stored, a plan made later cannot yet say which version of the issue it
  read. M2.2 and M2.3 must store that snapshot.
- The 409 for a missing permission is only an API answer so far. No screen shows it until the
  plan UI exists (M2.5).
