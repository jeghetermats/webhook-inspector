import { expect, test } from '@playwright/test'
import { createEndpoint, signIn } from './helpers.ts'

test('replaying to a private address is blocked and shown as a failed attempt', async ({ page }) => {
  await signIn(page, 'e2e-replay')
  await page.goto('/')
  const captureUrl = await createEndpoint(page, 'Replay source')
  const capture = await page.request.post(`${captureUrl}/hook`, {
    headers: { 'Content-Type': 'application/json' },
    data: '{"event":"ping"}',
  })
  expect(capture.status()).toBe(200)

  await page.getByRole('link', { name: /POST\s*\/hook/ }).click()
  await page.getByLabel('Target URL').fill('http://127.0.0.1/admin')
  await page.getByRole('button', { name: 'Send' }).click()

  const attempts = page.getByRole('list', { name: /Replay attempts/ })
  await expect(attempts.getByText('Failed')).toBeVisible()
  await expect(attempts.getByText('Blocked: The target is a private or reserved address.')).toBeVisible()

  // The attempt is stored: it is still there after a reload.
  await page.reload()
  await expect(page.getByRole('list', { name: /Replay attempts/ }).getByText('http://127.0.0.1/admin')).toBeVisible()
})

test('a malformed target is rejected before anything is sent', async ({ page }) => {
  await signIn(page, 'e2e-replay-invalid')
  await page.goto('/')
  const captureUrl = await createEndpoint(page, 'Replay validation')
  await page.request.post(`${captureUrl}/hook`, { data: 'x' })

  await page.getByRole('link', { name: /POST\s*\/hook/ }).click()
  // Valid for the browser's URL field, refused by the server.
  await page.getByLabel('Target URL').fill('https://user:secret@example.com/')
  await page.getByRole('button', { name: 'Send' }).click()

  await expect(page.getByRole('alert')).toHaveText('Credentials in the URL are not supported.')
  await expect(page.getByRole('list', { name: /Replay attempts/ })).toHaveCount(0)
})

test('the demo simulates a replay response without leaving the browser', async ({ page }) => {
  const serverCalls: string[] = []
  page.on('request', (request) => {
    if (new URL(request.url()).pathname.startsWith('/api/')) serverCalls.push(request.url())
  })

  await page.goto('/demo')
  await page.getByRole('link', { name: /GitHub push/ }).click()
  await page.locator('.request-row').first().click()
  await page.getByLabel('Target URL').fill('https://example.com/webhooks')
  await page.getByRole('button', { name: 'Send' }).click()

  const response = page.getByRole('region', { name: 'Response' })
  await expect(response.getByText('HTTP 200')).toBeVisible()
  await expect(response.getByText(/nothing leaves your browser/)).toBeVisible()
  expect(serverCalls).toEqual([])
})
