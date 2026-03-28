export type BodyKind = 'empty' | 'json' | 'text' | 'binary'

export interface DecodedBody {
  kind: BodyKind
  bytes: Uint8Array
  /** UTF-8 text for 'json' and 'text', otherwise null. */
  text: string | null
  /** Indented JSON for 'json', otherwise null. */
  prettyJson: string | null
}

export function base64ToBytes(base64: string): Uint8Array {
  const binary = atob(base64)
  const bytes = new Uint8Array(binary.length)
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i)
  }
  return bytes
}

const strictUtf8 = new TextDecoder('utf-8', { fatal: true })

function decodeText(bytes: Uint8Array): string | null {
  let text: string
  try {
    text = strictUtf8.decode(bytes)
  } catch {
    return null
  }
  // Control characters other than tab, newline and carriage return mean binary data.
  for (const char of text) {
    const code = char.codePointAt(0)!
    if ((code < 0x20 && char !== '\t' && char !== '\n' && char !== '\r') || code === 0x7f) {
      return null
    }
  }
  return text
}

/**
 * Decides how a captured body is shown. JSON is detected from the content, not only the
 * Content-Type, because senders often get the header wrong in both directions.
 */
export function decodeBody(bytes: Uint8Array, contentType: string | null): DecodedBody {
  if (bytes.length === 0) {
    return { kind: 'empty', bytes, text: '', prettyJson: null }
  }

  const text = decodeText(bytes)
  if (text === null) {
    return { kind: 'binary', bytes, text: null, prettyJson: null }
  }

  const trimmed = text.trimStart()
  const looksJson = /json/i.test(contentType ?? '') || trimmed.startsWith('{') || trimmed.startsWith('[')
  if (looksJson) {
    try {
      const prettyJson = JSON.stringify(JSON.parse(text), null, 2)
      return { kind: 'json', bytes, text, prettyJson }
    } catch {
      // Claims to be JSON but is not valid: show it as text.
    }
  }

  return { kind: 'text', bytes, text, prettyJson: null }
}

export interface HexRow {
  offset: string
  hex: string
  ascii: string
}

/** Classic hex dump: 16 bytes per row, offset, hex pairs and printable ASCII. */
export function hexRows(bytes: Uint8Array, maxBytes = 64 * 1024): HexRow[] {
  const rows: HexRow[] = []
  const end = Math.min(bytes.length, maxBytes)
  for (let start = 0; start < end; start += 16) {
    const slice = bytes.subarray(start, Math.min(start + 16, end))
    const hex = Array.from(slice, (b) => b.toString(16).padStart(2, '0')).join(' ')
    const ascii = Array.from(slice, (b) => (b >= 0x20 && b < 0x7f ? String.fromCharCode(b) : '.')).join('')
    rows.push({ offset: start.toString(16).padStart(8, '0'), hex, ascii })
  }
  return rows
}
