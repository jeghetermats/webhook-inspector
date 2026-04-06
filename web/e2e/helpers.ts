import { type Page, expect } from '@playwright/test'

let counter = 0

/** Signs in through the test-only endpoint as a fresh user, so tests never share data. */
export async function signIn(page: Page, login: string): Promise<void> {
  const githubId = Date.now() * 1000 + (counter++ % 1000) + Math.floor(Math.random() * 100_000)
  const response = await page.request.post(`/test/sign-in?githubId=${githubId}&login=${encodeURIComponent(login)}`)
  expect(response.ok(), 'test sign-in must be enabled on the server').toBeTruthy()
}

/** Creates an endpoint through the UI and returns its capture URL. */
export async function createEndpoint(page: Page, name: string): Promise<string> {
  await page.getByLabel('New endpoint').fill(name)
  await page.getByRole('button', { name: 'Create' }).click()
  await expect(page.getByRole('heading', { name })).toBeVisible()
  const url = await page.locator('.capture-url-text').textContent()
  expect(url).toMatch(/\/h\/[A-Za-z0-9_-]{22}$/)
  return url!
}
