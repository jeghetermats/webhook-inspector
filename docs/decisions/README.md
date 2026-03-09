# Architecture decision records

Short records of decisions that shaped the project. Copy `0000-template.md` to start a new one.

| # | Decision | Status |
|---|---|---|
| [0001](0001-postgres-and-bytea-bodies.md) | PostgreSQL, with request bodies stored in `bytea` | accepted |
| [0005](0005-quota-under-endpoint-row-lock.md) | Quota checked under an endpoint row lock with `COUNT` | accepted |
| [0006](0006-reject-when-quota-is-full.md) | Reject new requests when the quota is full | accepted |

Numbers follow the list in [PLAN.md section 15](../PLAN.md#15-decisions-to-document-adrs), so some are filled in later.
