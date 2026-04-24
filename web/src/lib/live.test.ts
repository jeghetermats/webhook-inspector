import { describe, expect, it } from 'vitest'
import type { RequestReceivedEvent, RequestSummary } from '../api/types'
import { LIVE_PAGE_SIZE, type RequestListData, applyLiveEvent, applyUsage, matchesFilters } from './live'

const base = Date.parse('2026-04-24T12:00:00Z')
const at = (seconds: number) => new Date(base + seconds * 1000).toISOString()

function summary(n: number, extra: Partial<RequestSummary> = {}): RequestSummary {
  return {
    id: `00000000-0000-0000-0000-${n.toString().padStart(12, '0')}`,
    method: 'POST',
    path: `/r/${n}`,
    rawQuery: '',
    receivedAt: at(n),
    contentType: 'application/json',
    bodySize: 2,
    bodyPreview: '{}',
    cursor: `cursor-${n}`,
    ...extra,
  }
}

function event(n: number, extra: Partial<RequestReceivedEvent> = {}): RequestReceivedEvent {
  const s = summary(n)
  return { endpointId: 'e1', requestId: s.id, method: s.method, path: s.path, receivedAt: s.receivedAt, bodySize: 5, cursor: s.cursor, ...extra }
}

/** One page with items numbered from `newest` down. */
function listOf(newest: number, count: number, nextCursor: string | null = null, extraPages = 0): RequestListData {
  const items = Array.from({ length: count }, (_, i) => summary(newest - i))
  const pages = [{ items, nextCursor }]
  for (let p = 0; p < extraPages; p++) {
    pages.push({ items: [summary(newest - count - p)], nextCursor: null })
  }
  return { pages, pageParams: pages.map((_, i) => (i === 0 ? null : `p${i}`)) }
}

const ids = (data: RequestListData | undefined) => data!.pages.flatMap((p) => p.items.map((i) => Number(i.id.slice(-12))))

describe('matchesFilters', () => {
  it('matches everything without filters', () => {
    expect(matchesFilters(event(1), {})).toBe(true)
  })

  it('applies method, path prefix and time like the server', () => {
    const e = event(10, { method: 'POST', path: '/github/push' })
    expect(matchesFilters(e, { method: 'post' })).toBe(true)
    expect(matchesFilters(e, { method: 'GET' })).toBe(false)
    expect(matchesFilters(e, { pathPrefix: '/github' })).toBe(true)
    expect(matchesFilters(e, { pathPrefix: '/GitHub' })).toBe(false) // case-sensitive, like the server
    expect(matchesFilters(e, { from: at(10), to: at(10) })).toBe(true)
    expect(matchesFilters(e, { from: at(11) })).toBe(false)
    expect(matchesFilters(e, { to: at(9) })).toBe(false)
  })
})

describe('applyLiveEvent', () => {
  it('puts a new request at the top', () => {
    expect(ids(applyLiveEvent(listOf(3, 3), event(4), {}))).toEqual([4, 3, 2, 1])
  })

  it('ignores an event it already has, on any loaded page', () => {
    const data = listOf(3, 3, 'c', 1) // page 2 holds item 0
    expect(applyLiveEvent(data, event(3), {})).toBe(data)
    expect(applyLiveEvent(data, event(0), {})).toBe(data)
  })

  it('ignores events that do not match the active filters', () => {
    const data = listOf(3, 3)
    expect(applyLiveEvent(data, event(4, { method: 'GET' }), { method: 'POST' })).toBe(data)
  })

  it('keeps newest-first order when events arrive out of order', () => {
    const once = applyLiveEvent(listOf(3, 3), event(6), {})
    expect(ids(applyLiveEvent(once, event(5), {}))).toEqual([6, 5, 3, 2, 1])
  })

  it('trims a single page to the newest 50 and moves the cursor to the last kept item', () => {
    const full = listOf(100, LIVE_PAGE_SIZE, 'old-cursor')
    const next = applyLiveEvent(full, event(101), {})!
    expect(next.pages).toHaveLength(1)
    expect(next.pages[0].items).toHaveLength(LIVE_PAGE_SIZE)
    expect(ids(next)[0]).toBe(101)
    expect(ids(next).at(-1)).toBe(52) // 51 was trimmed and is reachable through the cursor
    expect(next.pages[0].nextCursor).toBe('cursor-52')
  })

  it('shows "Load older" after trimming even if everything fit before', () => {
    const next = applyLiveEvent(listOf(50, LIVE_PAGE_SIZE, null), event(51), {})!
    expect(next.pages[0].nextCursor).toBe('cursor-2')
  })

  it('does not trim when older pages are loaded, so pages never get a gap', () => {
    const next = applyLiveEvent(listOf(100, LIVE_PAGE_SIZE, 'c', 1), event(101), {})!
    expect(next.pages[0].items).toHaveLength(LIVE_PAGE_SIZE + 1)
    expect(next.pages[0].nextCursor).toBe('c')
  })

  it('does nothing before the list has loaded', () => {
    expect(applyLiveEvent(undefined, event(1), {})).toBeUndefined()
  })
})

describe('applyUsage', () => {
  it('counts the event on its endpoint only', () => {
    const endpoints = [
      { id: 'e1', name: 'a', captureToken: 't', createdAt: at(0), requestCount: 1, bodyBytes: 10 },
      { id: 'e2', name: 'b', captureToken: 'u', createdAt: at(0), requestCount: 0, bodyBytes: 0 },
    ]
    expect(applyUsage(endpoints, event(1))).toEqual([
      { ...endpoints[0], requestCount: 2, bodyBytes: 15 },
      endpoints[1],
    ])
  })
})
