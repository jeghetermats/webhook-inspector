// Mirrors the DTOs in src/WebhookInspector.Api/Api/Dtos.cs (JSON is camelCase).

export interface Me {
  id: string
  login: string
}

export interface Endpoint {
  id: string
  name: string
  captureToken: string
  createdAt: string
  requestCount: number
  bodyBytes: number
}

export interface Header {
  name: string
  value: string
}

export interface RequestSummary {
  id: string
  method: string
  path: string
  rawQuery: string
  receivedAt: string
  contentType: string | null
  bodySize: number
  bodyPreview: string | null
}

export interface RequestPage {
  items: RequestSummary[]
  nextCursor: string | null
}

export interface RequestDetail {
  id: string
  endpointId: string
  method: string
  path: string
  rawQuery: string
  receivedAt: string
  headers: Header[]
  contentType: string | null
  bodySize: number
  bodyBase64: string
  sourceIp: string | null
}

export interface RequestFilters {
  method?: string
  pathPrefix?: string
  from?: string
  to?: string
}

/** Server limits, kept in one place for the UI (see README "Limits"). */
export const LIMITS = {
  maxEndpoints: 10,
  maxRequestsPerEndpoint: 1000,
  maxBodyBytesPerEndpoint: 50 * 1024 * 1024,
  maxNameLength: 100,
} as const
