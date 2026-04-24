import { useQueryClient } from '@tanstack/react-query'
import { type FormEvent, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router'
import { keys, useEndpointMutations, useRequestList } from '../api/hooks'
import { type Endpoint, LIMITS, type RequestFilters } from '../api/types'
import { useLiveRequests } from '../live/useLiveRequests'
import type { LiveStatus } from '../live/context'
import { formatBytes } from '../lib/format'
import { METHODS, captureUrl, formatRelative } from '../lib/http'
import { CopyButton } from './CopyButton'
import { MethodTag } from './MethodTag'

function filtersFrom(params: URLSearchParams): RequestFilters {
  return {
    method: params.get('method') ?? undefined,
    pathPrefix: params.get('path') ?? undefined,
    from: params.get('from') ?? undefined,
    to: params.get('to') ?? undefined,
  }
}

/** datetime-local value (local time, no zone) to ISO with offset, and back. */
function localToIso(value: string): string | undefined {
  return value ? new Date(value).toISOString() : undefined
}

function isoToLocal(iso: string | undefined): string {
  if (!iso) return ''
  const date = new Date(iso)
  return new Date(date.getTime() - date.getTimezoneOffset() * 60_000).toISOString().slice(0, 16)
}

export function RequestsPane({ endpoint, selectedRequestId }: { endpoint: Endpoint; selectedRequestId?: string }) {
  const [params, setParams] = useSearchParams()
  const filters = filtersFrom(params)
  const liveStatus = useLiveRequests(endpoint.id, filters)
  const list = useRequestList(endpoint.id, filters, liveStatus === 'live')
  const queryClient = useQueryClient()
  const search = params.toString() ? `?${params.toString()}` : ''
  const items = list.data?.pages.flatMap((page) => page.items) ?? []
  const hasFilters = Object.values(filters).some(Boolean)

  return (
    <section className="pane pane-requests" aria-label={`Requests for ${endpoint.name}`}>
      <Link to="/" className="back-link">
        All endpoints
      </Link>
      <EndpointHeader endpoint={endpoint} />

      <FilterBar
        filters={filters}
        onChange={(next) => {
          const updated = new URLSearchParams()
          if (next.method) updated.set('method', next.method)
          if (next.pathPrefix) updated.set('path', next.pathPrefix)
          if (next.from) updated.set('from', next.from)
          if (next.to) updated.set('to', next.to)
          setParams(updated, { replace: true })
        }}
      />

      <div className="list-toolbar">
        <span className="muted small">
          {list.isSuccess ? `${items.length}${list.hasNextPage ? '+' : ''} ${hasFilters ? 'matching ' : ''}requests` : ' '}
        </span>
        <LiveIndicator status={liveStatus} />
        <button
          type="button"
          className="button button-quiet"
          onClick={() =>
            // Usage counts live on the endpoint, so refresh it together with the list.
            Promise.all([
              queryClient.invalidateQueries({ queryKey: keys.requests(endpoint.id) }),
              queryClient.invalidateQueries({ queryKey: keys.endpoints }),
            ])
          }
          disabled={list.isFetching}
        >
          {list.isFetching && !list.isFetchingNextPage ? 'Refreshing…' : 'Refresh'}
        </button>
      </div>

      {list.isError ? <p className="error">Could not load requests: {list.error.message}</p> : null}

      {list.isSuccess && items.length === 0 ? (
        hasFilters ? (
          <p className="empty-note">No requests match these filters.</p>
        ) : (
          <div className="empty-note">
            <p>No requests yet. Send one to try it:</p>
            <pre className="wire wire-snippet">{`curl -X POST ${captureUrl(window.location.origin, endpoint.captureToken)}/hello \\\n  -H 'Content-Type: application/json' \\\n  -d '{"hello":"world"}'`}</pre>
          </div>
        )
      ) : null}

      <ol className="request-list">
        {items.map((item) => (
          <li key={item.id}>
            <Link
              to={`/endpoints/${endpoint.id}/requests/${item.id}${search}`}
              className={item.id === selectedRequestId ? 'request-row request-row-active' : 'request-row'}
              aria-current={item.id === selectedRequestId ? 'page' : undefined}
            >
              <MethodTag method={item.method} />
              <span className="request-path">
                {item.path}
                {item.rawQuery ? <span className="request-query">?{item.rawQuery}</span> : null}
              </span>
              <span className="request-meta">
                <time dateTime={item.receivedAt} title={new Date(item.receivedAt).toLocaleString()}>
                  {formatRelative(item.receivedAt)}
                </time>
                <span>{formatBytes(item.bodySize)}</span>
              </span>
              {item.bodyPreview ? <span className="request-preview">{item.bodyPreview}</span> : null}
            </Link>
          </li>
        ))}
      </ol>

      {list.hasNextPage ? (
        <button
          type="button"
          className="button load-more"
          onClick={() => list.fetchNextPage()}
          disabled={list.isFetchingNextPage}
        >
          {list.isFetchingNextPage ? 'Loading…' : 'Load older requests'}
        </button>
      ) : null}
    </section>
  )
}

function EndpointHeader({ endpoint }: { endpoint: Endpoint }) {
  const { rename, rotate, clear, remove } = useEndpointMutations()
  const navigate = useNavigate()
  const [editing, setEditing] = useState(false)
  const [name, setName] = useState(endpoint.name)
  const url = captureUrl(window.location.origin, endpoint.captureToken)
  const requestShare = endpoint.requestCount / LIMITS.maxRequestsPerEndpoint
  const byteShare = endpoint.bodyBytes / LIMITS.maxBodyBytesPerEndpoint
  const usage = Math.min(1, Math.max(requestShare, byteShare))
  const error = rename.error ?? rotate.error ?? clear.error ?? remove.error

  function submitRename(event: FormEvent) {
    event.preventDefault()
    rename.mutate({ id: endpoint.id, name: name.trim() }, { onSuccess: () => setEditing(false) })
  }

  return (
    <header className="endpoint-header">
      {editing ? (
        <form className="field-row" onSubmit={submitRename}>
          <label htmlFor="rename" className="visually-hidden">
            Endpoint name
          </label>
          <input
            id="rename"
            className="input"
            value={name}
            onChange={(e) => setName(e.target.value)}
            maxLength={LIMITS.maxNameLength}
            autoFocus
          />
          <button type="submit" className="button" disabled={!name.trim() || rename.isPending}>
            Save
          </button>
          <button type="button" className="button button-quiet" onClick={() => setEditing(false)}>
            Cancel
          </button>
        </form>
      ) : (
        <div className="endpoint-title-row">
          <h2 className="endpoint-title">{endpoint.name}</h2>
          <button
            type="button"
            className="button button-quiet"
            onClick={() => {
              setName(endpoint.name)
              setEditing(true)
            }}
          >
            Rename
          </button>
        </div>
      )}

      <div className="capture-url">
        <code className="capture-url-text">{url}</code>
        <CopyButton text={url} label="Copy URL" />
      </div>

      <div className="usage" title="Requests are rejected with 429 when either limit is reached.">
        <div className="usage-bar" role="meter" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(usage * 100)} aria-label="Storage used">
          <span className="usage-fill" style={{ inlineSize: `${usage * 100}%` }} data-level={usage >= 0.9 ? 'high' : 'normal'} />
        </div>
        <span className="muted small">
          {endpoint.requestCount} of {LIMITS.maxRequestsPerEndpoint} requests, {formatBytes(endpoint.bodyBytes)} of{' '}
          {formatBytes(LIMITS.maxBodyBytesPerEndpoint)}
        </span>
      </div>

      <div className="endpoint-actions">
        <button
          type="button"
          className="button button-quiet"
          disabled={rotate.isPending}
          onClick={() => {
            if (window.confirm('Get a new URL? The current URL stops working immediately. Stored requests are kept.')) {
              rotate.mutate(endpoint.id)
            }
          }}
        >
          New URL
        </button>
        <button
          type="button"
          className="button button-quiet"
          disabled={clear.isPending || endpoint.requestCount === 0}
          onClick={() => {
            if (window.confirm(`Delete all ${endpoint.requestCount} requests for "${endpoint.name}"?`)) {
              clear.mutate(endpoint.id, { onSuccess: () => navigate(`/endpoints/${endpoint.id}`) })
            }
          }}
        >
          Delete all requests
        </button>
        <button
          type="button"
          className="button button-danger"
          disabled={remove.isPending}
          onClick={() => {
            if (window.confirm(`Delete the endpoint "${endpoint.name}" and all its requests? The URL stops working.`)) {
              remove.mutate(endpoint.id, { onSuccess: () => navigate('/') })
            }
          }}
        >
          Delete endpoint
        </button>
      </div>
      {error ? <p className="error small">{error.message}</p> : null}
    </header>
  )
}

function FilterBar({ filters, onChange }: { filters: RequestFilters; onChange: (filters: RequestFilters) => void }) {
  const [path, setPath] = useState(filters.pathPrefix ?? '')

  return (
    <form
      className="filters"
      onSubmit={(event) => {
        event.preventDefault()
        onChange({ ...filters, pathPrefix: path || undefined })
      }}
    >
      <label className="filter">
        <span className="field-label">Method</span>
        <select
          className="input"
          value={filters.method ?? ''}
          onChange={(e) => onChange({ ...filters, method: e.target.value || undefined })}
        >
          <option value="">Any</option>
          {METHODS.map((m) => (
            <option key={m} value={m}>
              {m}
            </option>
          ))}
        </select>
      </label>
      <label className="filter filter-grow">
        <span className="field-label">Path starts with</span>
        <input
          className="input"
          value={path}
          onChange={(e) => setPath(e.target.value)}
          onBlur={() => onChange({ ...filters, pathPrefix: path || undefined })}
          placeholder="/github"
        />
      </label>
      <label className="filter">
        <span className="field-label">From</span>
        <input
          type="datetime-local"
          className="input"
          value={isoToLocal(filters.from)}
          onChange={(e) => onChange({ ...filters, from: localToIso(e.target.value) })}
        />
      </label>
      <label className="filter">
        <span className="field-label">To</span>
        <input
          type="datetime-local"
          className="input"
          value={isoToLocal(filters.to)}
          onChange={(e) => onChange({ ...filters, to: localToIso(e.target.value) })}
        />
      </label>
      {Object.values(filters).some(Boolean) ? (
        <button
          type="button"
          className="button button-quiet filter-clear"
          onClick={() => {
            setPath('')
            onChange({})
          }}
        >
          Clear filters
        </button>
      ) : null}
    </form>
  )
}

const LIVE_TEXT: Record<LiveStatus, string> = {
  connecting: 'Connecting…',
  live: 'Live',
  reconnecting: 'Reconnecting…',
  offline: 'Not live, use Refresh',
}

function LiveIndicator({ status }: { status: LiveStatus }) {
  return (
    <span className="live-indicator" data-status={status} role="status" title="New requests appear automatically while live.">
      {LIVE_TEXT[status]}
    </span>
  )
}
