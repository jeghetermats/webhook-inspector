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

# 2. Start the API (http://localhost:5080)
dotnet run --project src/WebhookInspector.Api

# 3. Start the frontend (http://localhost:5173)
cd web
npm install
npm run dev
```

## Tests

```bash
dotnet test          # backend; Docker must be running for the integration tests
cd web && npm test   # frontend
```
