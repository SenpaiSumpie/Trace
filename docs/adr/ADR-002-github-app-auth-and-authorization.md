# ADR-002: Sign in with GitHub, access taken from GitHub, read-only App tokens

Status: Accepted · 2026-11-20

## Context

TRACE must index private repositories without ever showing one user another user's code
(handoff Sections 2 and 10). It needs to know who a caller is, which repositories they may
see, and it needs to read repository content. The owner chose "Sign in with GitHub" using the
same GitHub App for identity and repository access.

## Options

1. **A separate OIDC identity provider plus the GitHub App.** Keeps identity independent of
   GitHub, but TRACE would then have to map identities to GitHub accounts itself to know who
   may see which repository.
2. **Personal access tokens or OAuth App tokens with `repo` scope.** Simple, but grants far
   more than read access to selected repositories, and the tokens would have to be stored.
3. **One GitHub App** for sign-in (user-to-server OAuth) and for content (installation tokens).

## Decision

Option 3.

- **Identity.** The OAuth web flow with a per-login random `state` bound to the browser by an
  HttpOnly cookie. A user is keyed by GitHub's numeric user ID. The session is an encrypted,
  HttpOnly, `SameSite=Lax` cookie (Secure outside Development) valid for 8 hours, holding only
  the TRACE user ID and login.
- **Access comes from GitHub, at sign-in.** TRACE asks GitHub which installations the user can
  reach and which repositories within them the user can access, and *replaces* the user's
  `repository_access` rows with exactly that. The user token is then discarded; none is stored.
- **One visibility rule**, in `VisibleRepositories`: the public demo fixture is visible to
  everyone; a GitHub repository (public or private) only to a signed-in user with an access
  row, and only while its installation is active. Every endpoint goes through it, and anything
  not visible answers 404.
- **Starting or stopping work requires sign-in** (`Auth:AllowAnonymousWrites` relaxes this; it
  is on in Development so the demo runs without a GitHub App).
- **Reading content.** A short-lived installation token is requested per index run, restricted
  to `contents: read` and `metadata: read` on that one repository, and used to read the tree
  and blobs at the branch's exact commit. Nothing is cloned or executed. The model never sees
  any token.
- **Webhooks** are accepted only with a valid HMAC-SHA256 signature over the raw body, checked
  before the payload is parsed. Suspending an installation hides its repositories at once;
  removing a repository or uninstalling the app **deletes** the repository and everything
  indexed from it. A push to the default branch queues a re-index, deduplicated by delivery ID.
- **Secrets are kept out of the index.** A file that matches a credential pattern is not
  indexed at all (`SecretScanner`).

## Consequences

- Access removed on GitHub lasts in TRACE until the user's session ends (at most 8 hours),
  because access is only re-read at sign-in. Uninstalling, suspending or removing a repository
  takes effect immediately through webhooks.
- Anyone with access to a repository sees every investigation on it, not only their own.
- Sessions do not survive a restart or span instances until the cookie encryption keys are
  persisted (a deployment task).
- Cross-site request forgery relies on `SameSite=Lax`, JSON request bodies and no CORS; there
  is no separate anti-forgery token.
- The secret scanner is pattern-based: it misses unusual credentials and skips some innocent
  files.

## Revisit trigger

Needing access revocation faster than the session length (re-check GitHub per request, or
keep a refresh token); teams wanting private investigations; or an enterprise customer needing
SSO that is not GitHub.
