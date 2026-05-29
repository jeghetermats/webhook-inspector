import { useMemo } from 'react'
import { type BodyView, type DecodedBody, hexRows } from '../lib/body'
import { formatBytes } from '../lib/format'

const HEX_LIMIT = 64 * 1024

export function BodySection({ body, view, onViewChange }: { body: DecodedBody; view: BodyView; onViewChange: (view: BodyView) => void }) {
  if (body.kind === 'empty') {
    return <p className="wire-empty">No body</p>
  }

  const views: { id: BodyView; label: string }[] = [
    ...(body.kind === 'json' ? [{ id: 'pretty' as const, label: 'Formatted' }] : []),
    ...(body.kind !== 'binary' ? [{ id: 'text' as const, label: 'Text' }] : []),
    { id: 'hex', label: 'Hex' },
  ]

  return (
    <div className="body">
      {views.length > 1 ? (
        <div className="body-views" role="group" aria-label="Show body as">
          {views.map((v) => (
            <button
              key={v.id}
              type="button"
              aria-pressed={view === v.id}
              className={view === v.id ? 'body-view body-view-active' : 'body-view'}
              onClick={() => onViewChange(v.id)}
            >
              {v.label}
            </button>
          ))}
        </div>
      ) : null}

      {view === 'pretty' && body.prettyJson !== null ? <pre className="wire-body">{body.prettyJson}</pre> : null}
      {view === 'text' && body.text !== null ? <pre className="wire-body">{body.text}</pre> : null}
      {view === 'hex' ? <HexView bytes={body.bytes} /> : null}
    </div>
  )
}

function HexView({ bytes }: { bytes: Uint8Array }) {
  const rows = useMemo(() => hexRows(bytes, HEX_LIMIT), [bytes])
  return (
    <>
      <pre className="wire-body hex">
        {rows.map((row) => `${row.offset}  ${row.hex.padEnd(47)}  ${row.ascii}`).join('\n')}
      </pre>
      {bytes.length > HEX_LIMIT ? (
        <p className="muted small">Showing the first {formatBytes(HEX_LIMIT)}. Download the body to see all of it.</p>
      ) : null}
    </>
  )
}
