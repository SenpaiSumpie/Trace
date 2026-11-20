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
| Metadata | Read-only (mandatory) |

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
3. Index one: `POST /api/v1/repositories/<id>/index-jobs` (the web UI does not have a button
   for this yet). After that, pushes to its default branch re-index it automatically if
   webhooks are active.

## What TRACE can and cannot do with this app

- It can read file contents and metadata of the selected repositories. It cannot write,
  open issues or pull requests, or read anything else.
- Uninstalling the app, or removing a repository from it, deletes that repository and
  everything indexed from it from TRACE.
- With real model providers configured, indexed source is sent to the embedding provider and
  retrieved passages to the chat provider.
