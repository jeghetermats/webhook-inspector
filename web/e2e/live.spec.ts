import { expect, request as playwrightRequest, test } from '@playwright/test'
import { createEndpoint, signIn } from './helpers.ts'

test('a new request appears without refreshing', async ({ page }) => {
  await signIn(page, 'e2e-live')
  await page.goto('/')
  const captureUrl = await createEndpoint(page, 'Live')
  await expect(page.getByRole('status').filter({ hasText: 'Live' })).toBeVisible()

  await page.request.post(`${captureUrl}/arrived-live`, { data: '{"live":true}' })

  // No click on Refresh: the row must arrive through the hub.
  await expect(page.getByRole('link', { name: /POST\s*\/arrived-live/ })).toBeVisible()
  await expect(page.getByText('1 of 1000 requests')).toBeVisible() // usage follows too
})

test('after a disconnect the newest page is correct and older requests are reachable', async ({ page, baseURL }) => {
  test.setTimeout(120_000)

  // Control the live connection: while "cut", the open WebSocket is closed and every new
  // attempt (negotiate, WebSocket, or a fallback transport) fails. The rest of the app
  // keeps working, like a flaky connection that only drops the live channel.
  let cut = false
  const sockets: { close: () => Promise<void> }[] = []
  await page.routeWebSocket(/\/hubs\/requests/, (ws) => {
    if (cut) {
      void ws.close()
      return
    }
    ws.connectToServer()
    sockets.push(ws)
  })
  await page.route(/\/hubs\/requests/, (route) => (cut ? route.abort() : route.continue()))

  await signIn(page, 'e2e-reconnect')
  await page.goto('/')
  const captureUrl = await createEndpoint(page, 'Reconnect')
  const live = page.getByRole('status')
  await expect(live).toHaveText('Live')

  cut = true
  await Promise.all(sockets.map((ws) => ws.close()))
  await expect(live).not.toHaveText('Live', { timeout: 30_000 })

  const sender = await playwrightRequest.newContext({ baseURL })
  for (let i = 0; i < 120; i++) {
    const response = await sender.post(`${captureUrl}/r/${i}`, { data: String(i) })
    expect(response.status()).toBe(200)
  }
  await sender.dispose()

  // Nothing arrived while disconnected.
  await expect(page.locator('.request-row')).toHaveCount(0)

  cut = false
  await expect(live).toHaveText('Live', { timeout: 60_000 })

  // The first page is replaced with fresh data: the newest 50, newest first.
  const rows = page.locator('.request-row')
  await expect(rows).toHaveCount(50, { timeout: 15_000 })
  await expect(rows.first()).toContainText('/r/119')
  await expect(rows.nth(49)).toContainText('/r/70')

  // Everything else through pagination, without gaps or duplicates.
  await page.getByRole('button', { name: 'Load older requests' }).click()
  await expect(rows).toHaveCount(100)
  await page.getByRole('button', { name: 'Load older requests' }).click()
  await expect(rows).toHaveCount(120)
  await expect(rows.last()).toContainText('/r/0')
  await expect(page.getByRole('button', { name: 'Load older requests' })).toHaveCount(0)
  const paths = await page.locator('.request-path').allTextContents()
  expect(new Set(paths).size).toBe(120)
})
