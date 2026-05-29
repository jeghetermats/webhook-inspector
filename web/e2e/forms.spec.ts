import { expect, test } from '@playwright/test'
import { signIn } from './helpers.ts'

test('an empty endpoint name is explained on submit instead of a disabled button', async ({ page }) => {
  await signIn(page, 'e2e-forms')
  await page.goto('/')

  const create = page.getByRole('button', { name: 'Create' })
  await expect(create).toBeEnabled()
  await create.click()

  const field = page.getByLabel('New endpoint')
  await expect(field).toBeFocused()
  await expect(field).toHaveAttribute('aria-invalid', 'true')
  await expect(field).toHaveAccessibleDescription('Enter a name for the endpoint, for example GitHub push.')

  await field.fill('Fixed')
  await expect(field).not.toHaveAttribute('aria-invalid', 'true') // the error clears as you type
  await create.click()
  await expect(page.getByRole('heading', { name: 'Fixed' })).toBeVisible()
})
