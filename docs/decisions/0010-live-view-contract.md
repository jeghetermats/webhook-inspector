# 0010: Live view contract: the newest page is live, full history through pagination

- Status: accepted
- Date: 2026-04-26

## Context

A live list that promises to contain every request ever received must handle reconnects, lost notifications, filters, and thousands of rows in the browser. That is much harder than what users need: to see what just arrived, and to be able to find anything older.

## Decision

The live view shows the newest 50 requests that match the active filters. It does not promise to contain every request that arrived. Every stored request is always reachable through cursor pagination ("Load older requests").

In the client:

1. A `RequestReceived` event that matches the active filters is inserted in order, de-duplicated on request id.
2. With only the first page loaded, the list is trimmed to 50 and the "load older" cursor moves to the last kept item, so pagination continues without a gap. With older pages loaded, nothing is trimmed, because that would open a gap between pages.
3. After a reconnect, the client subscribes again and refetches, replacing the first page with fresh data.
4. While connected, it also refetches every 30 seconds, because a notification can be lost even on a working connection.
5. Older requests come from pagination, independent of live events.

## Consequences

- Events lost while disconnected do not matter: the refetch after reconnect is the source of the truth, not the event stream.
- The browser holds at most about 50 rows plus what the user explicitly loads.
- A live row lacks the query string and body preview until the next fetch (they are not sent over the live channel, ADR 0003).
- Tested by the Vitest suite in `web/src/lib/live.test.ts` and the Playwright test that cuts the live connection, sends 120 requests and checks the first page and pagination after reconnecting.
