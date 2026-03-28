// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError, buildFilterQuery, readXsrfToken, request } from './client'

function clearCookies() {
  for (const cookie of document.cookie.split(';')) {
    const name = cookie.split('=')[0].trim()
    if (name) document.cookie = `${name}=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/`
  }
}

function json(status: number, body: unknown) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
}

describe('readXsrfToken', () => {
  it('finds and decodes the token among other cookies', () => {
    expect(readXsrfToken('a=1; XSRF-TOKEN=CfDJ8%2Babc%3D%3D; b=2')).toBe('CfDJ8+abc==')
  })

  it('returns null when the cookie is missing', () => {
    expect(readXsrfToken('a=1')).toBeNull()
  })
})

describe('request', () => {
  const fetchMock = vi.fn<typeof fetch>()

  beforeEach(() => {
    clearCookies()
    fetchMock.mockReset()
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('sends GET requests without an antiforgery header', async () => {
    fetchMock.mockResolvedValueOnce(json(200, { id: '1', login: 'octocat' }))

    await expect(request('GET', '/api/me')).resolves.toEqual({ id: '1', login: 'octocat' })

    const init = fetchMock.mock.calls[0][1]!
    expect(init.credentials).toBe('same-origin')
    expect(init.headers).not.toHaveProperty('X-XSRF-TOKEN')
  })

  it('fetches a token first when none exists, then sends it on the state-changing request', async () => {
    fetchMock.mockImplementationOnce(async () => {
      document.cookie = 'XSRF-TOKEN=token-1; path=/'
      return new Response(null, { status: 204 })
    })
    fetchMock.mockResolvedValueOnce(json(201, { id: 'e1' }))

    await request('POST', '/api/endpoints', { name: 'x' })

    expect(fetchMock.mock.calls[0][0]).toBe('/api/antiforgery')
    const init = fetchMock.mock.calls[1][1]!
    expect(init.headers).toMatchObject({ 'X-XSRF-TOKEN': 'token-1', 'Content-Type': 'application/json' })
    expect(init.body).toBe('{"name":"x"}')
  })

  it('refreshes the token and retries once when the server rejects it', async () => {
    document.cookie = 'XSRF-TOKEN=stale; path=/'
    fetchMock.mockResolvedValueOnce(json(400, { detail: 'Missing or invalid antiforgery token.' }))
    fetchMock.mockImplementationOnce(async () => {
      document.cookie = 'XSRF-TOKEN=fresh; path=/'
      return new Response(null, { status: 204 })
    })
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }))

    await request('DELETE', '/api/requests/1')

    expect(fetchMock).toHaveBeenCalledTimes(3)
    expect(fetchMock.mock.calls[2][1]!.headers).toMatchObject({ 'X-XSRF-TOKEN': 'fresh' })
  })

  it('surfaces the first validation error from a problem response', async () => {
    document.cookie = 'XSRF-TOKEN=t; path=/'
    fetchMock.mockResolvedValueOnce(json(400, { title: 'One or more validation errors occurred.', errors: { name: ['Name is required.'] } }))

    const error = await request('POST', '/api/endpoints', { name: '' }).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect(error).toMatchObject({ status: 400, message: 'Name is required.' })
    expect(fetchMock).toHaveBeenCalledTimes(1) // not an antiforgery problem, so no retry
  })
})

describe('buildFilterQuery', () => {
  it('includes only the filters that are set', () => {
    expect(buildFilterQuery({ method: 'POST', pathPrefix: '/github' }, 'c1')).toBe('?cursor=c1&method=POST&pathPrefix=%2Fgithub')
    expect(buildFilterQuery({})).toBe('')
  })
})
