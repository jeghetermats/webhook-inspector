// k6 load test for the public capture route (PLAN.md section 12).
//
// docker run --rm -i -v "${PWD}/load:/load" -e BASE_URL=... -e TOKENS=tok1,tok2 -e RATE=3 -e DURATION=3m grafana/k6 run /load/capture.js
//
// TOKENS     comma-separated capture tokens, requests are spread evenly across them
// RATE       requests per second in total (constant arrival rate)
// DURATION   test length, e.g. 3m
// BODY_BYTES size of each JSON body (default 1024)
// LABEL      name for the result file in load/results
import http from 'k6/http'
import { check } from 'k6'
import { Counter, Trend } from 'k6/metrics'

const baseUrl = (__ENV.BASE_URL || 'http://localhost:5080').replace(/\/$/, '')
const tokens = (__ENV.TOKENS || '').split(',').map((t) => t.trim()).filter(Boolean)
const rate = Number(__ENV.RATE || 3)
const duration = __ENV.DURATION || '3m'
const bodyBytes = Number(__ENV.BODY_BYTES || 1024)
const label = __ENV.LABEL || `rate${rate}`

if (tokens.length === 0) {
  throw new Error('Set TOKENS to one or more capture tokens.')
}

// Every response is one of four outcomes:
//   stored       200: the capture was saved
//   rateLimited  429: a rate limit or the endpoint quota said no, on purpose
//   shed         503: load shedding turned the request away quickly (Retry-After), on purpose
//   failed       anything else, including timeouts and 500s: a real failure
// Only `failed` counts against the test. Older result files (before 2026-05-30) have no
// `shed` field and count 503 under `failed`; see docs/performance.md.
http.setResponseCallback(http.expectedStatuses(200, 429, 503))

const stored = new Counter('captures_stored')
const rateLimited = new Counter('captures_429')
const shed = new Counter('captures_503')
const failed = new Counter('captures_failed')
const storedDuration = new Trend('capture_duration_stored', true)

const payload = JSON.stringify({ event: 'load-test', pad: 'x'.repeat(Math.max(0, bodyBytes - 40)) })

export const options = {
  scenarios: {
    captures: {
      executor: 'constant-arrival-rate',
      rate,
      timeUnit: '1s',
      duration,
      preAllocatedVUs: Math.max(10, rate * 2),
      maxVUs: Math.max(50, rate * 10),
    },
  },
  thresholds: {
    captures_failed: ['count==0'],
    capture_duration_stored: ['p(95)<1000'],
  },
  summaryTrendStats: ['avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
}

export default function () {
  const token = tokens[__ITER % tokens.length]
  const response = http.post(`${baseUrl}/h/${token}/load/${__VU}-${__ITER}?run=${label}`, payload, {
    headers: { 'Content-Type': 'application/json', 'User-Agent': 'k6-load-test' },
    tags: { route: 'capture' },
  })

  if (response.status === 200) {
    stored.add(1)
    storedDuration.add(response.timings.duration)
  } else if (response.status === 429) {
    rateLimited.add(1)
  } else if (response.status === 503) {
    shed.add(1)
  } else {
    failed.add(1)
  }

  check(response, { 'stored, rate limited or shed': (r) => [200, 429, 503].includes(r.status) })
}

export function handleSummary(data) {
  const m = data.metrics
  const value = (name, stat) => (m[name] ? m[name].values[stat] : undefined)
  const result = {
    label,
    baseUrl,
    endpoints: tokens.length,
    rate,
    duration,
    bodyBytes,
    requests: value('http_reqs', 'count'),
    stored: value('captures_stored', 'count') || 0,
    rateLimited: value('captures_429', 'count') || 0,
    shed: value('captures_503', 'count') || 0,
    failed: value('captures_failed', 'count') || 0,
    storedLatencyMs: {
      median: value('capture_duration_stored', 'med'),
      p95: value('capture_duration_stored', 'p(95)'),
      p99: value('capture_duration_stored', 'p(99)'),
      max: value('capture_duration_stored', 'max'),
    },
  }
  const text =
    `\n${label}: ${result.requests} requests at ${rate}/s for ${duration} over ${tokens.length} endpoint(s)\n` +
    `  stored ${result.stored}, rate limited or quota (429) ${result.rateLimited}, ` +
    `shed under load (503) ${result.shed}, failed ${result.failed}\n` +
    `  stored latency ms: median ${fmt(result.storedLatencyMs.median)}, p95 ${fmt(result.storedLatencyMs.p95)}, ` +
    `p99 ${fmt(result.storedLatencyMs.p99)}, max ${fmt(result.storedLatencyMs.max)}\n`
  return {
    stdout: text,
    [`/load/results/${label}.json`]: JSON.stringify(result, null, 2),
  }
}

function fmt(n) {
  return n === undefined ? '-' : n.toFixed(0)
}
