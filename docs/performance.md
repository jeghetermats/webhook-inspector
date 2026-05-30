# Performance

How much one small replica can take, what broke when it took too much, and what changed.

## How the test works

[`load/capture.js`](../load/capture.js) (k6) sends webhooks to the live capture route at a constant rate. Every response counts as one of four outcomes:

| Outcome | Status | Meaning |
|---|---|---|
| Stored | `200` | The capture was saved |
| Rate limited | `429` | A rate limit or the endpoint quota said no, on purpose |
| Shed | `503` | Load shedding turned the request away quickly, with `Retry-After`, on purpose |
| Failed | anything else | Timeouts, connection errors and `500`s: the real failures |

[`load/notify-latency.mjs`](../load/notify-latency.mjs) measures the time from sending a webhook to receiving its live notification over SignalR, the same path the browser uses. Both write JSON to [`load/results`](../load/results).

```powershell
# Capture load (k6 in Docker, nothing to install)
docker run --rm -i -v "${PWD}/load:/load" -e BASE_URL=https://<app URL> -e TOKENS=<token1>,<token2> `
  -e RATE=3 -e DURATION=3m -e LABEL=realistic grafana/k6 run /load/capture.js

# Notification latency, in a second terminal while k6 runs. AUTH_COOKIE is the value of the
# __Host-wi-auth cookie from your browser's dev tools: set it in your own terminal only.
cd load; npm install
$env:BASE_URL='https://<app URL>'; $env:ENDPOINT_ID='<id>'; $env:TOKEN='<token>'; $env:AUTH_COOKIE='<cookie>'
npm run notify-latency
```

The capture route allows 120 requests/min per endpoint and 300/min per source IP, so a test from one machine at the default limits measures realistic traffic, not capacity. For a stress test the limits can be raised temporarily with the GitHub `production` variables `CAPTURE_PERMITS_PER_TOKEN_PER_MINUTE` and `CAPTURE_PERMITS_PER_IP_PER_MINUTE` and an Infra run, and removed again afterwards.

## Results

Setup: one Container App replica with 0.25 vCPU and 0.5 GiB, Postgres Flexible Server B1MS, both in Norway East. k6 ran in Docker on a PC in Norway with 1 KB JSON bodies. The rate limits were raised for the test and restored afterwards, so no request was rate limited (`429`). Latencies are for stored requests and include the round trip from the PC to Azure.

| Run | Load | Requests | Stored | Shed (`503`) | Failed | Median | p95 | p99 |
|---|---|---|---|---|---|---|---|---|
| realistic | 3/s for 3 min, 8 endpoints | 540 | 540 (100%) | 0 | 0 | 44 ms | 113 ms | 165 ms |
| stress-10 | 10/s for 2 min, 8 endpoints | 1201 | 1201 (100%) | 0 | 0 | 44 ms | 50 ms | 111 ms |
| stress-25 | 25/s for 2 min, 8 endpoints | 3001 | 3001 (100%) | 0 | 0 | 44 ms | 50 ms | 59 ms |
| hot | 20/s for 30 s, **one** endpoint | 601 | 601 (100%) | 0 | 0 | 43 ms | 108 ms | 417 ms |
| stress-50, before | 50/s for 1 min, 8 endpoints | 2039 | 279 (14%) | 0 | **1760** | 8977 ms | 32891 ms | 36116 ms |
| stress-50, after | 50/s for 75 s, 8 endpoints | 3750 | 3044 (81%) | 706 | **0** | 1517 ms | 2294 ms | 3655 ms |

**A note on the raw file for "stress-50, after".** These runs were made before the script counted `503` separately, so [`stress-50-after.json`](../load/results/stress-50-after.json) lists the 706 shed requests under `failed`. The table counts them as shed because Application Insights showed only `200` and `503` responses for the run, and the app log had no exceptions or connection timeouts. The script now reports `shed` on its own; the raw files are kept as they were measured.

Time from sending a webhook to receiving its live notification over SignalR (`notify-latency.mjs`):

| Condition | Notified | Median | p95 | Max |
|---|---|---|---|---|
| Idle | 40 of 40 | 37 ms | 40 ms | 47 ms |
| During stress-50 (after) | 55 of 55 stored | 584 ms | 1592 ms | 1700 ms |

## What the results show

**Is the per-endpoint lock with `COUNT` sufficient?** In this test, yes. With 20 requests/s all waiting for the same endpoint row lock, all 601 requests were stored with a median of 43 ms, and the quota tests show the count stays correct under contention. At the loads tested, the replica's CPU ran out before the lock became a problem. Higher loads on one endpoint, or a bigger replica, were not measured.

**What broke at 50/s, and what changed after the first run.** The replica reached 99% CPU. Requests queued while each held a database connection, Npgsql opened connections up to its default pool size of 100, and the B1MS server (`max_connections` = 50, some reserved by Azure) refused or timed out. 1760 of 2039 requests failed after up to 37 seconds; the app log showed `Database.Connection` errors and `TimeoutException`s. Two changes:

1. **Load shedding on the capture route**: at most 16 captures at once, a queue of 50, and `503` with `Retry-After: 2` beyond that. The API and the hub are not affected.
2. **Npgsql pool capped at 20 connections**, safely below the server limit.

**Effect.** At the same 50/s, 3044 of 3750 requests (81%) were stored. The other 706 were turned away with `503` within the queue time instead of hanging for half a minute. The app log had no exceptions or connection timeouts, and Application Insights showed only `200` and `503` responses. CPU was still at 100%, so one 0.25 vCPU replica stores about **40 captures per second**. Live notifications arrived for every stored request, within 1.6 s at p95 under full load.

A `503` is still a request the sender has to deal with. Stripe and Slack retry failed deliveries on their own; GitHub does not, but its delivery log lets you redeliver. Shedding is a way to fail fast and stay up, not a way to accept more.

**Limits of this test.** It ran from one machine against one replica, with raised rate limits. In normal operation the per-token (120/min) and per-IP (300/min) limits stop a single sender long before this point. More throughput needs more CPU per replica, or several replicas with a SignalR backplane ([ADR 0004](decisions/0004-one-replica-per-revision.md)). A shorter queue would lower the latency of accepted requests under overload, at the cost of more `503`s.
