const UNITS = ['B', 'KB', 'MB', 'GB'] as const

const pluralRules = new Intl.PluralRules('en')

/** "1 request", "2 requests". Uses the language's plural rules, not a hand-written "s". */
export function countOf(count: number, one: string, other: string): string {
  return `${count} ${pluralRules.select(count) === 'one' ? one : other}`
}

/** Formats a byte count with binary units, e.g. 1536 -> "1.5 KB". */
export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) {
    throw new RangeError(`Invalid byte count: ${bytes}`)
  }
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < UNITS.length - 1) {
    value /= 1024
    unit++
  }
  const rounded = unit === 0 ? value.toString() : value.toFixed(1).replace(/\.0$/, '')
  return `${rounded} ${UNITS[unit]}`
}
