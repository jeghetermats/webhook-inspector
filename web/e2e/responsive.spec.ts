/// <reference lib="dom" />
import { type Page, expect, test } from '@playwright/test'
import { createEndpoint, signIn } from './helpers.ts'

// From a small phone to a large monitor. The layout changes at 700 px and 1100 px.
const widths = [320, 375, 600, 768, 1024, 1280, 1920, 2560]

/** The page must fit its viewport: nothing may force a horizontal scrollbar. */
async function expectNoHorizontalScroll(page: Page, where: string): Promise<void> {
  const size = await page.evaluate(() => ({
    viewport: document.documentElement.clientWidth,
    content: document.documentElement.scrollWidth,
  }))
  expect(size.content, `${where}: content is wider than the viewport`).toBeLessThanOrEqual(size.viewport)
}

test('the sign-in page and the demo fit every screen width', async ({ page }) => {
  for (const width of widths) {
    await page.setViewportSize({ width, height: 800 })

    await page.goto('/')
    await expect(page.getByRole('link', { name: 'Sign in with GitHub' })).toBeVisible()
    await expectNoHorizontalScroll(page, `sign-in at ${width}px`)

    await page.goto('/demo')
    await expect(page.getByRole('link', { name: /GitHub push/ })).toBeVisible()
    await expectNoHorizontalScroll(page, `demo home at ${width}px`)

    await page.getByRole('link', { name: /GitHub push/ }).click()
    await expectNoHorizontalScroll(page, `demo endpoint at ${width}px`)

    await page.locator('.request-row').first().click()
    await expect(page.getByRole('button', { name: 'Copy as curl' })).toBeVisible()
    await expectNoHorizontalScroll(page, `demo request at ${width}px`)

    // Every action in the top bar stays on screen.
    for (const name of ['Simulate a webhook', 'Sign in with GitHub']) {
      const box = await page.getByRole('banner').getByRole(name === 'Sign in with GitHub' ? 'link' : 'button', { name }).boundingBox()
      expect(box, name).not.toBeNull()
      expect(box!.x + box!.width, `${name} at ${width}px`).toBeLessThanOrEqual(width)
    }
  }
})

test('the signed-in app fits a narrow phone, including a long request', async ({ page }) => {
  await signIn(page, 'e2e-responsive-with-a-long-login-name')
  await page.setViewportSize({ width: 320, height: 700 })
  await page.goto('/')
  const captureUrl = await createEndpoint(page, 'An endpoint with a rather long name for a phone')
  await expectNoHorizontalScroll(page, 'endpoint')
  // Fields under 16px make iOS Safari zoom the whole page on focus.
  const fieldSizes = await page.evaluate(() => [...document.querySelectorAll('.input')].map((e) => getComputedStyle(e).fontSize))
  expect(new Set(fieldSizes)).toEqual(new Set(['16px']))

  const longValue = 'x'.repeat(400)
  await page.request.post(`${captureUrl}/a/${'segment/'.repeat(20)}?q=${longValue}`, {
    headers: { 'Content-Type': 'application/json', 'X-Long-Header': longValue },
    data: JSON.stringify({ long: longValue, nested: { list: [longValue, longValue] } }),
  })
  await page.getByRole('button', { name: 'Refresh' }).click()
  await page.locator('.request-row').first().click()
  await expect(page.getByRole('button', { name: 'Copy as curl' })).toBeVisible()
  await expectNoHorizontalScroll(page, 'request with long values')

  for (const view of ['Text', 'Hex']) {
    await page.getByRole('button', { name: view, exact: true }).click()
    await expectNoHorizontalScroll(page, `${view} body view`)
  }
})