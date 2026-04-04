import type { UseQueryResult } from '@tanstack/react-query'
import { type FormEvent, useState } from 'react'
import { NavLink, useNavigate } from 'react-router'
import { useEndpointMutations } from '../api/hooks'
import { type Endpoint, LIMITS } from '../api/types'

export function EndpointRail({ endpoints, selectedId }: { endpoints: UseQueryResult<Endpoint[]>; selectedId?: string }) {
  const { create } = useEndpointMutations()
  const navigate = useNavigate()
  const [name, setName] = useState('')
  const count = endpoints.data?.length ?? 0
  const full = count >= LIMITS.maxEndpoints

  function submit(event: FormEvent) {
    event.preventDefault()
    const trimmed = name.trim()
    if (!trimmed) return
    create.mutate(trimmed, {
      onSuccess: (endpoint) => {
        setName('')
        navigate(`/endpoints/${endpoint.id}`)
      },
    })
  }

  return (
    <nav className="pane pane-rail" aria-label="Endpoints">
      <h2 className="pane-title">Endpoints</h2>

      {endpoints.isPending ? <p className="muted">Loading…</p> : null}
      {endpoints.isError ? <p className="error">Could not load your endpoints.</p> : null}

      <ul className="rail-list">
        {endpoints.data?.map((endpoint) => (
          <li key={endpoint.id}>
            <NavLink
              to={`/endpoints/${endpoint.id}`}
              className={endpoint.id === selectedId ? 'rail-item rail-item-active' : 'rail-item'}
              aria-current={endpoint.id === selectedId ? 'page' : undefined}
            >
              <span className="rail-name">{endpoint.name}</span>
              <span className="rail-count">{endpoint.requestCount}</span>
            </NavLink>
          </li>
        ))}
      </ul>

      <form className="rail-create" onSubmit={submit}>
        <label htmlFor="new-endpoint" className="field-label">
          New endpoint
        </label>
        <div className="field-row">
          <input
            id="new-endpoint"
            className="input"
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="e.g. GitHub push"
            maxLength={LIMITS.maxNameLength}
            disabled={full}
          />
          <button type="submit" className="button" disabled={full || !name.trim() || create.isPending}>
            Create
          </button>
        </div>
        {full ? <p className="muted small">You have {LIMITS.maxEndpoints} endpoints, the maximum. Delete one to create another.</p> : null}
        {create.isError ? <p className="error small">{create.error.message}</p> : null}
      </form>
    </nav>
  )
}
