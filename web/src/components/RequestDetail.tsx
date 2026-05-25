import { useMemo, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router'
import { api } from '../api/client'
import { useDeleteRequest, useRequestDetail } from '../api/hooks'
import type { Endpoint, RequestDetail } from '../api/types'
import { type BodyView, base64ToBytes, decodeBody, initialView } from '../lib/body'
import { toCurl } from '../lib/curl'
import { formatBytes } from '../lib/format'
import { captureUrl, formatTime, queryPairs } from '../lib/http'
import { CopyButton } from './CopyButton'
import { BodySection } from './BodyView'
import { MethodTag } from './MethodTag'
import { ReplaySection } from './ReplaySection'

export function RequestDetailPane({ endpoint, requestId }: { endpoint: Endpoint; requestId: string }) {
  const detail = useRequestDetail(requestId)
  const [params] = useSearchParams()
  const search = params.toString() ? `?${params.toString()}` : ''

  return (
    <section className="pane pane-detail" aria-label="Request">
      <Link to={`/endpoints/${endpoint.id}${search}`} className="back-link">
        All requests
      </Link>
      {detail.isPending ? <p className="muted">Loading…</p> : null}
      {detail.isError ? <p className="error">This request does not exist or was deleted.</p> : null}
      {detail.data ? <RequestView key={detail.data.id} request={detail.data} endpoint={endpoint} backTo={`/endpoints/${endpoint.id}${search}`} /> : null}
    </section>
  )
}

function RequestView({ request, endpoint, backTo }: { request: RequestDetail; endpoint: Endpoint; backTo: string }) {
  const body = useMemo(() => decodeBody(base64ToBytes(request.bodyBase64), request.contentType), [request])
  const [view, setView] = useState<BodyView>(initialView(body))
  const remove = useDeleteRequest(endpoint.id)
  const navigate = useNavigate()
  const query = queryPairs(request.rawQuery)
  const target = `${request.path}${request.rawQuery ? `?${request.rawQuery}` : ''}`
  const curl = toCurl(request, {
    url: `${captureUrl(window.location.origin, endpoint.captureToken)}${target === '/' ? '' : target}`,
    bodyText: body.kind === 'binary' ? null : body.text,
  })

  return (
    <article className="request">
      <header className="request-head">
        <div className="request-actions">
          <CopyButton text={curl} label="Copy as curl" />
          <a className="button" href={api.bodyDownloadUrl(request.id)} download>
            Download body
          </a>
          <button
            type="button"
            className="button button-danger"
            disabled={remove.isPending}
            onClick={() => {
              if (window.confirm('Delete this request?')) {
                remove.mutate(request.id, { onSuccess: () => navigate(backTo) })
              }
            }}
          >
            Delete
          </button>
        </div>
        <dl className="facts">
          <div>
            <dt>Received</dt>
            <dd>
              <time dateTime={request.receivedAt}>{formatTime(request.receivedAt)}</time>
            </dd>
          </div>
          <div>
            <dt>Body</dt>
            <dd>
              {formatBytes(request.bodySize)}
              {body.kind !== 'empty' ? `, ${body.kind === 'json' ? 'JSON' : body.kind}` : ''}
            </dd>
          </div>
          {request.sourceIp ? (
            <div>
              <dt>From</dt>
              <dd>{request.sourceIp}</dd>
            </div>
          ) : null}
        </dl>
      </header>

      {/* The request as it would look on the wire. All captured values are rendered as
          text nodes, never as HTML. */}
      <div className="wire" role="region" aria-label="Raw request">
        <div className="wire-line wire-request">
          <MethodTag method={request.method} /> <span className="wire-target">{target}</span>
        </div>
        {request.headers.map((header, index) => (
          <div className="wire-line" key={`${header.name}-${index}`}>
            <span className="wire-name">{header.name}</span>: <span className="wire-value">{header.value}</span>
          </div>
        ))}
        <div className="wire-gap" aria-hidden="true" />
        <BodySection body={body} view={view} onViewChange={setView} />
      </div>

      {query.length > 0 ? (
        <section className="query">
          <h3 className="section-title">Query parameters</h3>
          <table className="pairs">
            <tbody>
              {query.map(([key, value], index) => (
                <tr key={`${key}-${index}`}>
                  <th scope="row">{key}</th>
                  <td>{value}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      ) : null}

      <ReplaySection requestId={request.id} />
    </article>
  )
}
