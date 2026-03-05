import { describe, expect, it } from 'vitest'
import { formatBytes } from './format'

describe('formatBytes', () => {
  it('keeps small values in bytes', () => {
    expect(formatBytes(0)).toBe('0 B')
    expect(formatBytes(1023)).toBe('1023 B')
  })

  it('uses binary units and trims trailing .0', () => {
    expect(formatBytes(1024)).toBe('1 KB')
    expect(formatBytes(1536)).toBe('1.5 KB')
    expect(formatBytes(256 * 1024)).toBe('256 KB')
    expect(formatBytes(50 * 1024 * 1024)).toBe('50 MB')
  })

  it('rejects negative and non-finite values', () => {
    expect(() => formatBytes(-1)).toThrow(RangeError)
    expect(() => formatBytes(Number.NaN)).toThrow(RangeError)
  })
})
