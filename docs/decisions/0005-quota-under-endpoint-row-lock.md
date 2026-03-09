# 0005: Quota checked under an endpoint row lock with `COUNT`

- Status: accepted
- Date: 2026-03-09

## Context

Each endpoint may store at most 1,000 requests and 50 MB of body bytes. Captures arrive concurrently, and requests are also removed concurrently by user deletes and the cleanup job. Without coordination, two captures can both see 999 rows and both insert.

## Decision

Every capture runs this protocol in one `READ COMMITTED` transaction:

1. Read the body and enforce the size limit before touching the database.
2. `SELECT "Id" FROM "Endpoint" WHERE "CaptureToken" = @token FOR UPDATE`
3. In a **separate** statement: `SELECT count(*), coalesce(sum("BodySize"), 0) FROM "CapturedRequest" WHERE "EndpointId" = @id`
4. Insert only if the new request fits both limits; otherwise roll back and return `429`.
5. Commit.

Usage is always computed from the rows; there are no stored counters.

## Why the count must be a separate statement

Under `READ COMMITTED` each statement takes its own snapshot. The count runs after the lock is granted, so it sees the insert of the transaction it waited for. Merging lock and count into one query or CTE would count from a snapshot taken before the wait and allow the quota to be exceeded.

## Alternatives considered

- **Stored counters on the endpoint row**: faster to read, but every delete path (single delete, clear, cleanup, cascade) has to update them correctly, and any bug makes them drift with no easy way to notice.
- **`SERIALIZABLE` isolation**: correct, but concurrent captures fail with serialization errors that need retries, which is more complex and slower under load.
- **Advisory locks**: work, but are less obvious to a reader than locking the row the quota belongs to.

## Consequences

- Captures for the same endpoint are serialized. Captures for different endpoints do not block each other.
- Deletes only remove `CapturedRequest` rows and never lock the endpoint row, so there is no lock-order deadlock. A concurrent delete can only free space; at worst a capture is rejected because a delete is not yet visible.
- `COUNT` over at most 1,000 rows per endpoint is cheap with the `(EndpointId, ReceivedAt, Id)` index. The k6 load test in phase 6 verifies this.
- `QuotaTests` fail if the `FOR UPDATE` is removed (checked during development: 50 stored instead of 10).
