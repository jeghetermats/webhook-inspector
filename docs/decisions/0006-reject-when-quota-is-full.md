# 0006: Reject new requests when the quota is full

- Status: accepted
- Date: 2026-03-09

## Context

When an endpoint has 1,000 stored requests or 50 MB of bodies, a new capture either has to be rejected or make room by deleting old requests.

## Decision

Reject with `429 Too Many Requests` and a short plain-text explanation. The user frees space by deleting requests or clearing the history, and the cleanup job frees space after 7 days.

## Alternatives considered

- **Delete the oldest requests to make room (ring buffer)**: convenient for a user who only cares about recent traffic, but it silently destroys data the user may be in the middle of inspecting, and turns every capture into a potential delete inside the quota transaction.

## Consequences

- No captured data disappears without the user's action or the documented retention.
- The sender sees `429` once the quota is full. Webhook providers usually retry, so a user who clears the history soon afterwards may still receive the retried delivery.
- The UI must show usage per endpoint so the user sees the limit coming (phase 4).
