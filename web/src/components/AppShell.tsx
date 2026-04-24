import type { ReactNode } from 'react'
import { Link } from 'react-router'
import { useSignOut } from '../api/hooks'
import { LiveProvider } from '../live/LiveProvider'

export function AppShell({ login, children }: { login: string; children: ReactNode }) {
  const signOut = useSignOut()

  return (
    <div className="shell">
      <header className="topbar">
        <Link to="/" className="brand">
          Webhook Inspector
        </Link>
        <p className="topbar-note">
          Captured requests may contain secrets. They are deleted 7 days after they arrive.
        </p>
        <div className="topbar-user">
          <span className="topbar-login">{login}</span>
          <button
            type="button"
            className="button button-quiet"
            onClick={() => signOut.mutate()}
            disabled={signOut.isPending}
          >
            Sign out
          </button>
        </div>
      </header>
      <LiveProvider>{children}</LiveProvider>
    </div>
  )
}
