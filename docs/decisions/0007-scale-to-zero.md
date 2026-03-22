# 0007: Scale to zero and accept cold starts

- Status: accepted
- Date: 2026-03-22

## Context

The service has no real users and must stay inside the Azure for Students offer. Container Apps on the Consumption plan gives a monthly free grant of 180,000 vCPU-seconds and 360,000 GiB-seconds. An always-running replica of 0.25 vCPU uses about 650,000 vCPU-seconds a month, far more than the grant.

## Decision

Run the app with `minReplicas: 0`, `maxReplicas: 1`, 0.25 vCPU and 0.5 GiB. Container Apps starts a replica on the first HTTP request and removes it after a quiet period.

## Alternatives considered

- **`minReplicas: 1`**: no cold starts, but it costs money every hour and uses up the student credit for nothing.
- **App Service (Free tier)**: daily CPU quotas, no scale to zero, and no built-in job type for migrations and cleanup.

## Consequences

- With 0.25 vCPU and 0.5 GiB, the free grant covers about 200 active hours a month.
- The first request after a quiet period waits for a cold start of a few seconds. For a webhook sender this usually means a slower first delivery; most providers allow 10 seconds or more.
- An open browser with a SignalR connection keeps the replica running, so active use is billed as active time.
- Work that must happen while the app sleeps (migrations, retention cleanup) runs as separate Container Apps Jobs (ADR 0008, ADR 0009).
