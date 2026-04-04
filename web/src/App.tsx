import { Route, Routes } from 'react-router'
import { useMe } from './api/hooks'
import { AppShell } from './components/AppShell'
import { SignIn } from './components/SignIn'
import { Workspace } from './components/Workspace'

export default function App() {
  const me = useMe()

  if (me.isPending) {
    return <p className="boot">Loading…</p>
  }

  if (me.isError) {
    return (
      <p className="boot" role="alert">
        The server did not answer. Check your connection and reload the page.
      </p>
    )
  }

  if (me.data === null) {
    return <SignIn />
  }

  return (
    <AppShell login={me.data.login}>
      <Routes>
        <Route path="/" element={<Workspace />} />
        <Route path="/endpoints/:endpointId" element={<Workspace />} />
        <Route path="/endpoints/:endpointId/requests/:requestId" element={<Workspace />} />
        <Route path="*" element={<Workspace />} />
      </Routes>
    </AppShell>
  )
}
