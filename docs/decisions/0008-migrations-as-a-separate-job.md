# 0008: Migrations as a separate job before the app update

- Status: accepted
- Date: 2026-03-22

## Context

The database schema changes over time through EF Core migrations. Applying them at app startup is common, but with Container Apps a new revision can start while the old one still serves traffic, several replicas could race to migrate, and a failed migration would leave the app crash-looping.

## Decision

The image contains an EF Core migration bundle (`/app/efbundle`). A manually triggered Container Apps Job runs it. Every deploy:

1. builds and pushes the image,
2. points the migrate job at the new image and runs it, waiting for success,
3. only then updates the app to the new image.

The app never applies migrations at startup. Every migration must work with both the old and the new code ("expand, then contract"): add columns and tables first, remove old ones in a later deploy.

## Alternatives considered

- **`Database.Migrate()` at startup**: simplest, but races between replicas and revisions, slows cold starts (ADR 0007), and turns a bad migration into an app outage.
- **Running migrations from GitHub Actions directly**: would need the database reachable from GitHub's runners, so a wider firewall rule and the admin connection string on the runner.

## Consequences

- A failed migration stops the deploy before the app changes, so the running version keeps working.
- The bundle runs inside Azure next to the database, with the connection string as a Container Apps secret.
- Old and new code can run against the same schema for a while, which constrains how migrations are written. Destructive changes need two deploys.
