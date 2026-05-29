import type { UseQueryResult } from '@tanstack/react-query'
import { type FormEvent, useRef, useState } from 'react'
import { NavLink, useNavigate } from 'react-router'
import { useEndpointMutations } from '../api/hooks'
import { type Endpoint, LIMITS } from '../api/types'

export function EndpointRail({ endpoints, selectedId }: { endpoints: UseQueryResult<Endpoint[]>; selectedId?: string }) {
  const { create } = useEndpointMutations()
  const navigate = useNavigate()
  const [name, setName] = useState('')
  const [nameError, setNameError] = useState<string | null>(null)
  const input = useRef<HTMLInputElement>(null)
  const count = endpoints.data?.length ?? 0
  const full = count >= LIMITS.maxEndpoints

  function submit(event: FormEvent) {
    event.preventDefault()
    const trimmed = name.trim()
    if (!trimmed) {
      setNameError('Enter a name for the endpoint, for example GitHub push.')
      input.current?.focus()
      return
    }
    setNameError(null)
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
      {endpoints.isError ? (
        <p className="error" role="alert">
          Could not load your endpoints. Check your connection and reload the page.
        </p>
      ) : null}

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
            ref={input}
            id="new-endpoint"
            className="input"
            value={name}
            onChange={(e) => {
              setName(e.target.value)
              if (nameError) setNameError(null)
            }}
            aria-invalid={nameError ? true : undefined}
            aria-describedby={nameError ? 'new-endpoint-error' : undefined}
            placeholder="e.g. GitHub push"
            maxLength={LIMITS.maxNameLength}
            disabled={full}
          />
          <button type="submit" className="button" disabled={full || create.isPending}>
            Create
          </button>
        </div>
        {full ? <p className="muted small">You have {LIMITS.maxEndpoints} endpoints, the maximum. Delete one to create another.</p> : null}
        {nameError ? (
          <p id="new-endpoint-error" className="error small">
            {nameError}
          </p>
        ) : null}
        {create.isError ? (
          <p className="error small" role="alert">
            {create.error.message}
          </p>
        ) : null}
      </form>
    </nav>
  )
}
