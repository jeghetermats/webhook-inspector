import { HubConnectionState } from '@microsoft/signalr'
import { type ReactNode, useState } from 'react'
import type { RequestReceivedEvent } from '../api/types'
import { LiveContext, type LiveConnection } from '../live/context'
import type { DemoBackend } from './demoApi'

/**
 * Stands in for the SignalR connection in the demo: simulated webhooks arrive as the same
 * RequestReceived events, so the real live-list code handles them.
 */
export function DemoLiveProvider({ backend, children }: { backend: DemoBackend; children: ReactNode }) {
  const [connection] = useState<LiveConnection>(() => {
    const removers = new Map<(event: RequestReceivedEvent) => void, () => void>()
    return {
      state: HubConnectionState.Connected,
      on: (method: string, handler: (event: RequestReceivedEvent) => void) => {
        if (method === 'RequestReceived' && !removers.has(handler)) {
          removers.set(handler, backend.subscribe(handler) as () => void)
        }
      },
      off: (method: string, handler?: (event: RequestReceivedEvent) => void) => {
        if (method === 'RequestReceived' && handler) {
          removers.get(handler)?.()
          removers.delete(handler)
        }
      },
      invoke: async () => undefined,
    } as unknown as LiveConnection
  })

  return <LiveContext.Provider value={{ connection, status: 'live', reconnects: 0 }}>{children}</LiveContext.Provider>
}
