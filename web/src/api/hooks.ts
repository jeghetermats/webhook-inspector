import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from './client'
import type { RequestFilters } from './types'

export const keys = {
  me: ['me'] as const,
  endpoints: ['endpoints'] as const,
  requests: (endpointId: string) => ['requests', endpointId] as const,
  requestList: (endpointId: string, filters: RequestFilters) => ['requests', endpointId, 'list', filters] as const,
  request: (id: string) => ['request', id] as const,
}

export function useMe() {
  return useQuery({
    queryKey: keys.me,
    queryFn: async () => {
      try {
        return await api.me()
      } catch (error) {
        if (error instanceof ApiError && error.status === 401) return null // signed out
        throw error
      }
    },
    staleTime: Infinity,
  })
}

export function useEndpoints() {
  return useQuery({ queryKey: keys.endpoints, queryFn: api.endpoints })
}

export function useRequestList(endpointId: string, filters: RequestFilters, live = false) {
  return useInfiniteQuery({
    queryKey: keys.requestList(endpointId, filters),
    queryFn: ({ pageParam }) => api.requests(endpointId, filters, pageParam),
    initialPageParam: null as string | null,
    getNextPageParam: (page) => page.nextCursor,
    // A notification can be lost even while connected (PLAN.md section 8), so refetch
    // regularly while live. Without a connection the user refreshes by hand.
    refetchInterval: live ? 30_000 : false,
  })
}

export function useRequestDetail(id: string | undefined) {
  return useQuery({
    queryKey: keys.request(id ?? ''),
    queryFn: () => api.requestDetail(id!),
    enabled: id !== undefined,
    staleTime: Infinity, // captured requests never change
  })
}

export function useEndpointMutations() {
  const queryClient = useQueryClient()
  const refreshEndpoints = () => queryClient.invalidateQueries({ queryKey: keys.endpoints })

  return {
    create: useMutation({ mutationFn: api.createEndpoint, onSuccess: refreshEndpoints }),
    rename: useMutation({
      mutationFn: ({ id, name }: { id: string; name: string }) => api.renameEndpoint(id, name),
      onSuccess: refreshEndpoints,
    }),
    rotate: useMutation({ mutationFn: api.rotateToken, onSuccess: refreshEndpoints }),
    remove: useMutation({
      mutationFn: api.deleteEndpoint,
      onSuccess: (_, id) => {
        queryClient.removeQueries({ queryKey: keys.requests(id) })
        return refreshEndpoints()
      },
    }),
    clear: useMutation({
      mutationFn: api.clearRequests,
      onSuccess: (_, id) =>
        Promise.all([refreshEndpoints(), queryClient.invalidateQueries({ queryKey: keys.requests(id) })]),
    }),
  }
}

export function useDeleteRequest(endpointId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: api.deleteRequest,
    onSuccess: (_, id) => {
      queryClient.removeQueries({ queryKey: keys.request(id) })
      return Promise.all([
        queryClient.invalidateQueries({ queryKey: keys.requests(endpointId) }),
        queryClient.invalidateQueries({ queryKey: keys.endpoints }),
      ])
    },
  })
}

export function useSignOut() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: api.logout,
    onSuccess: () => {
      queryClient.clear()
      queryClient.setQueryData(keys.me, null)
    },
  })
}
