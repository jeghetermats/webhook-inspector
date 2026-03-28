export const METHODS = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS'] as const

/** Splits a raw query string into ordered pairs, keeping repeated keys. */
export function queryPairs(rawQuery: string): [string, string][] {
  if (!rawQuery) return []
  return Array.from(new URLSearchParams(rawQuery).entries())
}

export function captureUrl(origin: string, token: string): string {
  return `${origin}/h/${token}`
}

export function formatTime(iso: string): string {
  return new Date(iso).toLocaleString(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  })
}

/** "12 s ago", "5 min ago", "3 h ago", or the date for older times. */
export function formatRelative(iso: string, now: number = Date.now()): string {
  const seconds = Math.max(0, Math.round((now - new Date(iso).getTime()) / 1000))
  if (seconds < 60) return `${seconds} s ago`
  const minutes = Math.round(seconds / 60)
  if (minutes < 60) return `${minutes} min ago`
  const hours = Math.round(minutes / 60)
  if (hours < 24) return `${hours} h ago`
  return new Date(iso).toLocaleDateString(undefined, { month: 'short', day: 'numeric' })
}
