# Your tasks

These are the steps only you can do: they need your GitHub account, your payment details or
your Azure subscription. Parts 1–4 give you the first real run on your own machine. Part 5
(Azure) is optional and comes last.

None of the real services has been called yet, so expect something in part 3 or 4 to fail the
first time. When it does, copy the error from the API terminal and bring it back.

Never commit any of the values below, and don't paste them into a chat.

## Part 1: get two API keys (about 10 minutes)

- [ ] **Anthropic key.** Create one at https://console.anthropic.com (API keys). It needs a
      payment method or credit. Answers questions.
- [ ] **Voyage key.** Create one at https://dashboard.voyageai.com (API keys). Used when
      indexing and searching code.

Keep both somewhere safe, such as a password manager.

Cost guard already in place: one question may use at most 300,000 tokens, and all questions
together at most 3,000,000 tokens per 24 hours.

## Part 2: register the GitHub App (about 5 minutes)

GitHub → Settings → Developer settings → GitHub Apps → **New GitHub App**.

- [ ] Fill in the form:

  | Field | Value |
  |---|---|
  | GitHub App name | Any unique name, e.g. `trace-<yourname>` |
  | Homepage URL | `http://localhost:5173` |
  | Callback URL | `http://localhost:5173/api/v1/auth/callback` |
  | Expire user authorization tokens | Leave on |
  | Request user authorization (OAuth) during installation | Off |
  | Webhook → Active | **Off** (GitHub cannot reach your machine) |
  | Repository permissions → Contents | Read-only |
  | Repository permissions → Metadata | Read-only |
  | Everything else | No access |
  | Where can this app be installed? | Only on this account |

- [ ] On the app's page, copy the **Client ID**.
- [ ] Choose **Generate a new client secret** and copy it.
- [ ] Choose **Generate a private key**. A `.pem` file downloads; note where it is.
- [ ] Choose **Install App**, pick your account, and select one small repository to start
      with. Use a personal one, not anything belonging to an employer.

With the webhook off, a repository is re-indexed only when you ask (part 4). Automatic
re-indexing on push starts working once TRACE has a public address (part 5).

## Part 3: start TRACE with the real services

You need Docker Desktop running, plus .NET SDK 10 and Node.js 24.

- [ ] Start the database (from the repository root, once):

  ```powershell
  if (-not (Test-Path .env)) { Copy-Item .env.example .env }
  docker compose --env-file .env -f infra/docker-compose.yml up -d
  ```

- [ ] In one PowerShell window, set the values and start the API. They last only for this
      window, so nothing is written to disk.

  ```powershell
  $env:Chat__Provider       = 'anthropic'
  $env:ANTHROPIC_API_KEY    = '<your Anthropic key>'
  $env:Embeddings__Provider = 'voyage'
  $env:VOYAGE_API_KEY       = '<your Voyage key>'
  $env:GitHub__ClientId     = '<Client ID>'
  $env:GitHub__ClientSecret = '<client secret>'
  $env:GitHub__PrivateKey   = Get-Content -Raw 'C:\path\to\your-app.private-key.pem'

  dotnet run --project src/Trace.Api --launch-profile http
  ```

  Locally the API also runs the background jobs, so you do not need to start the worker.

- [ ] In a second window, start the web app:

  ```powershell
  cd apps/web
  npm ci
  npm run dev
  ```

- [ ] Open http://localhost:5173.

## Part 4: the first real run

- [ ] Choose **Sign in with GitHub** and approve. You should land back on TRACE, signed in,
      with the repository from part 2 in the list.
- [ ] Index it: under **Repositories**, press **Index** next to your repository and wait for
      "Indexing finished".
- [ ] Pick your repository, and ask a question you already know the answer
      to. For example, "Where is authentication handled?"
- [ ] Check the answer:
  - Is it correct?
  - Does each piece of evidence show the right file and lines?
  - Roughly how long did it take?
- [ ] Ask one question the repository cannot answer. The right result is "not enough
      evidence", not a made-up answer.
- [ ] Look at the usage pages of the Anthropic and Voyage dashboards to see what it cost.

The demo repository (`trace-demo/sample-repo`) was indexed with the fake provider. Ignore it
for this run, or index it again the same way before asking it anything.

### What to bring back

- Whether sign-in, indexing and the question each worked.
- Any error text from the API window or the browser.
- How good the answer and its evidence were.
- The rough cost and time of one question.

## Part 5: Azure (optional, costs money)

Do this only after part 4 works, so that any problem is an Azure problem and not something
else. A small database, three small apps, a registry and logs run all month.

- [ ] Install the Azure CLI and run `az login`.
- [ ] Follow [deployment.md](deployment.md), steps 1–4. The template has never been deployed,
      so expect to fix details; bring back any error.
- [ ] In the GitHub App's settings, replace `http://localhost:5173` with the new address in
      the Homepage and Callback URLs.
- [ ] Turn the webhook on: URL `<address>/api/v1/github/webhooks`, a long random secret
      (the same value you gave the deployment as `gitHubWebhookSecret`), and subscribe to
      the **Push** event.
- [ ] Sign in at the new address and repeat part 4 there.

## Rules that still apply

- Do not index code, data or tickets that belong to an employer or a client.
- With the real providers on, indexed source text is sent to Voyage and retrieved passages
  are sent to Anthropic.
- If a key ever ends up in a file, a commit or a chat, revoke it and create a new one.
