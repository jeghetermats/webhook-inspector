import { expect, test } from '@playwright/test'

test('the demo works without signing in and never calls the server API', async ({ page }) => {
  const serverCalls: string[] = []
  page.on('request', (request) => {
    const path = new URL(request.url()).pathname
    if (/^\/(api|auth|hubs|h|test)(\/|$)/.test(path)) serverCalls.push(`${request.method()} ${path}`)
  })

  await page.goto('/demo')
  await expect(page.getByText('Demo with made-up data')).toBeVisible()

  // Open an endpoint and a request: synthetic data only.
  await page.getByRole('link', { name: /GitHub push/ }).click()
  await expect(page.getByRole('status')).toHaveText('Live')
  await page.locator('.request-row').first().click()
  const wire = page.getByRole('region', { name: 'Raw request' })
  await expect(wire).toContainText('X-GitHub-Event')
  await expect(wire).toContainText('example-org/example-app')
  await expect(page).toHaveURL(/\/demo\/endpoints\/.+\/requests\//)

  // A simulated webhook arrives through the real live-list code.
  const before = await page.locator('.request-row').count()
  await page.getByRole('button', { name: 'Simulate a webhook' }).click()
  await expect(page.locator('.request-row')).toHaveCount(before + 1)

  // Filters, reload and actions work in memory.
  await page.getByLabel('Method').selectOption('GET')
  await expect(page.locator('.request-row')).toHaveCount(0)
  await page.getByRole('button', { name: 'Clear filters' }).click()
  await page.reload()
  await expect(page.getByRole('region', { name: 'Raw request' })).toBeVisible()

  expect(serverCalls).toEqual([])
})

test('the sign-in page links to the demo', async ({ page }) => {
  await page.goto('/')
  await page.getByRole('link', { name: 'Try the demo' }).click()
  await expect(page).toHaveURL(/\/demo$/)
  await expect(page.getByText('Demo with made-up data')).toBeVisible()
})
