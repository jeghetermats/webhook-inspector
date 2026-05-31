# 0014: Least-privilege database role for the app

- Status: accepted
- Date: 2026-05-31

## Context

Until now the app, the cleanup job and the migrate job all connected as the Postgres administrator. The app only ever reads and changes rows, but with the administrator login a bug or an SQL injection in the app could drop tables, change the schema, create roles or read the migration history. The administrator password also had to sit in three Container Apps secrets instead of one.

The migrate job already runs separately, before every app update ([ADR 0008](0008-migrations-as-a-separate-job.md)), so there is a natural place for the one part that needs more rights.

## Decision

The app and the cleanup job connect as `webhook_app`, a login role that may only `SELECT`, `INSERT`, `UPDATE` and `DELETE` rows in the app's tables and use their sequences. It has no access to `__EFMigrationsHistory`, cannot create, alter, truncate or drop anything, and cannot create roles or schemas.

The migrate job runs as the administrator: first the EF Core migration bundle, then `dotnet WebhookInspector.Api.dll setup-db-role`, which creates the role if it is missing, sets its password and grants the rights on all current tables. Because this runs after every migration, a new table is covered in the same deploy that creates it.

The role's password is its own GitHub secret, `POSTGRES_APP_PASSWORD`. The statements are built on the server with `format()` and its `%I` and `%L` quoting, because Postgres DDL cannot take bind parameters.

## Alternatives considered

- **Create the role in an EF Core migration**: migrations run once, so the password could never change, and the grants would have to be repeated in every later migration that adds a table.
- **`ALTER DEFAULT PRIVILEGES` instead of granting after each migration**: covers new tables too, but only those created by the role named in it, and it is invisible when you look at a table's grants. Granting after each migration is explicit and repeatable.
- **Entra ID (managed identity) login to Postgres**: no password at all, but it needs Entra authentication turned on for the server, an Entra administrator and a token-fetching connection in the app, in a student tenant with limited rights ([ADR 0012](0012-managed-identity-for-deploy-login.md)). The natural next step after this one.
- **Keep the administrator, documented as a limitation**: the original v1 choice. Cheap, but the gap between "the app should only touch rows" and what the login allows was the largest one left.

## Consequences

- An app bug can at worst damage rows, not the schema, and cannot create a way back in through new roles.
- The administrator password is only in the migrate job.
- Every API test runs as `webhook_app` (the test fixture creates it the same way), so a missing grant fails CI instead of production. Separate tests check that schema changes, `TRUNCATE`, role creation and the migration history are refused.
- One more secret to manage. Infra stops with a clear error if it is missing.
- Switching an existing deployment needs the new image in place before the next Infra run, because Infra configures the migrate job to call `setup-db-role`. Deploy first, then Infra.
- Local development and the end-to-end tests still use the docker-compose superuser. Production is what this protects.
