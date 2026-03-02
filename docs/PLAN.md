# Webhook Inspector: Project Plan

A web service where a user signs in with GitHub, creates endpoints and gets a unique URL for each. Every HTTP request sent to that URL is stored and shown live in the browser, where it can be filtered, inspected and exported as `curl`.

Purpose: a general software development project for the CV, showing C#/.NET, a relational database, authentication and access control, real-time updates, testing and cloud deployment on Azure.

---

## 1. Scope

### Version 1 (the CV project)

- Sign in with GitHub
- Create, rename and delete endpoints; rotate an endpoint's capture URL
- Public capture route with size limits, rate limits and quotas
- Private request history per user, with cursor pagination
- Filter by method, path prefix and time
- Detail view: headers, raw query, body as text, formatted JSON or hex, raw download
- Copy a request as `curl`
- Live view of the newest requests via SignalR
- Automatic deletion at the next cleanup run after 7 days
- Deployed on Azure with Bicep, CI/CD and monitoring
- Automated tests, a load test and documented decisions

### Version 2 (only after v1 is finished)

- Replay a stored request to another URL (with SSRF protection, see section 13)
- Full-text search in bodies
- Custom response per endpoint (status code, body, delay)
- Signature verification for known providers (GitHub, Stripe)
- Managed identity for the database and Key Vault for secrets
- Several replicas with Azure SignalR Service

### Not in scope

Teams and sharing, payment, a mobile app, real-time forwarding/proxying.

---

## 2. Architecture

```
            webhook sender                     browser (React SPA)
                  |                              |          ^
   ANY /h/{token}/{**path}             /api/* (cookie)   SignalR /hubs/requests
                  |                              |          |
          +-------v------------------------------v----------+------+
          |              ASP.NET Core app (one container)          |
          |  capture pipeline  |  management API  |  SignalR hub   |
          |  (no auth, limits) | (auth, CSRF)     |  (auth)        |
          +---------------------------+----------------------------+
                                      |
                              PostgreSQL (EF Core)
                                      ^
                    cleanup job (Container Apps Job, cron)
```

- One deployable container: the API serves the built React files from `wwwroot`, so frontend and API share one origin.
- **The database is the source of truth.** SignalR only sends notifications. A lost notification must never mean lost data.
- The capture route and the management API are separate pipelines: the capture route never reads cookies, never needs antiforgery and has its own rate-limit policy.

---

## 3. Tech stack

| Area | Choice |
|---|---|
| Backend | C#, ASP.NET Core (.NET 10) |
| Database | PostgreSQL, EF Core with migrations (Npgsql) |
| Real-time | ASP.NET Core SignalR, in-process |
| Frontend | React, TypeScript, Vite, TanStack Query, `@microsoft/signalr` |
| Auth | GitHub OAuth (`AspNet.Security.OAuth.GitHub`), cookie authentication |
| Backend tests | xUnit, `WebApplicationFactory`, Testcontainers (real Postgres) |
| Frontend tests | Vitest, Playwright (one end-to-end flow) |
| Load test | k6 |
| Container | Docker (multi-stage: Node builds the SPA, .NET publishes the API) |
| Hosting | Azure Container Apps (Consumption plan) |
| Database hosting | Azure Database for PostgreSQL Flexible Server, Burstable B1MS |
| Image registry | GitHub Container Registry (free, not tied to the 12-month offer) |
| Infrastructure | Bicep |
| CI/CD | GitHub Actions, Azure login with OIDC (federated credentials) |
| Monitoring | OpenTelemetry with the Azure Monitor exporter (Application Insights) |

---

## 4. Repository layout

```
webhook-inspector/
  src/
    WebhookInspector.Api/          ASP.NET Core app (capture, API, hub, cleanup command)
  tests/
    WebhookInspector.Api.Tests/    integration tests with Testcontainers
  web/                             React + Vite
  infra/
    main.bicep
    modules/
  load/
    capture.js                     k6 script
  docs/
    PLAN.md
    decisions/                     short ADRs (0001-postgres.md, ...)
    architecture.png
  .github/workflows/
    ci.yml
    deploy.yml
    infra.yml
  docker-compose.yml               Postgres for local development
  Dockerfile
  README.md
```

---

## 5. Data model

**User**
| Column | Type | Note |
|---|---|---|
| Id | uuid | PK |
| GitHubId | bigint | unique |
| Login | text | |
| CreatedAt | timestamptz | |

**Endpoint**
| Column | Type | Note |
|---|---|---|
| Id | uuid | PK, internal, used in the management API |
| OwnerId | uuid | FK User |
| Name | text | |
| CaptureToken | text | unique; 128 random bits, base64url; can be rotated |
| CreatedAt | timestamptz | |

No stored usage counters. Usage (request count, body bytes) is computed from `CapturedRequest`. With at most 1,000 rows per endpoint (see the quota in section 7) this is expected to be sufficient; the load test in phase 6 verifies it.

**CapturedRequest**
| Column | Type | Note |
|---|---|---|
| Id | uuid | PK |
| EndpointId | uuid | FK Endpoint, cascade delete |
| ReceivedAt | timestamptz | UTC |
| Method | text | |
| Path | text | the part after the token |
| RawQuery | text | kept exactly, including repeated parameters |
| Headers | jsonb | array of `{name, value}`. Preserves header names and multiple values as exposed by ASP.NET Core. Original wire formatting and header order are not guaranteed (the ingress proxy and Kestrel may normalise them) |
| ContentType | text | |
| Body | bytea | at most the body limit |
| BodySize | int | |
| SourceIp | inet | from forwarded headers set by the Container Apps ingress |

**DataProtectionKeys**: table from `PersistKeysToDbContext`, so login cookies survive restarts and deploys.

**Indexes**
- `CapturedRequest (EndpointId, ReceivedAt DESC, Id DESC)`: list and pagination
- `CapturedRequest (ReceivedAt)`: retention cleanup
- `Endpoint (CaptureToken)` unique, `User (GitHubId)` unique

---

## 6. API

### Capture (public)
`GET | POST | PUT | PATCH | DELETE | HEAD | OPTIONS  /h/{captureToken}/{**path}`

- Unknown token: `404`
- Body over the limit: `413`
- Rate limit hit: `429`
- Quota full: `429` with a short explanation in the body
- Success: `200` with `{"ok":true}`. For `HEAD`: `200` with no body

### Management (cookie auth + antiforgery header on every non-GET)
| Method | Route | Purpose |
|---|---|---|
| GET | `/api/antiforgery` | issues the antiforgery token (see section 7) |
| GET | `/api/me` | current user |
| GET | `/api/endpoints` | list own endpoints with usage |
| POST | `/api/endpoints` | create |
| PATCH | `/api/endpoints/{id}` | rename |
| DELETE | `/api/endpoints/{id}` | delete endpoint and its requests |
| POST | `/api/endpoints/{id}/rotate-token` | new capture URL, old one stops working |
| DELETE | `/api/endpoints/{id}/requests` | clear history |
| GET | `/api/endpoints/{id}/requests?cursor=&limit=50&method=&pathPrefix=&from=&to=` | metadata + short body preview, cursor on `(ReceivedAt, Id)` |
| GET | `/api/requests/{id}` | full detail |
| GET | `/api/requests/{id}/body` | raw body as a download |
| DELETE | `/api/requests/{id}` | delete one |

### Auth
`GET /auth/login`, GitHub callback handled by the middleware, `POST /auth/logout`.

### Health
`GET /health/live` (process is up), `GET /health/ready` (database reachable).

### SignalR hub `/hubs/requests` (auth required)
- Client calls `Subscribe(endpointId)`. Server checks ownership before adding the connection to the group.
- Server sends `RequestReceived {endpointId, requestId, method, path, receivedAt, bodySize}` after the database transaction has committed. No body or header values in the message.

---

## 7. Security requirements (v1)

These must be in place **before** the first public deploy (phase 3).

**Capture route**
- Body limit 256 KB, header count and size limits (Kestrel limits)
- Rate limits with the built-in ASP.NET Core rate limiter: per capture token and per source IP
- Quota per endpoint: at most 1,000 stored requests and 50 MB of **body bytes** (metadata such as headers is not counted; headers are bounded separately by the Kestrel limits).
- Capture protocol, followed by **every** insert:
  1. Read the HTTP body and enforce the size limit **before** touching the database, so a slow upload never holds a lock
  2. Start a transaction with `READ COMMITTED`
  3. Lock the endpoint row: `SELECT ... FROM "Endpoint" WHERE "CaptureToken" = @token FOR UPDATE`
  4. In a **separate** statement: `SELECT count(*), coalesce(sum("BodySize"), 0) FROM "CapturedRequest" WHERE "EndpointId" = @id`
  5. Insert only if the new request fits both limits, otherwise roll back and return `429`
  6. Commit
  Locking and counting must stay separate statements. Under `READ COMMITTED` the count statement takes a new snapshot after the lock is granted, so it sees the insert of the transaction it waited for. Do not merge them into one query or CTE, which would count from the snapshot taken before the wait.
  Deletes (cleanup, user delete, clear) only remove `CapturedRequest` rows and never lock the endpoint row, so there is no lock-order conflict. Stored requests are never updated, so a concurrent delete can only free space: at worst a capture is rejected because a delete is not yet visible. Deleting the same row twice changes nothing. No counters means nothing can drift.
- At most 10 endpoints per user
- **Nothing sensitive in logs or telemetry.** Header values, bodies, query strings, the capture token and the path after it must not appear in application logs, exception messages, automatic OpenTelemetry HTTP telemetry or platform logs. Concretely:
  - Logs contain endpoint id, method, body size and status only
  - An OpenTelemetry processor rewrites the URL of every `/h/*` request to `/h/{token}` and drops the query string before export
  - Exceptions from the capture pipeline are logged without request data
  - The URL rewrite does not cover separate logs, exceptions or platform logs, so the live check below is required
  - Verified in phase 3, **before the service receives real webhook data**, and again after any telemetry change, by sending a unique test secret in body, header, query and path to the live service and searching Application Insights and Log Analytics for it

**Accounts and access**
- Auth cookie: `HttpOnly`, `Secure`, `SameSite=Lax`. No auth tokens in localStorage
- Antiforgery on every state-changing request: all non-GET `/api` routes **and** `POST /auth/logout`. The SPA calls `GET /api/antiforgery` after sign-in; the server uses `IAntiforgery.GetAndStoreTokens` and returns the request token in a cookie readable by JavaScript (`XSRF-TOKEN`). The SPA sends it back in the `X-XSRF-TOKEN` header
- Every query filters on `OwnerId`. Another user's resource returns `404`, not `403`
- Hub subscriptions check ownership on every new connection (groups are per connection and are not a security boundary on their own)
- Data Protection keys stored in Postgres. In v1 the keys have no separate application-level encryption (they are only covered by the database platform's storage encryption); documented as a known limitation, v2 can protect them with Key Vault

**Showing untrusted content**
- Never render captured content as HTML. No `dangerouslySetInnerHTML`
- Raw body downloads use `Content-Disposition: attachment` and `X-Content-Type-Options: nosniff`
- Content Security Policy: `default-src 'self'; connect-src 'self'; frame-ancestors 'none'`
- UI warning: captured data may contain secrets and is deleted at the next cleanup after 7 days

**Secrets**
- OIDC removes the need for a long-lived Azure credential in GitHub Actions
- The GitHub OAuth client secret and the database password are stored as GitHub environment secrets and passed to Bicep as `@secure()` parameters, ending up as Container Apps secrets. (v2: Key Vault and managed identity)

---

## 8. Real-time design

**Contract for v1:** the live view shows the newest 50 requests matching the active filters. Every stored request is always available through the paginated history. The live list is not promised to contain every request that arrived.

1. Capture request is stored, transaction commits.
2. Server sends `RequestReceived` to the endpoint's group.
3. Client applies the active filters (method, path prefix, time) to the event. If it matches, the client inserts it at the top, deduplicating on `requestId`, and trims the list to 50.
4. On reconnect: client calls `Subscribe` again and **replaces** the first page with fresh data from the API.
5. While connected: client also refetches the first page every 30 seconds, because a notification can be lost even when the connection is up (for example if the insert succeeds but the send fails).
6. Older requests are loaded with the cursor pagination, independently of the live events.

Run with `maxReplicas: 1` in v1. This applies per revision, so during a deploy the old and new revision can overlap briefly. Document that open connections drop during a deploy and that several replicas would need Azure SignalR Service or a Redis backplane.

---

## 9. Retention and cleanup

- A Container Apps Job on a cron schedule (hourly) runs the same image with a `cleanup` argument.
- Deletes requests older than 7 days in batches of 1,000. Requests are therefore deleted at the next run after 7 days, not at an exact time.
- Runs independently of the app, so cleanup still happens when the app has scaled to zero.

---

## 10. Azure resources and cost

| Resource | Setting |
|---|---|
| Resource group | one, e.g. `rg-webhook-inspector` |
| Container Apps environment | Consumption plan, with a Log Analytics workspace that has a daily cap |
| Container App `api` | 0.25 vCPU, 0.5 GiB, `minReplicas: 0`, `maxReplicas: 1`, external ingress on port 8080 |
| Container Apps Job `cleanup` | cron, same image |
| Container Apps Job `migrate` | manual trigger, runs the EF Core migration bundle from the new image |
| PostgreSQL Flexible Server | Burstable B1MS, 32 GB storage, the only server in the subscription |
| Application Insights | workspace-based, with sampling |
| Budget | alert at 5 and 20 USD of usage. Alerts only notify: they do not stop resources or act as a hard cost cap |

**What the Azure for Students offer covers (check the current terms before you start)**
- 100 USD credit for 12 months, no credit card
- Container Apps: 180,000 vCPU-seconds, 360,000 GiB-seconds and 2 million requests per month, always free. With 0.25 vCPU and 0.5 GiB that is about 200 hours of active time per month
- PostgreSQL: 750 hours of B1MS per month for 12 months, enough for one server running all month

**What can still cost money**
- `minReplicas: 1` (an always-running replica uses more than the free grant)
- A second Postgres server, for example for staging
- Log and telemetry ingestion beyond the free amount
- A custom domain (use the default `azurecontainerapps.io` address)
- PostgreSQL after the first 12 months

**Trade-off with scale to zero:** the first request after a quiet period has a cold start of a few seconds. An open SignalR connection keeps the replica running while someone has the UI open.

---

## 11. CI/CD

**`ci.yml`** (every push and pull request)
- `dotnet build`, `dotnet test` (Testcontainers uses Docker on the GitHub runner)
- `npm ci`, lint, type check, Vitest, `npm run build`
- Playwright end-to-end test against the app and Postgres in Docker

**`deploy.yml`** (push to `main`)
- Build the image, push to GHCR tagged with the commit SHA
- `azure/login` with OIDC (federated credential, role scoped to the resource group)
- Start the `migrate` job with the new image tag and wait for it to succeed (the image contains an EF Core migration bundle)
- Only then update the Container App to the new image tag
- The app never applies migrations at startup. Old and new revisions can run at the same time during a deploy, so every migration must work with both the old and the new code (expand first, remove old columns in a later deploy)

**`infra.yml`** (manual trigger)
- `az deployment group create` with `infra/main.bicep`

---

## 12. Testing

**Backend integration tests (real Postgres via Testcontainers)**
- Each supported method is stored with the right method, path, raw query, headers and body
- Multiple values for the same header and repeated query parameters are preserved
- Binary bodies are stored byte for byte
- Body over the limit gives `413`; unknown token gives `404`; `HEAD` gets no body
- Quota: 50 parallel requests against an endpoint with 10 free slots store exactly 10, the rest get `429`
- Concurrency: parallel captures, user deletes and a cleanup run on the same endpoint never leave it over quota, and computed usage matches the remaining rows
- After token rotation the old token gives `404` and the new one works
- User B gets `404` on user A's endpoint, request list, request detail, body download and delete
- User B cannot subscribe to user A's endpoint on the hub
- A state-changing request (including logout) without the antiforgery header is rejected
- Cleanup deletes only requests older than the cutoff
- A unique secret sent in body, header, query and path never appears in captured logs or in exported OpenTelemetry data (test logger and in-memory exporter). This covers the app; platform logs are checked against the live service in phase 3

**Frontend (Vitest)**
- Body display: detects JSON, text and binary
- `curl` export escapes quotes and special characters correctly
- Live events are deduplicated on id, respect active filters and the list is trimmed to 50

**End-to-end (Playwright)**
- Phase 4: sign in (test auth handler, enabled only in the test environment), create an endpoint, send a request, refresh, open the detail view
- Phase 5: the same flow, but the request appears without refreshing

**Load test (k6), reported in the README**
- State body size, Azure configuration and duration
- Measure error rate, p95 latency of the capture route and time from capture to UI notification
- Write down what you changed after the first run and the effect

---

## 13. Version 2: replay with SSRF protection

Only after v1 is done.

- Only `http` and `https`; optionally only ports 80 and 443
- Validate the IP address **the connection actually uses**, in `SocketsHttpHandler.ConnectCallback`, not a separate DNS lookup beforehand
- Block loopback, private ranges, link-local (including `169.254.169.254`), CGNAT, multicast, IPv6 unique-local and link-local, and IPv4-mapped IPv6 such as `::ffff:127.0.0.1`
- No automatic redirects
- Timeout 5 seconds; response read limit 64 KB
- Header policy: drop `Host`, `Cookie`, `Authorization`, hop-by-hop headers and `Content-Length` (recomputed)
- Store each attempt: target, status, duration, error
- Tests: direct private IPs, DNS names resolving to private IPs, redirect to a private IP, IPv6 forms

---

## 14. Phases and definition of done

Each phase ends with: tests green in CI, a short ADR for any important decision, and you can explain the code without help.

**Phase 0: Setup**
Repo, solution, `docker-compose.yml` with Postgres, CI running empty tests, README skeleton, ADR template.
Done when CI is green.

**Phase 1: Capture with limits (local)**
Data model and first migration, capture route, body and header limits, rate limits, quota check under the endpoint row lock, logging policy.
Done when the capture, quota and concurrency tests pass.

**Phase 2: Accounts and isolation**
GitHub OAuth, cookie settings, antiforgery, Data Protection keys in Postgres, endpoint management, token rotation.
Done when all access-control tests pass.

**Phase 3: First Azure deploy**
Bicep for environment, app, migrate job, Postgres and logs; GHCR; OIDC deploy with migrations before the app update; OpenTelemetry to Application Insights with URL redaction; budget alert.
Done when a live URL exists, sign-in works there, a `curl` to the live capture URL is stored, and the live test secret is found nowhere in Application Insights or Log Analytics. Do not send real webhook data before this check passes.

**Phase 4: Inspection UI**
Endpoint list with usage, request list with pagination and filters, detail view, raw download, `curl` export, delete and clear.
Done when the phase 4 end-to-end test passes.

**Phase 5: Live updates**
Hub with ownership check, notification after commit, filters applied to events, first page replaced on reconnect, periodic refetch while connected.
Done when the hub tests and the phase 5 end-to-end test pass, and a manual test (disconnect, send 120 requests, reconnect) shows a correct newest page with the rest reachable through pagination.

**Phase 6: Cleanup and operations**
Cleanup job, Application Insights dashboards, health checks, k6 test with results.
Done when the job runs on schedule in Azure, the load test results (including whether the per-endpoint lock and count are sufficient) are in the README, and the live secret check has been rerun after any telemetry changes.

**Phase 7: Finish**
README with screenshot or GIF, architecture diagram, how to run locally and deploy, known limitations. Public read-only demo page with **synthetic** data (never real captured content). ADRs complete.
Done when someone who has never seen the project can understand and try it from the README.

---

## 15. Decisions to document (ADRs)

1. PostgreSQL and storing bodies in `bytea` instead of Blob Storage
2. Cookie authentication instead of JWT in the browser
3. Database as source of truth, SignalR for notifications only
4. One replica per revision, revision overlap during deploy, and the consequences
5. Quota checked under an endpoint row lock with `COUNT`, instead of stored counters
6. Rejecting new requests when the quota is full instead of deleting the oldest
7. Scale to zero and cold starts
8. Migrations as a separate job before the app update, compatible with old and new code
9. Cleanup as a separate scheduled job
10. Live view contract: newest page live, full history through pagination

---

## 16. Working with AI assistants

- Read and understand every diff before committing. Security code (auth, access control, quota, later SSRF) is reviewed line by line
- Write or review the tests for a feature before accepting its implementation
- No new packages without a reason you can state
- Small commits with honest messages
- Keep this plan and the ADRs up to date so the assistant has the right context

---

## 17. Known risks

- Wrong access control exposes other users' captured data: covered by the isolation tests in phase 2
- Abuse of the public capture route: limits and quotas exist before the first deploy
- Unexpected Azure cost: budget alert from phase 3, `minReplicas: 0`, one Postgres server
- Free PostgreSQL ends after 12 months: decide then whether to renew the student offer, move the database or shut the demo down
