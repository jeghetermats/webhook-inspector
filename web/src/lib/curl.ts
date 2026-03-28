import type { RequestDetail } from '../api/types'

/**
 * Headers left out of the curl command: curl computes them itself, they describe the
 * original connection, or they were added by the hosting platform's proxy.
 */
const SKIPPED_HEADERS = new Set([
  'host',
  'content-length',
  'connection',
  'keep-alive',
  'transfer-encoding',
  'te',
  'upgrade',
  'proxy-connection',
  'accept-encoding',
  'x-request-id',
  'x-arr-ssl',
  'x-arr-log-id',
])

const SKIPPED_PREFIXES = ['x-forwarded-', 'x-envoy-', 'x-original-']

function isSkipped(name: string): boolean {
  const lower = name.toLowerCase()
  return SKIPPED_HEADERS.has(lower) || SKIPPED_PREFIXES.some((prefix) => lower.startsWith(prefix))
}

/** Quotes a value for POSIX shells: single quotes, with embedded ' written as '\''. */
export function shellQuote(value: string): string {
  return `'${value.replaceAll("'", `'\\''`)}'`
}

export interface CurlOptions {
  /** Full URL the command sends to, e.g. the capture URL or a local server. */
  url: string
  /** Body as text, or null when it is binary and has to come from a downloaded file. */
  bodyText: string | null
}

/** Rebuilds a captured request as a curl command for bash/zsh. */
export function toCurl(request: Pick<RequestDetail, 'id' | 'method' | 'headers' | 'bodySize'>, options: CurlOptions): string {
  // One flag with its value per line, joined with shell line continuations.
  const parts: string[] = ['curl']

  if (request.method === 'HEAD') {
    parts.push('--head')
  } else if (request.method !== 'GET' || request.bodySize > 0) {
    parts.push(`-X ${request.method}`)
  }

  parts.push(shellQuote(options.url))

  for (const header of request.headers) {
    if (!isSkipped(header.name)) {
      parts.push(`-H ${shellQuote(`${header.name}: ${header.value}`)}`)
    }
  }

  if (request.bodySize > 0 && request.method !== 'HEAD') {
    parts.push(`--data-binary ${options.bodyText !== null ? shellQuote(options.bodyText) : `@request-${request.id}.bin`}`)
  }

  return parts.join(' \\\n  ')
}
