# Deploying TRACE

**Status: nothing has been deployed.** The container images and a full local stack built from
them have been run and tested. The Azure template in `infra/azure/main.bicep` is compiled in
CI but has never been deployed, so expect to fix details on the first real run. Creating these
resources costs money (a small PostgreSQL server, three small container apps, a registry and
logs run all month).

## What runs where

| Piece | Image | Exposure |
|---|---|---|
| Web | `trace-web` (nginx: static app, forwards `/api` and `/health` to the API) | Public HTTPS — the only public entry |
| API | `trace-api` | Internal only |
| Worker | `trace-worker` | No ingress |
| Migrations | `trace-api --migrate` | A job you start once per release |
| Database | Azure Database for PostgreSQL 17 with `vector` allow-listed | Azure services only, TLS required |

Because the browser only talks to the web address, the session cookie is first-party and the
GitHub App's callback and webhook URLs both live under that one address.

## Try the images locally first

```sh
cp .env.example .env
docker compose --env-file .env -f infra/docker-compose.stack.yml up --build
# http://localhost:8080
```

This runs the production images in the `Staging` environment with the fake models and the
demo repository, so it needs no keys.

## Before you start

1. An Azure subscription and the Azure CLI (`az login`).
2. An Anthropic API key and a Voyage API key.
3. A registered GitHub App ([github-app-setup.md](github-app-setup.md)). You will not know the
   public address until step 4 below; create the app with a placeholder address and fix its
   three URLs afterwards.

## Deploy

Choose a prefix (3–12 lowercase letters or digits) and a region. Put the secret values in
shell variables or a parameters file that is **not** committed.

```sh
RG=trace-rg; PREFIX=trace; LOCATION=westeurope
az group create -n $RG -l $LOCATION

# 1. Registry and database only (no images exist yet).
az deployment group create -g $RG -f infra/azure/main.bicep \
  -p namePrefix=$PREFIX deployApps=false \
     postgresAdminPassword="$PG_PASSWORD" anthropicApiKey="$ANTHROPIC_API_KEY" voyageApiKey="$VOYAGE_API_KEY" \
     gitHubClientId="$GH_CLIENT_ID" gitHubClientSecret="$GH_CLIENT_SECRET" \
     gitHubPrivateKey="$GH_PRIVATE_KEY" gitHubWebhookSecret="$GH_WEBHOOK_SECRET"

# 2. Build and push the three images (run from the repository root).
ACR=$(az deployment group show -g $RG -n main --query properties.outputs.registryName.value -o tsv)
TAG=$(git rev-parse --short HEAD)
az acr build -r $ACR -t trace-api:$TAG    -f src/Trace.Api/Dockerfile .
az acr build -r $ACR -t trace-worker:$TAG -f src/Trace.Worker/Dockerfile .
az acr build -r $ACR -t trace-web:$TAG    -f apps/web/Dockerfile .

# 3. Everything, with the same parameters plus the image tag.
az deployment group create -g $RG -f infra/azure/main.bicep \
  -p namePrefix=$PREFIX deployApps=true imageTag=$TAG \
     postgresAdminPassword="$PG_PASSWORD" anthropicApiKey="$ANTHROPIC_API_KEY" voyageApiKey="$VOYAGE_API_KEY" \
     gitHubClientId="$GH_CLIENT_ID" gitHubClientSecret="$GH_CLIENT_SECRET" \
     gitHubPrivateKey="$GH_PRIVATE_KEY" gitHubWebhookSecret="$GH_WEBHOOK_SECRET"

# 4. Apply database migrations, then find the address.
az containerapp job start -g $RG -n $PREFIX-migrate
az deployment group show -g $RG -n main --query properties.outputs.webUrl.value -o tsv
```

The API and worker start before the migration job has run on a first deployment and will
report "not ready" until it finishes; that is expected.

5. In the GitHub App's settings, set Homepage, Callback (`<address>/api/v1/auth/callback`)
   and Webhook (`<address>/api/v1/github/webhooks`) to the address from step 4.
6. Open the address, sign in with GitHub, and queue an index of one repository
   (`POST /api/v1/repositories/<id>/index-jobs`).

## Releasing a new version

Push new image tags (step 2), start the migration job with the new tag (redeploy step 3 with
the new `imageTag`, then step 4). Migrations must stay backward compatible with the previous
version for the minutes both versions run.

## What production does differently from local development

- `ASPNETCORE_ENVIRONMENT=Production`: the fake models and demo seeding are refused; a missing
  provider or key stops the affected feature with a clear error.
- The demo repository is not seeded, so anonymous callers see no repositories. Asking
  questions requires sign-in.
- Jobs run only in the worker (`Jobs:ProcessInThisHost` is off).
- Migrations run only through the migration job, never at startup.
- Session cookies are `Secure`; their encryption keys are stored in the database so sessions
  survive restarts and work across API instances.

## Not done yet

- A deployment pipeline: every step above is manual.
- A custom domain and certificate (Container Apps provides an HTTPS address out of the box).
- Network isolation: the database accepts connections from any Azure service (with TLS and a
  password) rather than only from a private network.
- Key Vault: secrets are held in the Container Apps secret store.
- Telemetry beyond container logs in Log Analytics; no alerts; no OpenTelemetry yet.
- Backups beyond the server's 7-day automatic backups; no restore drill.
- Load, cost and latency have never been measured.
