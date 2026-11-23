# Registering the TRACE GitHub App

TRACE needs one GitHub App, which you register in your own GitHub account. It is used for
sign-in and for read-only access to the repositories people choose. Nothing here can be done
by the code; it takes about five minutes.

Replace `https://trace.example.com` with the address the TRACE web app is served from. For
local development that is `http://localhost:5173`.

## 1. Create the app

GitHub → Settings → Developer settings → GitHub Apps → **New GitHub App**.

| Field | Value |
|---|---|
| GitHub App name | Any unique name, e.g. `trace-<yourname>` |
| Homepage URL | `https://trace.example.com` |
| Callback URL | `https://trace.example.com/api/v1/auth/callback` |
| Expire user authorization tokens | Leave on |
| Request user authorization (OAuth) during installation | Off |
| Webhook → Active | On (off for local development unless you use a tunnel) |
| Webhook URL | `https://trace.example.com/api/v1/github/webhooks` |
| Webhook secret | A long random string; you will give the same value to TRACE |

**Repository permissions** — set exactly these, everything else "No access":

| Permission | Access |
|---|---|
| Contents | Read-only |
| Issues | Read-only |
| Metadata | Read-only (mandatory) |

Issues is needed to list and fetch issues (milestone M2). If your app was created before that
permission was listed here, see [Adding the Issues permission](#adding-the-issues-permission-to-an-existing-app).

**Subscribe to events:** Push. (Installation events are always delivered.)

**Where can this GitHub App be installed?** "Only on this account" unless you want others to
install it.

## 2. Collect four values

On the app's page after creating it:

1. **Client ID** (starts with `Iv`).
2. **Client secret** — "Generate a new client secret".
3. **Private key** — "Generate a private key"; a `.pem` file downloads.
4. The **webhook secret** you chose.

## 3. Give them to TRACE

Set these for the API and the worker. Never commit them.

```sh
GitHub__ClientId=Iv...
GitHub__ClientSecret=...
GitHub__WebhookSecret=...
GitHub__PrivateKey="-----BEGIN RSA PRIVATE KEY-----\n...\n-----END RSA PRIVATE KEY-----"
```

The private key may be given on one line with literal `\n` between lines. For local
development use `dotnet user-secrets` in `src/Trace.Api` and `src/Trace.Worker` rather than
environment variables.

## 4. Install it and sign in

1. On the app's page choose **Install App**, pick your account, and select the repositories
   TRACE may read.
2. Open TRACE and choose **Sign in with GitHub**. The repositories you can access through the
   app appear in the repository list.
3. Index one: under **Repositories**, press **Index** next to it. After that, pushes to its default branch re-index it automatically if
   webhooks are active.

## Adding the Issues permission to an existing app

Reading issues needs one more read-only permission. Only you can grant it: the code cannot,
and nothing in TRACE changes the app's settings. There are two steps, and the second is easy
to miss. The names of GitHub's buttons below are from memory and may differ slightly.

**1. Add the permission to the app.**

GitHub → Settings → Developer settings → GitHub Apps → **Edit** next to your app →
**Permissions & events** in the left menu → **Repository permissions** → **Issues** → set
Access to **Read-only** → **Save changes** at the bottom of the page.

Leave everything else as it is. Do not choose "Read and write", and do not subscribe to the
Issues event: TRACE does not use it.

**2. Accept the new permission on every installation.**

Changing an app's permissions does not change what its existing installations allow. Each
account the app is installed on has to accept the request first. GitHub emails the account's
owner, and the prompt is here:

- Your own account: GitHub → Settings → **Applications** → **Installed GitHub Apps** →
  **Configure** next to the app. A banner at the top says the app is requesting updated
  permissions; choose **Review request**, then **Accept new permissions**.
- An organization: the organization's Settings → **GitHub Apps** (under Third-party Access) →
  **Configure** next to the app, then the same banner. It needs an organization owner.

**Until it is accepted**

- Everything that worked before keeps working: sign-in, indexing, questions.
- The two issue endpoints (`GET /api/v1/repositories/{id}/issues` and
  `.../issues/{number}`) answer **409** with `"code": "issues_permission_missing"` and the
  detail "The GitHub App installation for this repository has not granted read access to
  issues. Its owner must accept the app's updated permissions on GitHub." It is not a 500 and
  nothing is retried.
- There is no screen for issues yet, so the web app shows nothing about this; the answer is
  only visible through the API until the plan UI is built.

**Check that it worked** (after signing in to TRACE in the browser, open this address in the
same browser, with the ID of one of your repositories from `/api/v1/repositories`):

```text
http://localhost:5173/api/v1/repositories/<repository id>/issues
```

A JSON list of the repository's open issues means the permission is in place. A 409 with
`issues_permission_missing` means step 2 is still open for that installation. A 409 with
`issues_disabled` means the repository has its Issues feature switched off on GitHub.

**This has never been run against real GitHub issues.** It was built and tested against a
stub that answers in GitHub's documented formats. The first time you try it is the first time
it meets the real service, so expect a detail to be off and bring back what the API answered.

## What TRACE can and cannot do with this app

- It can read file contents, issues (with their comments) and metadata of the selected
  repositories. It cannot write: it cannot push, open or edit issues or pull requests,
  comment, or read anything else. Every request about issues is a GET, and a test fails if
  that changes.
- Issue text is fetched when it is asked for and is not stored by TRACE.
- Uninstalling the app, or removing a repository from it, deletes that repository and
  everything indexed from it from TRACE.
- With real model providers configured, indexed source is sent to the embedding provider and
  retrieved passages to the chat provider.
