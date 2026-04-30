# 0009: Retention cleanup as a separate scheduled job

- Status: accepted
- Date: 2026-04-30

## Context

Captured requests may contain secrets, so they are deleted after 7 days. The app scales to zero when nobody uses it (ADR 0007), so anything that runs inside the app only runs while someone happens to be sending requests or using the UI.

## Decision

A Container Apps Job runs every hour at :17 with the same image as the app and the argument `cleanup`. It deletes requests older than 7 days in batches of 1,000 rows and exits. Requests are therefore deleted at the first run after they turn 7 days old, not at an exact time.

The deploy workflow points the job at each new image, so the cleanup code always matches the schema the app uses.

## Alternatives considered

- **A background service in the app**: simplest, but it does not run while the app is scaled to zero, and with several replicas it would run several times.
- **`pg_cron` in Postgres**: would run independently, but puts application logic in the database, needs a server extension, and is harder to test and observe.
- **Delete on read or on capture**: spreads the work across requests, but data could stay far longer than 7 days on endpoints nobody uses.

## Consequences

- Cleanup runs whether or not the app is awake. One run takes seconds, well inside the free grant.
- Batches keep each DELETE short. Like user deletes, cleanup only removes `CapturedRequest` rows and never locks the endpoint row, so it cannot deadlock with captures and can only free quota (ADR 0005). Tested together with parallel captures and user deletes.
- The schedule is offset from the full hour, when many jobs in the region start at once.
- Each run logs how many rows it deleted, shown in the operations workbook.
