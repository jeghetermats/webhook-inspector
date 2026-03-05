const UNITS = ['B', 'KB', 'MB', 'GB'] as const

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
