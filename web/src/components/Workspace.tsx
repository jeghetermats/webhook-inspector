import { useParams } from 'react-router'
import { useEndpoints } from '../api/hooks'
import { EndpointRail } from './EndpointRail'
import { RequestDetailPane } from './RequestDetail'
import { RequestsPane } from './RequestsPane'

/**
 * Three panes: endpoints, the selected endpoint's requests, the selected request.
 * On narrow screens only the deepest selected pane is shown (see index.css).
 */
export function Workspace() {
  const { endpointId, requestId } = useParams()
  const endpoints = useEndpoints()
  const endpoint = endpoints.data?.find((e) => e.id === endpointId)
  const depth = requestId ? 'request' : endpointId ? 'endpoint' : 'home'

  return (
    <main className="workspace" data-depth={depth}>
      <EndpointRail endpoints={endpoints} selectedId={endpointId} />

      {endpointId && endpoint ? (
        <RequestsPane endpoint={endpoint} selectedRequestId={requestId} />
      ) : endpointId && endpoints.isSuccess ? (
        <section className="pane pane-requests pane-empty">
          <p>This endpoint does not exist or was deleted.</p>
        </section>
      ) : (
        <section className="pane pane-requests pane-empty">
          {endpoints.data?.length === 0 ? (
            <p>Create an endpoint to get a URL you can send webhooks to.</p>
          ) : (
            <p>Choose an endpoint to see the requests it received.</p>
          )}
        </section>
      )}

      {endpoint && requestId ? (
        <RequestDetailPane endpoint={endpoint} requestId={requestId} />
      ) : (
        <section className="pane pane-detail pane-empty">
          {endpoint ? <p>Choose a request to read it.</p> : null}
        </section>
      )}
    </main>
  )
}
