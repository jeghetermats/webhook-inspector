import { describe, expect, it } from 'vitest'
import { formatRelative, queryPairs } from './http'

describe('queryPairs', () => {
  it('keeps order and repeated keys, and decodes values', () => {
    expect(queryPairs('a=1&a=2&b=x%20y&flag')).toEqual([
      ['a', '1'],
      ['a', '2'],
      ['b', 'x y'],
      ['flag', ''],
    ])
  })

  it('returns nothing for an empty query', () => {
    expect(queryPairs('')).toEqual([])
  })
})

describe('formatRelative', () => {
  const now = Date.parse('2026-03-28T12:00:00Z')

  it('uses seconds, minutes and hours', () => {
    expect(formatRelative('2026-03-28T11:59:50Z', now)).toBe('10 s ago')
    expect(formatRelative('2026-03-28T11:55:00Z', now)).toBe('5 min ago')
    expect(formatRelative('2026-03-28T09:00:00Z', now)).toBe('3 h ago')
  })

  it('never shows negative times for clock skew', () => {
    expect(formatRelative('2026-03-28T12:00:05Z', now)).toBe('0 s ago')
  })
})
