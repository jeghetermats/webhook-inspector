import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { type ReactNode, useEffect, useState } from 'react'
import { LiveContext, type LiveStatus } from './context'

const RETRY_AFTER_CLOSE_MS = 15_000

/**
 * One SignalR connection for the signed-in session. SignalR's automatic reconnect covers
 * short drops; if it gives up, we keep retrying every 15 seconds. Data never depends on
 * this connection: it only makes new requests appear sooner (ADR 0003).
 */
export function LiveProvider({ children }: { children: ReactNode }) {
  const [connection] = useState(() =>
    new HubConnectionBuilder()
      .withUrl('/hubs/requests')
      .withAutomaticReconnect([0, 2_000, 5_000, 10_000, 30_000])
      .configureLogging(LogLevel.Warning)
      .build(),
  )
  const [status, setStatus] = useState<LiveStatus>('connecting')
  const [reconnects, setReconnects] = useState(0)

  useEffect(() => {
    let stopped = false
    let retry: ReturnType<typeof setTimeout> | undefined

    const start = async () => {
      if (stopped || connection.state !== HubConnectionState.Disconnected) return
      setStatus('connecting')
      try {
        await connection.start()
        if (!stopped) setStatus('live')
      } catch {
        if (!stopped) {
          setStatus('offline')
          retry = setTimeout(start, RETRY_AFTER_CLOSE_MS)
        }
      }
    }

    connection.onreconnecting(() => setStatus('reconnecting'))
    connection.onreconnected(() => {
      setStatus('live')
      setReconnects((n) => n + 1)
    })
    connection.onclose(() => {
      if (stopped) return
      setStatus('offline')
      retry = setTimeout(async () => {
        await start()
        if (connection.state === HubConnectionState.Connected) setReconnects((n) => n + 1)
      }, RETRY_AFTER_CLOSE_MS)
    })

    void start()

    return () => {
      stopped = true
      clearTimeout(retry)
      void connection.stop()
    }
  }, [connection])

  return <LiveContext.Provider value={{ connection, status, reconnects }}>{children}</LiveContext.Provider>
}

