# ADR-020: History lists what the caller asked, not what the caller may read

Status: Accepted · 2026-11-23

## Context

Until this step an investigation or a plan could only be reached by someone holding its ID,
and the web app forgot the ID on reload. M3.1 adds a list: `GET /api/v1/investigations`.

A list is a different thing from a read. Reading by ID has one rule (ADR-002): a caller may
read an investigation if the caller may see its repository; anything else is a 404. Applying
that same rule to a list would hand every caller every question ever asked about every
repository they can see. For a private repository that is a product decision nobody has
made (teams are not in v0.1). For the public demo repository it is a leak: everyone can see
it, anonymous callers may ask it questions when `Auth:AllowAnonymousWrites` is on, and a
question is free text typed by a person, which may be personal.

What was already stored: `investigations.user_id`, set from the session of the signed-in
user who asked and null for an anonymous request. Nothing recorded which anonymous browser
asked.

## Decision

### Who is listed what

The list is **the caller's own** investigations. A row is listed only if both hold, and both
are part of every query:

1. **The caller asked for it.**
   - Signed in: `user_id` is the caller.
   - Not signed in: the row has no user and its `visitor_key` matches the caller's visitor
     cookie (below). A caller without the cookie is listed nothing.
2. **The caller may see its repository now** (the same definition as everywhere:
   `VisibleRepositories`). A user who lost access on GitHub, or whose installation was
   suspended, no longer sees those rows; they come back if access does.

People with access to the same repository do **not** share history. Sharing is by address.

### What "shared with someone who has access" means

Every investigation and plan has an address (ADR-021). Reading by ID did not change, so:

- a link works for anyone who may see the repository, whoever asked;
- a link never works for anyone who may not: the API answers 404 and the page shows "not
  found", the same as for an ID that does not exist;
- opening a link does not put the item in the opener's history.

So an investigation on the demo repository is readable by anyone who has its ID (a UUIDv7:
unguessable in practice, not a secret), as it was before this step. The list is what no
longer exists for strangers.

### Anonymous visitors: a visitor cookie

The alternatives for a caller who is not signed in were an empty list (or 401), or a list
limited to what that browser asked. The second was chosen because Development, the local
stack and the browser tests all run without sign-in, and history that works only with a
GitHub App configured would be untested where the project is actually run.

- When an anonymous caller is allowed to create an investigation or a plan and does, the
  response sets `trace_visitor`: 32 random bytes as hex, `HttpOnly`, `SameSite=Lax`, `Secure`
  outside Development, path `/api/v1`, 180 days. A refused request sets nothing; listing or
  reading never sets it.
- The investigation stores `visitor_key`: the SHA-256 of the cookie value. The cookie itself
  is never stored, so a copy of the database does not contain anything that can be presented
  as a cookie (tested: presenting a stored key lists nothing).
- The cookie is not a credential. It is never read for a signed-in user, it is not consulted
  by reading by ID or by repository visibility, and a forged or malformed one lists nothing.
  All it can do is narrow the list to rows carrying its hash.
- It identifies a browser profile, not a person. Clearing cookies, another browser or
  another device starts an empty history; the rows stay stored and reachable by address.
  Anyone using the same browser profile sees its history, as with any local history.
- Signing in does not adopt the browser's anonymous rows, and signing out shows them again.
  Merging them would attach rows to an account on the strength of a cookie.

The database enforces the shape: `visitor_key` is 64 lowercase hex characters, and a row
cannot have both a user and a visitor key.

### Rows without an owner

Rows asked anonymously before this migration have neither a user nor a visitor key, and so
do rows whose user was deleted (`user_id` is set to null). **They are in nobody's list.**
Nothing can honestly say whose they were. They remain readable by address under the usual
rule. The migration adds a nullable column and gives no existing row a value.

### The endpoint

`GET /api/v1/investigations?limit=&cursor=&repositoryId=&type=` answers
`{ items, nextCursor }`, the envelope of the issue list (ADR-015), with `Cache-Control:
no-store`.

- Order: `created_at` descending, then `id` descending. The ID breaks ties, so the order is
  total and a page boundary can fall between two rows created in the same instant.
- `limit` 1 to 100, default 30.
- `type` is `question` or `issue_to_plan`. `repositoryId` must be a repository the caller can
  see; otherwise 404, the answer every other endpoint gives for such a repository (a filter
  that silently matched nothing would be a second, different answer to the same question).
- A bad `limit`, `type` or empty `repositoryId` is a 400 with field errors; a cursor this API
  did not write is a 400 with `code: invalid_cursor`.
- An item has: investigation ID, type, repository (ID, owner, name), state, outcome
  (`answered`, `planned`, `insufficient_evidence` or null), creation time, the question for a
  question, and for a plan the issue number, the stored issue title and the plan ID. No
  answer, evidence, plan document, tokens or model.

### The cursor is a position and nothing else

The cursor encodes the creation time and ID of the last row of a page (base64url, version
prefix, one accepted spelling per position). It is opaque to clients and validated, but **not
signed and not secret**, because it grants nothing: the owner and visibility conditions are
in the query of every page and are never derived from the cursor. A cursor invented by a
caller or taken from another user selects a different place in the caller's own rows. A
test pages one user with another user's cursors.

### Hidden rows cannot be noticed

Filtering happens in SQL, before the limit. A page is cut after the caller's own rows, and
`nextCursor` is set only when one more of the caller's rows exists (the query asks for
`limit + 1`). Other people's rows therefore neither shorten a page, nor produce a cursor,
nor change the envelope: an empty list is `{"items":[],"nextCursor":null}` for everyone.

### Cost of the query

One query per page. Two partial indexes match it: `(user_id, created_at DESC, id DESC)` where
`user_id` is not null, and the same for `visitor_key`. The stored result JSON is not loaded:
the outcome is read by the database with `jsonb_extract_path_text`. The first index replaces
the plain index on `user_id` that served the foreign key; it serves that too.

## Consequences

- Colleagues with access to one repository cannot see each other's questions in a list. If
  teams want shared history, that is a decision for a later version, with its own rule.
- An anonymous visitor's history lives and dies with a cookie. That is weaker than an
  account and is meant to be: it exists so the demo works, not to identify anyone.
- `trace_visitor` is a second cookie. It is strictly necessary for a feature the visitor
  uses (their own history) and carries no profile, but whoever deploys this publicly with
  anonymous use switched on should mention it wherever cookies are described. With anonymous
  use off (the production default) it is never issued.
- The visitor key is a stable pseudonymous identifier stored with each anonymous row. It is
  not logged and not put on a span.
- Ownerless rows accumulate invisibly: nobody is listed them, and until M3.5 nothing deletes
  them. The retention rule of M4 has to cover them.
- An address on the demo repository is readable by whoever has it. That was already true; a
  list would have made the IDs discoverable, and this decision is what prevents that.

## Not verified

- Tested with a stub of GitHub, the fixture repository and the fake model only. No real
  sign-in was used.
- The query plan was not inspected and nothing was measured: the indexes are chosen by
  reasoning about the query, on tables of a few rows.
- `Secure` on the visitor cookie outside Development is set by code that no test runs over
  HTTPS.
