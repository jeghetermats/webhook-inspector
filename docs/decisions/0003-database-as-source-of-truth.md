# 0003: Database as the source of truth, SignalR for notifications only

- Status: accepted
- Date: 2026-04-26

## Context

The UI should show new requests as they arrive. Live channels drop: browsers sleep, networks change, deploys restart the server, and a send can fail even while connected. Captured requests must never be lost or shown differently depending on whether someone was watching.

## Decision

Every captured request is committed to Postgres first. Only then does the server send a small `RequestReceived` notification to the endpoint's SignalR group: request id, method, path, time, size and a pagination cursor. The notification carries no body, header values or query string.

The client treats a notification as a hint. It inserts the row into its cached list, and fills in the rest (query, preview) on the next fetch from the API.

## Alternatives considered

- **Push full requests over SignalR**: the live list would not need a fetch, but secrets in bodies and headers would travel over a second channel, and anything missed while disconnected would be gone unless the client also fetched.
- **Polling only**: simplest, but a 2 to 5 second delay and constant requests that keep the app from scaling to zero.
- **Server-Sent Events**: would work for one direction, but SignalR adds reconnect, groups and a fallback transport for free, and is the standard choice in ASP.NET Core.

## Consequences

- A lost notification never means lost data: the client refetches the newest page after every reconnect and every 30 seconds while connected (ADR 0010).
- Notifications are sent only after commit, so a client that is told about a request can always load it. Tested in `RequestsHubTests`.
- A failed send is logged (without request data) and never fails the capture.
- The hub requires sign-in, and `Subscribe` checks endpoint ownership on every connection. Groups are not trusted as a security boundary on their own.
