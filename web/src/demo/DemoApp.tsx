import { Link, Route, Routes, useLocation, useNavigate } from 'react-router'
import { useEndpoints } from '../api/hooks'
import { Workspace } from '../components/Workspace'
import { DemoLiveProvider } from './DemoLiveProvider'
import type { DemoBackend } from './demoApi'

/**
 * The public, read-only-in-spirit demo at /demo: the real UI on made-up data. Everything
 * happens in the browser and is reset on reload; no request reaches the server.
 */
export function DemoApp({ backend }: { backend: DemoBackend }) {
  return (
    <div className="shell">
      <header className="topbar">
        <Link to="/" className="brand">
          Webhook Inspector
        </Link>
        <p className="topbar-note topbar-demo">
          Demo with made-up data. It runs only in your browser and resets when you reload.
        </p>
        <div className="topbar-user">
          <SimulateButton backend={backend} />
          <a className="button" href="/auth/login">
            Sign in with GitHub
          </a>
        </div>
      </header>
      <DemoLiveProvider backend={backend}>
        <Routes>
          <Route path="/" element={<Workspace />} />
          <Route path="/endpoints/:endpointId" element={<Workspace />} />
          <Route path="/endpoints/:endpointId/requests/:requestId" element={<Workspace />} />
          <Route path="*" element={<Workspace />} />
        </Routes>
      </DemoLiveProvider>
    </div>
  )
}

/** Sends a synthetic webhook to the open endpoint (or the first one) so it arrives live. */
function SimulateButton({ backend }: { backend: DemoBackend }) {
  const endpoints = useEndpoints()
  const location = useLocation()
  const navigate = useNavigate()
  const openId = /^\/endpoints\/([^/]+)/.exec(location.pathname)?.[1]
  const targetId = endpoints.data?.some((e) => e.id === openId) ? openId : endpoints.data?.[0]?.id

  return (
    <button
      type="button"
      className="button button-primary"
      disabled={!targetId}
      onClick={() => {
        if (!targetId) return
        if (targetId !== openId) navigate(`/endpoints/${targetId}`)
        backend.simulateWebhook(targetId)
      }}
    >
      Simulate a webhook
    </button>
  )
}
