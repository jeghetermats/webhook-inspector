import type { HubConnection } from '@microsoft/signalr'
import { createContext, useContext } from 'react'

export type LiveStatus = 'connecting' | 'live' | 'reconnecting' | 'offline'

/** The part of a SignalR connection the app uses, so the demo can provide its own. */
export type LiveConnection = Pick<HubConnection, 'on' | 'off' | 'invoke' | 'state'>

export interface LiveContextValue {
  connection: LiveConnection
  status: LiveStatus
  /** Increases every time the connection comes back after being down. */
  reconnects: number
}

export const LiveContext = createContext<LiveContextValue | null>(null)

export function useLive(): LiveContextValue {
  const value = useContext(LiveContext)
  if (!value) throw new Error('useLive must be used inside LiveProvider')
  return value
}
