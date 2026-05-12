// @vitest-environment jsdom
import { describe, expect, it } from 'vitest'
import { ApiError } from '../api/client'
import { createDemoApi } from './demoApi'

const now = new Date('2026-05-12T12:00:00Z')

describe('demo API', () => {
  it('starts with three endpoints of synthetic traffic', async () => {
    const { api } = createDemoApi(now, 'demo.example')
    const endpoints = await api.endpoints()
    expect(endpoints.map((e) => e.name)).toEqual(['GitHub push', 'Stripe payments', 'Slack commands'])
    expect(endpoints.every((e) => e.requestCount > 0)).toBe(true)
  })

  it('pages newest first without gaps or duplicates', async () => {
    const backend = createDemoApi(now, 'demo.example')
    const [github] = await backend.api.endpoints()
    for (let i = 0; i < 60; i++) backend.simulateWebhook(github.id)

    const first = await backend.api.requests(github.id, {})
    const second = await backend.api.requests(github.id, {}, first.nextCursor)

    const all = [...first.items, ...second.items]
    expect(first.items).toHaveLength(50)
    expect(second.nextCursor).toBeNull()
    expect(new Set(all.map((r) => r.id)).size).toBe(all.length)
    expect(all.length).toBe((await backend.api.endpoints())[0].requestCount)
    const times = all.map((r) => Date.parse(r.receivedAt))
    expect(times).toEqual([...times].sort((a, b) => b - a))
  })

  it('applies the same filters as the server', async () => {
    const { api } = createDemoApi(now, 'demo.example')
    const slack = (await api.endpoints())[2]
    const gets = await api.requests(slack.id, { method: 'GET' })
    const health = await api.requests(slack.id, { pathPrefix: '/health' })
    expect(gets.items.length).toBeGreaterThan(0)
    expect(gets.items.every((r) => r.method === 'GET')).toBe(true)
    expect(health.items.every((r) => r.path.startsWith('/health'))).toBe(true)
  })

  it('previews text bodies but not binary ones', async () => {
    const { api } = createDemoApi(now, 'demo.example')
    const slack = (await api.endpoints())[2]
    const items = (await api.requests(slack.id, {})).items
    expect(items.find((r) => r.method === 'PUT')?.bodyPreview).toBeNull()
    expect(items.find((r) => r.method === 'POST')?.bodyPreview).toContain('command=%2Fdeploy')
  })

  it('notifies listeners after storing a simulated webhook', async () => {
    const backend = createDemoApi(now, 'demo.example')
    const [github] = await backend.api.endpoints()
    const events: string[] = []
    backend.subscribe((e) => events.push(e.requestId))

    const stored = backend.simulateWebhook(github.id)!
    await new Promise((r) => setTimeout(r, 80))

    expect(events).toEqual([stored.id])
    await expect(backend.api.requestDetail(stored.id)).resolves.toMatchObject({ endpointId: github.id })
  })

  it('enforces the endpoint limit and answers 404 for unknown ids', async () => {
    const { api } = createDemoApi(now, 'demo.example')
    for (let i = 0; i < 7; i++) await api.createEndpoint(`extra ${i}`)
    await expect(api.createEndpoint('one too many')).rejects.toMatchObject({ status: 409 })
    await expect(api.requestDetail('missing')).rejects.toBeInstanceOf(ApiError)
  })

  it('uses only documentation IP addresses and no real-looking secrets', async () => {
    const { api } = createDemoApi(now, 'demo.example')
    const [github] = await api.endpoints()
    const detail = await api.requestDetail((await api.requests(github.id, {})).items[0].id)
    expect(detail.sourceIp).toMatch(/^203\.0\.113\./)
    expect(atob(detail.bodyBase64)).toContain('example-org/example-app')
  })
})
