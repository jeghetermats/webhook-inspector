import { type Api, ApiError } from '../api/client'
import { type Endpoint, LIMITS, type ReplayAttempt, type RequestDetail, type RequestFilters, type RequestPage, type RequestReceivedEvent, type RequestSummary } from '../api/types'
import { base64ToBytes } from '../lib/body'
import { matchesFilters } from '../lib/live'
import { type DemoEndpoint, TEMPLATES, buildRequest, initialEndpoints, initialRequests } from './data'

const PAGE_SIZE = 50
const PREVIEW_BYTES = 160

/** Same idea as the server's BodyPreview: short text, or nothing for binary bodies. */
function preview(request: RequestDetail): string | null {
  const bytes = base64ToBytes(request.bodyBase64).subarray(0, PREVIEW_BYTES)
  if (bytes.length === 0) return null
  for (let cut = 0; cut <= 3 && cut < bytes.length; cut++) {
    try {
      const text = new TextDecoder('utf-8', { fatal: true }).decode(bytes.subarray(0, bytes.length - cut))
      return [...text].some((c) => c < ' ' && !'\n\r\t'.includes(c)) ? null : text
    } catch {
      // try a shorter prefix: the cut may be inside a multi-byte character
    }
  }
  return null
}

const cursorOf = (r: Pick<RequestDetail, 'receivedAt' | 'id'>) => `${r.receivedAt}|${r.id}`

/** Keyset comparison on (receivedAt, id), newest first, like the server. */
function isOlderThan(r: RequestDetail, cursor: string): boolean {
  const [at, id] = cursor.split('|')
  const diff = Date.parse(r.receivedAt) - Date.parse(at)
  return diff < 0 || (diff === 0 && r.id < id)
}

function summary(r: RequestDetail): RequestSummary {
  return {
    id: r.id,
    method: r.method,
    path: r.path,
    rawQuery: r.rawQuery,
    receivedAt: r.receivedAt,
    contentType: r.contentType,
    bodySize: r.bodySize,
    bodyPreview: preview(r),
    cursor: cursorOf(r),
  }
}

type Listener = (event: RequestReceivedEvent) => void

/**
 * In-memory implementation of the API for the public demo. It behaves like the server
 * (filters, keyset pagination, limits, 404s) but lives entirely in the browser and is
 * reset by reloading the page. Nothing is ever sent to the server.
 */
export function createDemoApi(now = new Date(), host = window.location.host) {
  const endpoints: DemoEndpoint[] = initialEndpoints(now)
  const requests = initialRequests(endpoints, now, host)
  const listeners = new Set<Listener>()
  const downloads = new Map<string, string>()
  const replays = new Map<string, ReplayAttempt[]>()
  let simulated = 0

  const delay = <T>(value: T) => new Promise<T>((resolve) => setTimeout(() => resolve(value), 60))

  const findEndpoint = (id: string) => {
    const endpoint = endpoints.find((e) => e.id === id)
    if (!endpoint) throw new ApiError(404, 'Not found.')
    return endpoint
  }

  const withUsage = (e: DemoEndpoint): Endpoint => {
    const list = requests.get(e.id) ?? []
    return {
      id: e.id,
      name: e.name,
      captureToken: e.captureToken,
      createdAt: e.createdAt,
      requestCount: list.length,
      bodyBytes: list.reduce((sum, r) => sum + r.bodySize, 0),
    }
  }

  const findRequest = (id: string) => {
    for (const list of requests.values()) {
      const found = list.find((r) => r.id === id)
      if (found) return found
    }
    throw new ApiError(404, 'Not found.')
  }

  const validName = (name: string) => {
    const trimmed = name.trim()
    if (!trimmed || trimmed.length > LIMITS.maxNameLength) {
      throw new ApiError(400, `Name is required and can be at most ${LIMITS.maxNameLength} characters.`)
    }
    return trimmed
  }

  const api: Api = {
    me: () => delay({ id: 'demo', login: 'demo' }),
    logout: () => delay(undefined),

    endpoints: () => delay(endpoints.map(withUsage)),
    createEndpoint: async (name: string) => {
      if (endpoints.length >= LIMITS.maxEndpoints) {
        throw new ApiError(409, `You can have at most ${LIMITS.maxEndpoints} endpoints. Delete one to create another.`)
      }
      const endpoint: DemoEndpoint = {
        id: `0d3e0000-0000-7000-8000-${Date.now().toString(16).padStart(12, '0').slice(-12)}`,
        name: validName(name),
        captureToken: `demo${Math.random().toString(36).slice(2).padEnd(18, '0').slice(0, 18)}`,
        createdAt: new Date().toISOString(),
        kind: (['github', 'stripe', 'slack'] as const)[endpoints.length % 3],
      }
      endpoints.push(endpoint)
      requests.set(endpoint.id, [])
      return delay(withUsage(endpoint))
    },
    renameEndpoint: async (id: string, name: string) => {
      const endpoint = findEndpoint(id)
      endpoint.name = validName(name)
      return delay(withUsage(endpoint))
    },
    deleteEndpoint: async (id: string) => {
      findEndpoint(id)
      endpoints.splice(endpoints.findIndex((e) => e.id === id), 1)
      requests.delete(id)
      return delay(undefined)
    },
    rotateToken: async (id: string) => {
      const endpoint = findEndpoint(id)
      endpoint.captureToken = `demo${Math.random().toString(36).slice(2).padEnd(18, '0').slice(0, 18)}`
      return delay(withUsage(endpoint))
    },
    clearRequests: async (id: string) => {
      findEndpoint(id)
      requests.set(id, [])
      return delay(undefined)
    },

    requests: async (endpointId: string, filters: RequestFilters, cursor?: string | null): Promise<RequestPage> => {
      findEndpoint(endpointId)
      const matching = (requests.get(endpointId) ?? [])
        .filter((r) => matchesFilters(r, filters))
        .filter((r) => !cursor || isOlderThan(r, cursor))
      const page = matching.slice(0, PAGE_SIZE)
      return delay({
        items: page.map(summary),
        nextCursor: matching.length > PAGE_SIZE ? cursorOf(page[page.length - 1]) : null,
      })
    },
    requestDetail: async (id: string) => delay(findRequest(id)),
    deleteRequest: async (id: string) => {
      const request = findRequest(id)
      const list = requests.get(request.endpointId)!
      list.splice(list.indexOf(request), 1)
      return delay(undefined)
    },
    bodyDownloadUrl: (id: string) => {
      if (!downloads.has(id)) {
        const request = findRequest(id)
        const bytes = base64ToBytes(request.bodyBase64)
        downloads.set(id, URL.createObjectURL(new Blob([bytes.slice().buffer], { type: 'application/octet-stream' })))
      }
      return downloads.get(id)!
    },

    replays: async (requestId: string) => {
      findRequest(requestId)
      return delay(replays.get(requestId) ?? [])
    },
    replay: async (requestId: string, targetUrl: string) => {
      const request = findRequest(requestId)
      const attempt = simulateReplay(request, targetUrl)
      replays.set(requestId, [attempt, ...(replays.get(requestId) ?? [])].slice(0, 20))
      return delay(attempt)
    },
  }

  return {
    api,

    /** Delivers a synthetic webhook to an endpoint, like a sender would, and notifies listeners. */
    simulateWebhook(endpointId: string) {
      const endpoint = findEndpoint(endpointId)
      const templates = TEMPLATES[endpoint.kind]
      const n = 1000 + simulated++
      const request = buildRequest(endpoint.id, templates[n % templates.length], n, new Date(), host)
      const list = requests.get(endpoint.id)!
      if (list.length >= LIMITS.maxRequestsPerEndpoint) return null
      list.unshift(request)
      const event: RequestReceivedEvent = {
        endpointId: endpoint.id,
        requestId: request.id,
        method: request.method,
        path: request.path,
        receivedAt: request.receivedAt,
        bodySize: request.bodySize,
        cursor: cursorOf(request),
      }
      // Like the server: notify after the request is stored.
      setTimeout(() => listeners.forEach((listener) => listener(event)), 40)
      return request
    },

    subscribe(listener: Listener) {
      listeners.add(listener)
      return () => listeners.delete(listener)
    },
  }
}

export type DemoBackend = ReturnType<typeof createDemoApi>

/**
 * A rough imitation of the server's rules, so the demo shows what a blocked target looks like.
 * The real check runs on the server against the addresses a name resolves to (ADR 0013).
 */
const PRIVATE_HOST =
  /^(localhost|.*\.localhost|127\.\d+\.\d+\.\d+|10\.\d+\.\d+\.\d+|192\.168\.\d+\.\d+|169\.254\.\d+\.\d+|172\.(1[6-9]|2\d|3[01])\.\d+\.\d+|0\.\d+\.\d+\.\d+|\[(::1?|f[cd][0-9a-f:]*|fe80:[0-9a-f:]*|::ffff:.*)\])$/i

/** No request leaves the browser: a public target gets a made-up response that says so. */
function simulateReplay(request: RequestDetail, targetUrl: string): ReplayAttempt {
  let url: URL
  try {
    url = new URL(targetUrl)
  } catch {
    throw new ApiError(400, 'Enter an absolute http:// or https:// URL.')
  }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') {
    throw new ApiError(400, 'Enter an absolute http:// or https:// URL.')
  }

  const port = url.port ? Number(url.port) : url.protocol === 'https:' ? 443 : 80
  const error =
    port !== 80 && port !== 443
      ? 'Blocked: Only ports 80 and 443 are allowed.'
      : PRIVATE_HOST.test(url.hostname)
        ? 'Blocked: The target is a private or reserved address.'
        : null
  const body = JSON.stringify(
    { demo: true, message: 'Simulated response. In the demo nothing leaves your browser.', received: { method: request.method, bytes: request.bodySize } },
    null,
    2,
  )

  return {
    id: crypto.randomUUID(),
    requestId: request.id,
    targetUrl: url.href,
    startedAt: new Date().toISOString(),
    durationMs: error ? 0 : 38,
    statusCode: error ? null : 200,
    responseHeaders: error ? [] : [{ name: 'Content-Type', value: 'application/json; charset=utf-8' }],
    responseBodyBase64: error ? '' : btoa(body),
    responseTruncated: false,
    error,
  }
}
