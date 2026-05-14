import { expect, test } from '@playwright/test'
import { signIn } from './helpers.ts'

test('sign out shows the sign-in page and ends the session', async ({ page }) => {
  await signIn(page, 'e2e-sign-out')
  await page.goto('/')
  await expect(page.getByText('e2e-sign-out')).toBeVisible()

  await page.getByRole('button', { name: 'Sign out' }).click()

  await expect(page.getByRole('link', { name: 'Sign in with GitHub' })).toBeVisible()
  await expect(page.getByText('e2e-sign-out')).toHaveCount(0)
  const me = await page.request.get('/api/me')
  expect(me.status()).toBe(401)
})