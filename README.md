# Webhook Inspector

Sign in with GitHub, create an endpoint and get a unique URL. Every HTTP request sent to that URL is stored and shows up live in the browser, where it can be filtered, inspected byte for byte, exported as `curl` or replayed to your own server. Useful when building against GitHub, Stripe or Slack webhooks and you want to see exactly what they send.

**[Try the demo](https://ca-webhook-inspector.blueriver-b0d3312f.norwayeast.azurecontainerapps.io/demo)** (made-up data, runs in your browser, no sign-in) or [sign in](https://ca-webhook-inspector.blueriver-b0d3312f.norwayeast.azurecontainerapps.io) to create real endpoints.

![Requests arriving live in the inspector](docs/images/live.gif)

| Light | Dark | Mobile |
|---|---|---|
| ![Inspector in light mode](docs/images/inspector-light.png) | ![Inspector in dark mode](docs/images/inspector-dark.png) | ![Request detail on a phone](docs/images/mobile.png) |

## Highlights

- **Correct under concurrency.** Quotas are enforced under a per-endpoint row lock, tested with parallel requests against a real Postgres ([ADR 0005](docs/decisions/0005-quota-under-endpoint-row-lock.md)).
- **Live without losing anything.** SignalR only says "something arrived"; the database is the source of truth, and the UI catches up through keyset pagination after a disconnect ([ADR 0003](docs/decisions/0003-database-as-source-of-truth.md), [ADR 0010](docs/decisions/0010-live-view-contract.md)).
- **Captured data never reaches telemetry.** A trace processor strips everything but an allowlist on the capture route, and a script checks the live logs for a planted secret ([live secret check](#live-secret-check)).
- **Replay without SSRF.** A stored request can be sent again to any public URL, but never to `localhost`, private networks or the cloud metadata service, whatever the notation, DNS answer or redirect. The address is checked at connect time, so DNS rebinding has no gap to use ([ADR 0013](docs/decisions/0013-replay-with-ssrf-protection.md)).
- **Isolation between users.** Every query is scoped to the signed-in user, and another user's resources answer `404`, covered by tests for every route.
- **Measured, not guessed.** A load test found the breaking point of one small replica, and load shedding turned 1760 failures into clean `503` responses ([results](#load-test)).
- **Cheap to run.** Scale to zero, a burstable database, an ingestion cap on logs and a budget alert.

## Architecture

```mermaid
flowchart LR
  sender["Webhook sender<br/>(GitHub, Stripe, curl)"]
  user["Browser<br/>React SPA"]
  gh["GitHub OAuth"]

  subgraph aca["Azure Container Apps environment"]
    app["ca-webhook-inspector<br/>ASP.NET Core 10<br/>capture, API, SignalR hub, SPA"]
    migrate["Migration job<br/>(EF bundle, per deploy)"]
    cleanup["Cleanup job<br/>(hourly, 7 day retention)"]
  end

  db[("PostgreSQL 17<br/>Flexible Server")]
  mon["Log Analytics +<br/>Application Insights"]

  subgraph ci["GitHub Actions"]
    build["CI: tests, e2e,<br/>image build"]
    deploy["Deploy via OIDC<br/>managed identity"]
  end
  ghcr["GHCR image"]

  sender -- "ANY /h/{token}/..." --> app
  user -- "/api, cookie + XSRF" --> app
  user <-. "WebSocket /hubs/requests" .-> app
  user -- "sign in" --> gh
  gh -- "callback" --> app
  app --> db
  migrate --> db
  cleanup --> db
  app -- "redacted traces, logs" --> mon
  app -. "replay: public IPs, ports 80/443" .-> replayTarget["Your server<br/>(replay target)"]
  build --> ghcr --> deploy
  deploy --> migrate
  deploy --> app
```

One ASP.NET Core app serves the capture route, the JSON API, the SignalR hub and the built React app from the same origin, so the session cookie never has to cross origins. A capture request goes through rate limiting and load shedding, has its body read with a size limit before any database work, and is stored in one short transaction that locks the endpoint row and checks the quota. After the commit the owner's browser gets a small notification and fetches the details itself. Migrations and retention cleanup run as separate Container Apps jobs, so the app never changes the schema or deletes data on its own. The design and its trade-offs are in [docs/PLAN.md](docs/PLAN.md) and the [decision records](docs/decisions).

## Tech stack

- **Backend:** C#, ASP.NET Core (.NET 10), EF Core with PostgreSQL, SignalR
- **Frontend:** React, TypeScript, Vite, TanStack Query
- **Auth:** GitHub OAuth with cookie authentication
- **Tests:** xUnit with Testcontainers (real Postgres), Vitest, Playwright, k6
- **Cloud:** Azure Container Apps, Azure Database for PostgreSQL, Bicep, GitHub Actions with OIDC

## Running locally

Requirements: .NET 10 SDK, Node.js 24, Docker.

```bash
# 1. Start Postgres
docker compose up -d

# 2. Create the database schema
dotnet tool restore
dotnet ef database update --project src/WebhookInspector.Api

# 3. Start the API (http://localhost:5080)
dotnet run --project src/WebhookInspector.Api

# 4. Start the frontend (http://localhost:5173)
cd web
npm install
npm run dev
```

### GitHub sign-in for local development

1. Create an OAuth app (not a GitHub App, see [ADR 0011](docs/decisions/0011-github-oauth-app-for-sign-in.md)) at <https://github.com/settings/developers> with
   - Homepage URL: `http://localhost:5173`
   - Redirect URI: `http://localhost:5173/signin-github` (wildcard matching and device flow off)
2. Store its credentials as user secrets (never in `appsettings`):

   ```bash
   dotnet user-secrets set "Authentication:GitHub:ClientId" "<client id>" --project src/WebhookInspector.Api
   dotnet user-secrets set "Authentication:GitHub:ClientSecret" "<client secret>" --project src/WebhookInspector.Api
   ```

3. Open <http://localhost:5173/auth/login>. Without these settings `/auth/login` returns `503`; everything else still works.

Browsers treat `http://localhost` as a secure context, so the `Secure` cookies work locally without HTTPS.

The demo at <http://localhost:5173/demo> needs neither the API nor a database.

Send a request to an endpoint's capture URL:

```bash
curl -X POST "http://localhost:5080/h/<token>/github?x=1" -H "Content-Type: application/json" -d '{"hello":"world"}'
```

## API

All `/api` routes need a signed-in user. Every non-GET request under `/api` and `/auth` needs the antiforgery token from `GET /api/antiforgery` in the `X-XSRF-TOKEN` header. Another user's resources return `404`.

| Method | Route | Purpose |
|---|---|---|
| ANY | `/h/{token}/{**path}` | Public capture route (GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS) |
| GET | `/auth/login?returnUrl=` | Sign in with GitHub |
| POST | `/auth/logout` | Sign out |
| GET | `/api/antiforgery` | Issue the antiforgery token |
| GET | `/api/me` | Current user |
| GET, POST | `/api/endpoints` | List with usage / create (max 10) |
| PATCH, DELETE | `/api/endpoints/{id}` | Rename / delete with all requests |
| POST | `/api/endpoints/{id}/rotate-token` | New capture URL; the old one stops working |
| DELETE | `/api/endpoints/{id}/requests` | Clear history |
| GET | `/api/endpoints/{id}/requests?cursor=&limit=&method=&pathPrefix=&from=&to=` | Newest first, keyset pagination |
| GET, DELETE | `/api/requests/{id}` | Full detail / delete one |
| GET | `/api/requests/{id}/body` | Raw body as an attachment |
| GET, POST | `/api/requests/{id}/replays` | Earlier attempts, newest first / replay to `{ "targetUrl": "https://…" }` |
| GET | `/health/live`, `/health/ready` | Liveness / database readiness |
| WS | `/hubs/requests` | SignalR hub (sign-in required): `Subscribe(endpointId)`, then `RequestReceived` events with id, method, path, time, size and cursor (no body, headers or query) |

## Limits

| Limit | Value |
|---|---|
| Body size | 256 KB (`413` above) |
| Headers | 64 headers, 32 KB in total |
| Rate limit | 120 requests/min per capture token, 300/min per source IP (`429`) |
| Quota per endpoint | 1,000 requests and 50 MB of body bytes (`429`) |
| Replay | Public addresses on ports 80 and 443 only, 5 s timeout, 64 KB of the response kept, 10 per user per minute (`429`), 8 at once (`503`), last 20 attempts per request |

## Tests

```bash
dotnet test          # backend integration tests; Docker must be running (Testcontainers)
cd web && npm test   # frontend unit tests (body decoding, curl export, API client, live updates, demo)
cd web && npm run e2e  # Playwright end-to-end tests against the real app
```

The end-to-end tests build the SPA, start the API in the `Testing` environment with test sign-in enabled, and use the docker-compose database (run `docker compose up -d` and `dotnet ef database update` first). Test sign-in (`POST /test/sign-in`) only exists when the environment is `Testing` **and** `TestSignIn:Enabled` is true; the app refuses to start if the flag is set anywhere else.

## Deployment (Azure)

Everything runs in one resource group, `rg-webhook-inspector` (Norway East), defined in [`infra/main.bicep`](infra/main.bicep):

| Resource | Setting |
|---|---|
| Container App `ca-webhook-inspector` | 0.25 vCPU, 0.5 GiB, 0 to 1 replicas, external HTTPS ingress |
| Container Apps Job `caj-webhook-inspector-migrate` | Manual trigger, runs the EF Core migration bundle from the image |
| Container Apps Job `caj-webhook-inspector-cleanup` | Every hour at :17, deletes requests older than 7 days ([ADR 0009](docs/decisions/0009-cleanup-as-a-scheduled-job.md)) |
| Workbook "Webhook Inspector operations" | Capture traffic by status, latency p50/p95/p99, server errors, cleanup runs, failures that need attention |
| PostgreSQL Flexible Server | Burstable B1MS, 32 GB, Postgres 17 |
| Log Analytics + Application Insights | 0.15 GB/day ingestion cap, 30 days retention |
| Budget | Email at 5 and 20 USD of monthly usage (only a notification, not a cap) |

### Pipelines

- **CI** (`ci.yml`): every push and pull request. Backend tests with Testcontainers, frontend lint/typecheck/tests/build, Playwright end-to-end tests against the real app and Postgres, Bicep build and Docker build.
- **Deploy** (`deploy.yml`): after CI succeeds on `main`. Pushes the image to GHCR, runs the migrate job with the new image, then updates the app ([ADR 0008](docs/decisions/0008-migrations-as-a-separate-job.md)).
- **Infra** (`infra.yml`): manual. Deploys the Bicep and keeps the app on its current image.

Both deploying workflows log in to Azure with OIDC through a managed identity, so no Azure credential is stored in GitHub ([ADR 0012](docs/decisions/0012-managed-identity-for-deploy-login.md)).

### GitHub `production` environment

| Type | Name | Purpose |
|---|---|---|
| Variable | `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` | OIDC login (managed identity `id-github-deploy`) |
| Variable | `BUDGET_ALERT_EMAIL` | Where budget alerts go. Empty skips the budget |
| Secret | `POSTGRES_ADMIN_PASSWORD` | Database administrator password |
| Variable | `GH_OAUTH_CLIENT_ID` | Production GitHub OAuth app. Empty disables sign-in (`503`) |
| Secret | `GH_OAUTH_CLIENT_SECRET` | Production GitHub OAuth app secret |

### First deploy

1. Push to `main`. CI runs, then Deploy builds and pushes the image. Its deploy step warns that the infrastructure does not exist yet; that is expected.
2. Make the image public: GitHub profile → **Packages** → the package → **Package settings** → **Change visibility** → Public. Container Apps pulls it without credentials. The image contains no secrets.
3. Run the **Infra** workflow (Actions → Infra → Run workflow). The first run takes 10 to 15 minutes, mostly for Postgres. The run summary shows the app URL.
4. Create the production GitHub OAuth app with the redirect URI `<app URL>/signin-github`, set `GH_OAUTH_CLIENT_ID` and `GH_OAUTH_CLIENT_SECRET`, and run **Infra** again.
5. Run the live secret check below. **Do not send real webhook data before it passes.**

After that, every push to `main` deploys automatically once CI is green.

### Live secret check

[`scripts/check-telemetry-leak.ps1`](scripts/check-telemetry-leak.ps1) sends a unique secret in the body, headers, query and path to the live service, waits until the app's own log lines for those requests are in Log Analytics, and then searches every table for the secret and the capture token. Run it after the first deploy and after any telemetry change:

```powershell
az login
./scripts/check-telemetry-leak.ps1 -BaseUrl https://<app URL> -Token <capture token of one of your endpoints>
```

Create an endpoint called "leak check" in the live UI and pass the last part of its capture URL as `-Token`.

### Known limitations (v1)

- Postgres accepts connections from any Azure service (Consumption Container Apps have no fixed outbound IP). TLS and the password protect it; private networking is a v2 improvement.
- The app connects as the Postgres administrator. A separate least-privilege role is a v2 improvement.
- Data Protection keys are stored unencrypted in the database (see [ADR 0002](docs/decisions/0002-cookie-authentication.md)).
- The first request after a quiet period has a cold start of a few seconds ([ADR 0007](docs/decisions/0007-scale-to-zero.md)).
- Replay cannot reach `localhost` or ports other than 80 and 443, by design. To replay to your own machine, expose it through a tunnel such as ngrok or Cloudflare Tunnel.

## Load test

[`load/capture.js`](load/capture.js) (k6) sends webhooks to the live capture route at a constant rate and reports stored, rate-limited and failed requests and the latency of stored captures. [`load/notify-latency.mjs`](load/notify-latency.mjs) measures the time from sending a webhook to receiving its live notification over SignalR, the same path the browser uses. Both write JSON to `load/results`.

```powershell
# Capture load (k6 in Docker, nothing to install)
docker run --rm -i -v "${PWD}/load:/load" -e BASE_URL=https://<app URL> -e TOKENS=<token1>,<token2> `
  -e RATE=3 -e DURATION=3m -e LABEL=realistic grafana/k6 run /load/capture.js

# Notification latency, in a second terminal while k6 runs. AUTH_COOKIE is the value of the
# __Host-wi-auth cookie from your browser's dev tools: set it in your own terminal only.
cd load; npm install
$env:BASE_URL='https://<app URL>'; $env:ENDPOINT_ID='<id>'; $env:TOKEN='<token>'; $env:AUTH_COOKIE='<cookie>'
npm run notify-latency
```

The capture route allows 120 requests/min per endpoint and 300/min per source IP, so a test from one machine at the default limits measures realistic traffic, not capacity. For a stress test the limits can be raised temporarily with the GitHub `production` variables `CAPTURE_PERMITS_PER_TOKEN_PER_MINUTE` and `CAPTURE_PERMITS_PER_IP_PER_MINUTE` and an Infra run, and removed again afterwards.

### Results (2026-05-08)

Setup: one Container App replica with 0.25 vCPU and 0.5 GiB, Postgres Flexible Server B1MS, both in Norway East. k6 ran in Docker on a PC in Norway with 1 KB JSON bodies. The rate limits were raised for the test and restored afterwards. Raw results are in [`load/results`](load/results). Latencies include the round trip from the PC to Azure.

| Run | Load | Requests | Stored | Rejected | Failed | Median | p95 | p99 |
|---|---|---|---|---|---|---|---|---|
| realistic | 3/s for 3 min, 8 endpoints | 540 | 540 | 0 | 0 | 44 ms | 113 ms | 165 ms |
| stress-10 | 10/s for 2 min, 8 endpoints | 1201 | 1201 | 0 | 0 | 44 ms | 50 ms | 111 ms |
| stress-25 | 25/s for 2 min, 8 endpoints | 3001 | 3001 | 0 | 0 | 44 ms | 50 ms | 59 ms |
| hot | 20/s for 30 s, **one** endpoint | 601 | 601 | 0 | 0 | 43 ms | 108 ms | 417 ms |
| stress-50, before | 50/s for 1 min, 8 endpoints | 2039 | 279 | 0 | **1760** | 8977 ms | 32891 ms | 36116 ms |
| stress-50, after | 50/s for 75 s, 8 endpoints | 3750 | 3044 | 706 (503) | **0** | 1517 ms | 2294 ms | 3655 ms |

Time from sending a webhook to receiving its live notification over SignalR (`notify-latency.mjs`):

| Condition | Notified | Median | p95 | Max |
|---|---|---|---|---|
| Idle | 40 of 40 | 37 ms | 40 ms | 47 ms |
| During stress-50 (after) | 55 of 55 stored | 584 ms | 1592 ms | 1700 ms |

**Is the per-endpoint lock with `COUNT` sufficient?** Yes. With 20 requests/s all waiting for the same endpoint row lock, every request was stored with a median of 43 ms; the quota tests show the count stays correct under contention. The lock is not the bottleneck at the throughput one replica can reach.

**What broke at 50/s, and what changed after the first run.** The replica reached 99% CPU. Requests queued while each held a database connection, Npgsql opened connections up to its default pool size of 100, and the B1MS server (`max_connections` = 50, some reserved by Azure) refused or timed out. 1760 of 2039 requests failed after up to 37 seconds; the app log showed `Database.Connection` errors and `TimeoutException`s. Two changes:

1. **Load shedding on the capture route**: at most 16 captures at once, a queue of 50, and `503` with `Retry-After: 2` beyond that. Webhook senders retry on 503. The API and the hub are not affected.
2. **Npgsql pool capped at 20 connections**, safely below the server limit.

**Effect.** At the same 50/s, 3044 of 3750 requests were stored and the other 706 were turned away with 503 within the queue time. There were no server errors (the app log is clean, and Application Insights shows only 200 and 503). CPU was still at 100%, so one 0.25 vCPU replica stores about **40 captures per second**. Live notifications kept arriving for every stored request, within 1.6 s at p95 under full load.

**Limits of this test.** It ran from one machine against one replica, with raised rate limits. In normal operation the per-token (120/min) and per-IP (300/min) limits stop a single sender long before this point. More throughput needs more CPU per replica, or several replicas with a SignalR backplane ([ADR 0004](docs/decisions/0004-one-replica-per-revision.md)). A shorter queue would lower the latency of accepted requests under overload, at the cost of more 503s.
