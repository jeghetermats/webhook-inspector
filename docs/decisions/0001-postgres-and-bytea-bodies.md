# 0001: PostgreSQL, with request bodies stored in `bytea`

- Status: accepted
- Date: 2026-03-09

## Context

The service stores captured HTTP requests: metadata (method, path, headers, time) and a body of at most 256 KB. Users list, filter and paginate requests, and an endpoint's usage must be checked atomically against a quota on every capture. Azure for Students includes 12 months of a Burstable B1MS PostgreSQL Flexible Server.

## Decision

Use PostgreSQL through EF Core (Npgsql) as the only data store. Bodies are stored inline in a `bytea` column, headers in a `jsonb` array of `{name, value}` pairs.

## Alternatives considered

- **Bodies in Azure Blob Storage, metadata in Postgres**: cheaper per GB, but every capture becomes two writes that cannot share a transaction. The quota check and the insert would no longer be atomic, deletes would need cleanup in two places, and orphaned blobs become possible. With bodies capped at 256 KB and 50 MB per endpoint the total size stays small.
- **A document database (Cosmos DB)**: fits the request shape, but the quota check needs a lock or counter across many documents, and cursor pagination with filters is more natural in SQL. It would also add cost outside the free offer.
- **Headers as a dictionary or a separate table**: a dictionary loses repeated headers; a separate table adds a join for every detail view. `jsonb` keeps them in order with the row.

## Consequences

- One transaction covers the quota check and the insert (see 0005).
- Deleting an endpoint cascades to its requests in the database.
- Table size grows with body bytes. The quota and the 7-day retention keep it bounded; if it ever matters, bodies can move to blob storage behind the same API.
- Postgres-specific types (`bytea`, `jsonb`, `inet`) tie the schema to Postgres, which is acceptable for this project.
