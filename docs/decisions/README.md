# Architecture decision records

Short records of decisions that shaped the project. Copy `0000-template.md` to start a new one.

| # | Decision | Status |
|---|---|---|
| [0001](0001-postgres-and-bytea-bodies.md) | PostgreSQL, with request bodies stored in `bytea` | accepted |
| [0002](0002-cookie-authentication.md) | Cookie authentication instead of JWT in the browser | accepted |
| [0005](0005-quota-under-endpoint-row-lock.md) | Quota checked under an endpoint row lock with `COUNT` | accepted |
| [0006](0006-reject-when-quota-is-full.md) | Reject new requests when the quota is full | accepted |
| [0007](0007-scale-to-zero.md) | Scale to zero and accept cold starts | accepted |
| [0008](0008-migrations-as-a-separate-job.md) | Migrations as a separate job before the app update | accepted |
| [0011](0011-github-oauth-app-for-sign-in.md) | GitHub OAuth app (not a GitHub App) for sign-in | accepted |
| [0012](0012-managed-identity-for-deploy-login.md) | User-assigned managed identity for the deploy login | accepted |

Numbers 0001–0010 follow the list in [PLAN.md section 15](../PLAN.md#15-decisions-to-document-adrs), so some are filled in later. Decisions not in that list start at 0011.
