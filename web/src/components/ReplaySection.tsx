import { useMemo, useState } from 'react'
import { useReplay, useReplays } from '../api/hooks'
import { LIMITS, type ReplayAttempt } from '../api/types'
import { type BodyView, base64ToBytes, decodeBody, initialView } from '../lib/body'
import { formatBytes } from '../lib/format'
import { formatTime } from '../lib/http'
import { BodySection } from './BodyView'

const TARGET_KEY = 'wi-replay-target'

// The last target is a small convenience for this browser only; storage may be unavailable.
function readLastTarget(): string {
  try {
    return localStorage.getItem(TARGET_KEY) ?? ''
  } catch {
    return ''
  }
}

function rememberTarget(url: string): void {
  try {
    localStorage.setItem(TARGET_KEY, url)
  } catch {
    // Not remembering is fine.
  }
}

/**
 * Sends the captured request again to a URL the user chooses, and lists earlier attempts.
 * The server decides what may be reached (ADR 0013); the UI only explains the rules.
 */
export function ReplaySection({ requestId }: { requestId: string }) {
  const attempts = useReplays(requestId)
  const replay = useReplay(requestId)
  const [target, setTarget] = useState(readLastTarget)

  return (
    <section className="replay" aria-labelledby="replay-title">
      <h3 className="section-title" id="replay-title">
        Replay
      </h3>
      <p className="muted small">
        Send this request again to a server you choose. Only public addresses on ports 80 and 443 can be reached.
        Cookies and Authorization headers are not sent, and redirects are not followed.
      </p>
      <form
        className="replay-form"
        onSubmit={(event) => {
          event.preventDefault()
          const url = target.trim()
          if (!url) return
          rememberTarget(url)
          replay.mutate(url)
        }}
      >
        <label className="field-label" htmlFor="replay-target">
          Target URL
        </label>
        <div className="field-row">
          <input
            id="replay-target"
            className="input"
            type="url"
            inputMode="url"
            autoComplete="url"
            placeholder="https://example.com/webhooks"
            maxLength={2048}
            required
            value={target}
            onChange={(event) => setTarget(event.target.value)}
          />
          <button type="submit" className="button" disabled={replay.isPending}>
            {replay.isPending ? 'Sending…' : 'Send'}
          </button>
        </div>
      </form>
      {replay.isError ? (
        <p className="error small" role="alert">
          {replay.error.message}
        </p>
      ) : null}

      {attempts.data && attempts.data.length > 0 ? (
        <ol className="replay-list" aria-label="Replay attempts, newest first">
          {attempts.data.map((attempt) => (
            <li key={attempt.id}>
              <AttemptView attempt={attempt} open={attempt.id === replay.data?.id} />
            </li>
          ))}
        </ol>
      ) : null}
    </section>
  )
}

function statusLevel(attempt: ReplayAttempt): 'ok' | 'redirect' | 'failed' {
  if (attempt.statusCode === null || attempt.statusCode >= 400) return 'failed'
  return attempt.statusCode >= 300 ? 'redirect' : 'ok'
}

function AttemptView({ attempt, open }: { attempt: ReplayAttempt; open: boolean }) {
  return (
    <details className="replay-attempt" open={open}>
      <summary className="replay-summary">
        <span className="replay-status" data-level={statusLevel(attempt)}>
          {attempt.statusCode ?? 'Failed'}
        </span>
        <span className="replay-url">{attempt.targetUrl}</span>
        <span className="replay-meta">
          <time dateTime={attempt.startedAt}>{formatTime(attempt.startedAt)}</time>, {attempt.durationMs} ms
        </span>
      </summary>
      <p className="replay-sent">
        Sent to <code className="replay-sent-url">{attempt.targetUrl}</code>
      </p>
      {attempt.error !== null ? (
        <p className="replay-error">{attempt.error}</p>
      ) : (
        <ResponseView attempt={attempt} />
      )}
    </details>
  )
}

function ResponseView({ attempt }: { attempt: ReplayAttempt }) {
  const contentType = attempt.responseHeaders.find((h) => h.name.toLowerCase() === 'content-type')?.value ?? null
  const body = useMemo(
    () => decodeBody(base64ToBytes(attempt.responseBodyBase64), contentType),
    [attempt.responseBodyBase64, contentType],
  )
  const [view, setView] = useState<BodyView>(initialView(body))

  // The response is shown as text, exactly like a captured request: never rendered as HTML.
  return (
    <div className="wire wire-response" role="region" aria-label="Response">
      <div className="wire-line wire-request">HTTP {attempt.statusCode}</div>
      {attempt.responseHeaders.map((header, index) => (
        <div className="wire-line" key={`${header.name}-${index}`}>
          <span className="wire-name">{header.name}</span>: <span className="wire-value">{header.value}</span>
        </div>
      ))}
      <div className="wire-gap" aria-hidden="true" />
      <BodySection body={body} view={view} onViewChange={setView} />
      {attempt.responseTruncated ? (
        <p className="muted small">Only the first {formatBytes(LIMITS.replayResponseBytes)} of the response was kept.</p>
      ) : null}
    </div>
  )
}
