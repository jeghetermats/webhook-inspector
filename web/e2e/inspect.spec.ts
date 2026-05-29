import { expect, test } from '@playwright/test'
import { createEndpoint, signIn } from './helpers.ts'

test('sign in, create an endpoint, send a request and inspect it', async ({ page }) => {
  await signIn(page, 'e2e-inspector')
  await page.goto('/')
  await expect(page.getByText('e2e-inspector')).toBeVisible()

  const captureUrl = await createEndpoint(page, 'GitHub push')

  // Send a webhook to the capture URL, like an external sender would.
  const capture = await page.request.post(`${captureUrl}/push?ref=main&ref=dev`, {
    headers: { 'Content-Type': 'application/json', 'X-GitHub-Event': 'push' },
    data: '{"ref":"refs/heads/main","commits":3}',
  })
  expect(capture.status()).toBe(200)

  // Phase 4: the request shows up after a refresh (live updates come in phase 5).
  await page.getByRole('button', { name: 'Refresh' }).click()
  const row = page.getByRole('link', { name: /POST\s*\/push/ })
  await expect(row).toBeVisible()
  await expect(row).toContainText('"commits":3') // body preview

  await row.click()
  const wire = page.getByRole('region', { name: 'Raw request' })
  await expect(wire).toContainText('POST /push?ref=main&ref=dev')
  await expect(wire).toContainText('X-GitHub-Event: push')
  await expect(wire).toContainText('"commits": 3') // formatted JSON
  // The capture was sent from the signed-in browser context: its session cookie must not be stored.
  await expect(wire).not.toContainText('wi-auth')
  await expect(wire).not.toContainText('XSRF-TOKEN')

  await page.getByRole('button', { name: 'Hex', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Hex', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await expect(wire).toContainText('7b 22 72 65 66 22') // {"ref"

  const query = page.locator('.pairs')
  await expect(query.getByRole('row')).toHaveCount(2) // repeated key kept

  const downloadPromise = page.waitForEvent('download')
  await page.getByRole('link', { name: 'Download body' }).click()
  const download = await downloadPromise
  expect(download.suggestedFilename()).toMatch(/^request-.+\.bin$/)

  // A reload keeps the selected endpoint and request: state lives in the URL.
  await page.reload()
  await expect(page.getByRole('region', { name: 'Raw request' })).toContainText('X-GitHub-Event: push')

  // Deleting asks first, in a dialog that names the request; Cancel keeps it.
  await page.getByRole('button', { name: 'Delete', exact: true }).click()
  const confirm = page.getByRole('dialog', { name: 'Delete request “POST /push”?' })
  await expect(confirm).toBeVisible()
  await expect(confirm.getByRole('button', { name: 'Cancel' })).toBeFocused()
  await page.keyboard.press('Escape')
  await expect(confirm).toBeHidden()
  await expect(page.getByRole('button', { name: 'Delete', exact: true })).toBeFocused() // focus returns to the trigger
  await page.getByRole('button', { name: 'Delete', exact: true }).click()
  await confirm.getByRole('button', { name: 'Delete request' }).click()
  await expect(page.getByRole('link', { name: /POST\s*\/push/ })).toHaveCount(0)
  await expect(page.getByText('No requests yet')).toBeVisible()
})

test('captured HTML and scripts are shown as text, never rendered', async ({ page }) => {
  await signIn(page, 'e2e-xss')
  await page.goto('/')
  const captureUrl = await createEndpoint(page, 'Untrusted input')

  let dialogs = 0
  page.on('dialog', (dialog) => {
    dialogs++
    void dialog.dismiss()
  })

  const payload = '<img src=x onerror="alert(1)"><script>alert(2)</script>'
  await page.request.post(`${captureUrl}/x?q=${encodeURIComponent('<b>bold</b>')}`, {
    headers: { 'Content-Type': 'text/html', 'X-Evil': '<svg onload=alert(3)>' },
    data: payload,
  })

  await page.getByRole('button', { name: 'Refresh' }).click()
  await page.getByRole('link', { name: /POST\s*\/x/ }).click()

  const wire = page.getByRole('region', { name: 'Raw request' })
  await expect(wire).toContainText(payload)
  await expect(wire).toContainText('X-Evil: <svg onload=alert(3)>')
  await expect(page.locator('.request img, .request script, .request svg, .request b')).toHaveCount(0)
  expect(dialogs).toBe(0)
})

test('refresh updates usage, and filters narrow the list', async ({ page }) => {
  await signIn(page, 'e2e-filters')
  await page.goto('/')
  const captureUrl = await createEndpoint(page, 'Filters')

  for (const [method, path] of [
    ['POST', '/github/push'],
    ['GET', '/github/ping'],
    ['PUT', '/stripe/charge'],
  ]) {
    await page.request.fetch(`${captureUrl}${path}`, { method, data: method === 'GET' ? undefined : '{}' })
  }

  await page.getByRole('button', { name: 'Refresh' }).click()
  await expect(page.locator('.request-row')).toHaveCount(3)
  await expect(page.getByText('3 of 1000 requests')).toBeVisible()

  await page.getByLabel('Method').selectOption('POST')
  await expect(page.locator('.request-row')).toHaveCount(1)
  await expect(page).toHaveURL(/method=POST/)

  await page.getByRole('button', { name: 'Clear filters' }).click()
  await page.getByLabel('Path starts with').fill('/github')
  await page.getByLabel('Path starts with').press('Enter')
  await expect(page.locator('.request-row')).toHaveCount(2)
})
