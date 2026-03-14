# Webhook Inspector

Sign in with GitHub, create an endpoint and get a unique URL. Every HTTP request sent to that URL is stored and shown live in the browser, where it can be filtered, inspected and exported as `curl`.

> See [docs/PLAN.md](docs/PLAN.md) for the full plan and [docs/decisions](docs/decisions) for the architecture decisions.

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

1. Create an OAuth app at <https://github.com/settings/developers> with
   - Homepage URL: `http://localhost:5173`
   - Authorization callback URL: `http://localhost:5173/signin-github`
2. Store its credentials as user secrets (never in `appsettings`):

   ```bash
   dotnet user-secrets set "Authentication:GitHub:ClientId" "<client id>" --project src/WebhookInspector.Api
   dotnet user-secrets set "Authentication:GitHub:ClientSecret" "<client secret>" --project src/WebhookInspector.Api
   ```

3. Open <http://localhost:5173/auth/login>. Without these settings `/auth/login` returns `503`; everything else still works.

Browsers treat `http://localhost` as a secure context, so the `Secure` cookies work locally without HTTPS.

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
| GET | `/health/live`, `/health/ready` | Liveness / database readiness |

## Limits

| Limit | Value |
|---|---|
| Body size | 256 KB (`413` above) |
| Headers | 64 headers, 32 KB in total |
| Rate limit | 120 requests/min per capture token, 300/min per source IP (`429`) |
| Quota per endpoint | 1,000 requests and 50 MB of body bytes (`429`) |

## Tests

```bash
dotnet test          # backend; Docker must be running for the integration tests
cd web && npm test   # frontend
```
