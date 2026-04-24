import { HubConnectionState } from '@microsoft/signalr'
import { useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef } from 'react'
import { keys } from '../api/hooks'
import type { Endpoint, RequestFilters, RequestReceivedEvent } from '../api/types'
import { type RequestListData, applyLiveEvent, applyUsage } from '../lib/live'
import { useLive } from './context'

/**
 * Subscribes to one endpoint's live events and applies them to the cached list (PLAN.md
 * section 8). After a reconnect the list is refetched, which replaces the first page with
 * fresh data, because events sent while disconnected are lost.
 */
export function useLiveRequests(endpointId: string, filters: RequestFilters) {
  const { connection, status, reconnects } = useLive()
  const queryClient = useQueryClient()
  const filtersKey = JSON.stringify(filters)
  const seenReconnects = useRef(reconnects)

  useEffect(() => {
    if (reconnects !== seenReconnects.current) {
      seenReconnects.current = reconnects
      void queryClient.invalidateQueries({ queryKey: keys.requests(endpointId) })
      void queryClient.invalidateQueries({ queryKey: keys.endpoints })
    }
  }, [reconnects, endpointId, queryClient])

  useEffect(() => {
    if (status !== 'live') return
    const activeFilters = JSON.parse(filtersKey) as RequestFilters

    const onEvent = (event: RequestReceivedEvent) => {
      if (event.endpointId !== endpointId) return
      queryClient.setQueryData<RequestListData>(keys.requestList(endpointId, activeFilters), (data) =>
        applyLiveEvent(data, event, activeFilters),
      )
      queryClient.setQueryData<Endpoint[]>(keys.endpoints, (endpoints) => applyUsage(endpoints, event))
    }

    connection.on('RequestReceived', onEvent)
    connection.invoke('Subscribe', endpointId).catch(() => {
      // Deleted or not ours: the list query shows that state already.
    })

    return () => {
      connection.off('RequestReceived', onEvent)
      if (connection.state === HubConnectionState.Connected) {
        connection.invoke('Unsubscribe', endpointId).catch(() => {})
      }
    }
  }, [connection, status, endpointId, filtersKey, queryClient])

  return status
}
