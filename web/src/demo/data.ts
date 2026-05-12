import type { Endpoint, Header, RequestDetail } from '../api/types'

/**
 * Synthetic data for the public demo (PLAN.md phase 7). Everything here is made up: the
 * organisations, ids, signatures and IP addresses (from the 203.0.113.0/24 documentation
 * range) do not exist. Real captured content never appears in the demo.
 */

const encoder = new TextEncoder()

function toBase64(bytes: Uint8Array): string {
  let binary = ''
  for (const b of bytes) binary += String.fromCharCode(b)
  return btoa(binary)
}

function fakeHex(seed: number, length: number): string {
  let out = ''
  let x = seed * 2654435761
  while (out.length < length) {
    x = (x * 1103515245 + 12345) >>> 0
    out += x.toString(16).padStart(8, '0')
  }
  return out.slice(0, length)
}

let idCounter = 0
export function demoId(): string {
  idCounter++
  const hex = idCounter.toString(16).padStart(12, '0')
  return `0d3e0000-0000-7000-8000-${hex}`
}

export interface Template {
  method: string
  path: string
  rawQuery?: string
  contentType: string | null
  headers: (n: number) => Header[]
  body: (n: number) => Uint8Array
}

const json = (value: unknown) => encoder.encode(JSON.stringify(value))

export const TEMPLATES: Record<string, Template[]> = {
  github: [
    {
      method: 'POST',
      path: '/github',
      contentType: 'application/json',
      headers: (n) => [
        { name: 'User-Agent', value: 'GitHub-Hookshot/7f3a2c1' },
        { name: 'X-GitHub-Event', value: 'push' },
        { name: 'X-GitHub-Delivery', value: `${fakeHex(n, 8)}-${fakeHex(n + 1, 4)}-11ef-${fakeHex(n + 2, 4)}-${fakeHex(n + 3, 12)}` },
        { name: 'X-Hub-Signature-256', value: `sha256=${fakeHex(n + 4, 64)}` },
      ],
      body: (n) =>
        json({
          ref: 'refs/heads/main',
          before: fakeHex(n, 40),
          after: fakeHex(n + 9, 40),
          repository: { full_name: 'example-org/example-app', private: false },
          pusher: { name: 'octo-example' },
          commits: [{ id: fakeHex(n + 9, 7), message: ['Fix flaky test', 'Add retry to webhook client', 'Update dependencies'][n % 3] }],
        }),
    },
    {
      method: 'POST',
      path: '/github',
      contentType: 'application/json',
      headers: (n) => [
        { name: 'User-Agent', value: 'GitHub-Hookshot/7f3a2c1' },
        { name: 'X-GitHub-Event', value: 'pull_request' },
        { name: 'X-Hub-Signature-256', value: `sha256=${fakeHex(n + 5, 64)}` },
      ],
      body: (n) =>
        json({
          action: ['opened', 'synchronize', 'closed'][n % 3],
          number: 40 + n,
          pull_request: { title: 'Show request bodies as hex', draft: false },
          repository: { full_name: 'example-org/example-app' },
        }),
    },
  ],
  stripe: [
    {
      method: 'POST',
      path: '/stripe/events',
      contentType: 'application/json; charset=utf-8',
      headers: (n) => [
        { name: 'User-Agent', value: 'Stripe/1.0 (+https://stripe.com/docs/webhooks)' },
        { name: 'Stripe-Signature', value: `t=1793480000,v1=${fakeHex(n + 6, 64)}` },
      ],
      body: (n) =>
        json({
          id: `evt_demo_${fakeHex(n, 14)}`,
          type: ['payment_intent.succeeded', 'charge.refunded', 'invoice.paid'][n % 3],
          livemode: false,
          data: { object: { id: `pi_demo_${fakeHex(n + 2, 14)}`, amount: 1990 + n * 100, currency: 'nok' } },
        }),
    },
  ],
  slack: [
    {
      method: 'POST',
      path: '/slack/commands',
      contentType: 'application/x-www-form-urlencoded',
      headers: (n) => [
        { name: 'User-Agent', value: 'Slackbot 1.0 (+https://api.slack.com/robots)' },
        { name: 'X-Slack-Request-Timestamp', value: String(1793480000 + n) },
        { name: 'X-Slack-Signature', value: `v0=${fakeHex(n + 7, 64)}` },
      ],
      body: (n) =>
        encoder.encode(
          `token=demo&team_domain=example&command=%2Fdeploy&text=${['staging', 'production', 'rollback+staging'][n % 3]}&user_name=demo.user`,
        ),
    },
    {
      method: 'GET',
      path: '/health',
      rawQuery: 'source=uptime&region=eu-north',
      contentType: null,
      headers: () => [{ name: 'User-Agent', value: 'ExampleUptime/2.4' }],
      body: () => new Uint8Array(),
    },
    {
      method: 'PUT',
      path: '/files/report.bin',
      contentType: 'application/octet-stream',
      headers: () => [{ name: 'User-Agent', value: 'curl/8.10.1' }],
      body: (n) => Uint8Array.from({ length: 96 }, (_, i) => (i * 37 + n * 11) % 256),
    },
  ],
}

export function buildRequest(endpointId: string, template: Template, n: number, receivedAt: Date, host: string): RequestDetail {
  const body = template.body(n)
  return {
    id: demoId(),
    endpointId,
    method: template.method,
    path: template.path,
    rawQuery: template.rawQuery ?? '',
    receivedAt: receivedAt.toISOString(),
    headers: [
      { name: 'Host', value: host },
      ...template.headers(n),
      ...(template.contentType ? [{ name: 'Content-Type', value: template.contentType }] : []),
      ...(body.length ? [{ name: 'Content-Length', value: String(body.length) }] : []),
    ],
    contentType: template.contentType,
    bodySize: body.length,
    bodyBase64: toBase64(body),
    sourceIp: `203.0.113.${(n * 7) % 250 + 1}`,
  }
}

export interface DemoEndpoint extends Omit<Endpoint, 'requestCount' | 'bodyBytes'> {
  kind: keyof typeof TEMPLATES
}

export function initialEndpoints(now: Date): DemoEndpoint[] {
  const minutesAgo = (m: number) => new Date(now.getTime() - m * 60_000).toISOString()
  return [
    { id: '0d3e0000-0000-7000-8000-e00000000001', name: 'GitHub push', captureToken: 'demoGitHubPush00000000', createdAt: minutesAgo(600), kind: 'github' },
    { id: '0d3e0000-0000-7000-8000-e00000000002', name: 'Stripe payments', captureToken: 'demoStripePayments0000', createdAt: minutesAgo(500), kind: 'stripe' },
    { id: '0d3e0000-0000-7000-8000-e00000000003', name: 'Slack commands', captureToken: 'demoSlackCommands00000', createdAt: minutesAgo(400), kind: 'slack' },
  ]
}

/** A few hours of traffic per endpoint, newest first. */
export function initialRequests(endpoints: DemoEndpoint[], now: Date, host: string): Map<string, RequestDetail[]> {
  const result = new Map<string, RequestDetail[]>()
  endpoints.forEach((endpoint, e) => {
    const templates = TEMPLATES[endpoint.kind]
    const count = [18, 11, 9][e] ?? 6
    const list: RequestDetail[] = []
    for (let i = 0; i < count; i++) {
      const at = new Date(now.getTime() - (i * 11 + e * 3 + 2) * 60_000 - i * 7_000)
      list.push(buildRequest(endpoint.id, templates[i % templates.length], i + e * 100, at, host))
    }
    result.set(endpoint.id, list)
  })
  return result
}
