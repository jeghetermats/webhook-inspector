# 0004: One replica per revision, and what happens during a deploy

- Status: accepted
- Date: 2026-04-26

## Context

SignalR groups live in the memory of the server process that holds the connection. With several replicas, a capture handled by replica A could not reach a browser connected to replica B, unless a backplane (Azure SignalR Service or Redis) forwards messages between them.

## Decision

Run the app with `maxReplicas: 1` and SignalR in-process. No backplane.

## Consequences

- Correct live updates without extra services or cost.
- Throughput is limited to one 0.25 vCPU replica. The k6 test in phase 6 measures what that means.
- `maxReplicas` applies per revision. During a deploy the old and new revision run side by side for a short time, and every deploy starts a new process: open SignalR connections drop and reconnect to the new revision. The client handles this like any other reconnect and refetches the newest page (ADR 0010), so no request is lost from view.
- Scaling out later means adding Azure SignalR Service (or a Redis backplane) and changing `AddSignalR()` accordingly. Nothing else in the code depends on there being one replica: the database is the source of truth (ADR 0003), and the quota uses row locks, not in-memory state (ADR 0005).
