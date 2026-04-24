import type { InfiniteData } from '@tanstack/react-query'
import type { Endpoint, RequestFilters, RequestPage, RequestReceivedEvent, RequestSummary } from '../api/types'

/** The live view shows the newest page; older requests come from pagination (ADR 0010). */
export const LIVE_PAGE_SIZE = 50

export type RequestListData = InfiniteData<RequestPage, string | null>

/** Applies the same filters as the server, so a live event only appears if a refetch would show it. */
export function matchesFilters(event: Pick<RequestReceivedEvent, 'method' | 'path' | 'receivedAt'>, filters: RequestFilters): boolean {
  if (filters.method && event.method !== filters.method.toUpperCase()) return false
  if (filters.pathPrefix && !event.path.startsWith(filters.pathPrefix)) return false
  const time = Date.parse(event.receivedAt)
  if (filters.from && time < Date.parse(filters.from)) return false
  if (filters.to && time > Date.parse(filters.to)) return false
  return true
}

/** Newest first, ties broken by id, the same order as the server's index. */
function newestFirst(a: RequestSummary, b: RequestSummary): number {
  return Date.parse(b.receivedAt) - Date.parse(a.receivedAt) || (a.id < b.id ? 1 : a.id > b.id ? -1 : 0)
}

/**
 * Inserts a live event into the cached request list:
 * - ignored if it does not match the active filters or is already in the list (de-duplicated on id),
 * - inserted in order on the first page,
 * - when only the first page is loaded, trimmed to the newest LIVE_PAGE_SIZE items, with the
 *   next cursor moved to the last kept item so "Load older" continues without a gap.
 *   When older pages are loaded, nothing is trimmed: trimming would open a gap between pages.
 */
export function applyLiveEvent(
  data: RequestListData | undefined,
  event: RequestReceivedEvent,
  filters: RequestFilters,
): RequestListData | undefined {
  if (!data || data.pages.length === 0) return data
  if (!matchesFilters(event, filters)) return data
  if (data.pages.some((page) => page.items.some((item) => item.id === event.requestId))) return data

  const item: RequestSummary = {
    id: event.requestId,
    method: event.method,
    path: event.path,
    rawQuery: '', // not sent over the live channel; filled in by the next refetch
    receivedAt: event.receivedAt,
    contentType: null,
    bodySize: event.bodySize,
    bodyPreview: null,
    cursor: event.cursor,
  }

  const [first, ...rest] = data.pages
  let items = [item, ...first.items].sort(newestFirst)
  let nextCursor = first.nextCursor
  if (rest.length === 0 && items.length > LIVE_PAGE_SIZE) {
    items = items.slice(0, LIVE_PAGE_SIZE)
    nextCursor = items[items.length - 1].cursor
  }

  return { ...data, pages: [{ items, nextCursor }, ...rest] }
}

/** Keeps the usage counter in step with live events until the next refetch. */
export function applyUsage(endpoints: Endpoint[] | undefined, event: RequestReceivedEvent): Endpoint[] | undefined {
  return endpoints?.map((endpoint) =>
    endpoint.id === event.endpointId
      ? { ...endpoint, requestCount: endpoint.requestCount + 1, bodyBytes: endpoint.bodyBytes + event.bodySize }
      : endpoint,
  )
}
