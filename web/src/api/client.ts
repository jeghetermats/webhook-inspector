import type { Endpoint, Me, RequestDetail, RequestFilters, RequestPage } from './types'

export class ApiError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
  }
}

const XSRF_COOKIE = 'XSRF-TOKEN'
const XSRF_HEADER = 'X-XSRF-TOKEN'

/** Reads the antiforgery request token the server put in a JavaScript-readable cookie. */
export function readXsrfToken(cookies: string = document.cookie): string | null {
  for (const part of cookies.split(';')) {
    const [name, ...rest] = part.trim().split('=')
    if (name === XSRF_COOKIE) {
      return decodeURIComponent(rest.join('='))
    }
  }
  return null
}

let tokenRequest: Promise<void> | null = null

/** Asks the server for a fresh antiforgery token (sets the XSRF-TOKEN cookie). */
export function refreshXsrfToken(): Promise<void> {
  tokenRequest ??= fetch('/api/antiforgery', { credentials: 'same-origin' })
    .then((response) => {
      if (!response.ok) {
        throw new ApiError(response.status, 'Could not get an antiforgery token.')
      }
    })
    .finally(() => {
      tokenRequest = null
    })
  return tokenRequest
}

async function problemMessage(response: Response): Promise<string> {
  try {
    const body = (await response.json()) as { detail?: string; title?: string; errors?: Record<string, string[]> }
    const firstError = body.errors ? Object.values(body.errors).flat()[0] : undefined
    return firstError ?? body.detail ?? body.title ?? `Request failed with status ${response.status}.`
  } catch {
    return `Request failed with status ${response.status}.`
  }
}

/**
 * fetch with the session cookie. Every non-GET request carries the antiforgery token,
 * fetched first if the cookie is missing and retried once if the server rejects it
 * (for example after signing in as someone else).
 */
export async function request<T>(method: string, url: string, body?: unknown, retried = false): Promise<T> {
  const headers: Record<string, string> = {}
  if (body !== undefined) {
    headers['Content-Type'] = 'application/json'
  }

  if (method !== 'GET') {
    if (readXsrfToken() === null) {
      await refreshXsrfToken()
    }
    headers[XSRF_HEADER] = readXsrfToken() ?? ''
  }

  const response = await fetch(url, {
    method,
    headers,
    credentials: 'same-origin',
    body: body === undefined ? undefined : JSON.stringify(body),
  })

  if (response.status === 400 && method !== 'GET' && !retried) {
    const message = await problemMessage(response.clone())
    if (message.toLowerCase().includes('antiforgery')) {
      await refreshXsrfToken()
      return request<T>(method, url, body, true)
    }
  }

  if (!response.ok) {
    throw new ApiError(response.status, await problemMessage(response))
  }

  if (response.status === 204) {
    return undefined as T
  }
  return (await response.json()) as T
}

function filterQuery(filters: RequestFilters, cursor?: string | null): string {
  const params = new URLSearchParams()
  if (cursor) params.set('cursor', cursor)
  if (filters.method) params.set('method', filters.method)
  if (filters.pathPrefix) params.set('pathPrefix', filters.pathPrefix)
  if (filters.from) params.set('from', filters.from)
  if (filters.to) params.set('to', filters.to)
  const query = params.toString()
  return query ? `?${query}` : ''
}

export const api = {
  me: () => request<Me>('GET', '/api/me'),
  logout: () => request<void>('POST', '/auth/logout'),

  endpoints: () => request<Endpoint[]>('GET', '/api/endpoints'),
  createEndpoint: (name: string) => request<Endpoint>('POST', '/api/endpoints', { name }),
  renameEndpoint: (id: string, name: string) => request<Endpoint>('PATCH', `/api/endpoints/${id}`, { name }),
  deleteEndpoint: (id: string) => request<void>('DELETE', `/api/endpoints/${id}`),
  rotateToken: (id: string) => request<Endpoint>('POST', `/api/endpoints/${id}/rotate-token`),
  clearRequests: (id: string) => request<void>('DELETE', `/api/endpoints/${id}/requests`),

  requests: (endpointId: string, filters: RequestFilters, cursor?: string | null) =>
    request<RequestPage>('GET', `/api/endpoints/${endpointId}/requests${filterQuery(filters, cursor)}`),
  requestDetail: (id: string) => request<RequestDetail>('GET', `/api/requests/${id}`),
  deleteRequest: (id: string) => request<void>('DELETE', `/api/requests/${id}`),
  bodyDownloadUrl: (id: string) => `/api/requests/${id}/body`,
}

export type Api = typeof api

/**
 * Swaps the implementation behind `api`, before the app renders. Used by the public demo,
 * which runs the real UI against an in-memory API and never calls the server.
 */
export function replaceApi(implementation: Api): void {
  Object.assign(api, implementation)
}

export { filterQuery as buildFilterQuery }
