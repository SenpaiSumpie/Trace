import { expect, test } from '@playwright/test'

test('ask the fixture repository a question and inspect the evidence', async ({ page }) => {
  await page.goto('/')

  // The demo repository is selected, pinned to its indexed revision.
  await expect(page.getByLabel('Repository')).toContainText('trace-demo/sample-repo at 4a07032f4525')
  await expect(page.getByRole('button', { name: 'Ask' })).toBeDisabled()

  await page.getByLabel('Question').fill('Why does the first page of the task list skip the first results?')
  await page.getByRole('button', { name: 'Ask' }).click()

  await expect(page.getByRole('heading', { name: 'Answer' })).toBeVisible()
  await expect(page.getByText('no language model interpreted this question')).toBeVisible()

  // The evidence is the real source at its real line numbers.
  const passage = page.getByRole('figure').filter({ hasText: 'src/TaskBoard.Api/Tasks/TaskRepository.cs' })
  await expect(passage).toBeVisible()
  const offByOne = passage.locator('.line', { hasText: 'var skip = request.Page * request.PageSize;' })
  await expect(offByOne.locator('.line-number')).toHaveText('25')

  // Long source lines scroll inside the passage; they never widen the page.
  const passageBox = (await passage.boundingBox())!
  const columnBox = (await page.getByRole('main').boundingBox())!
  expect(passageBox.x + passageBox.width).toBeLessThanOrEqual(columnBox.x + columnBox.width)

  // Every step the investigation took is listed.
  const steps = page.getByRole('list').filter({ hasText: 'Started investigating at commit 4a07032f4525.' })
  await expect(steps.getByRole('listitem')).toHaveCount(3)
  await expect(steps).toContainText('Searched the code for')
  await expect(steps).toContainText('Answered with')

  await expect(page.getByText('fake-extractive-v1')).toBeVisible()
  await expect(page.getByText('API healthy')).toBeVisible()
})

test('a question the repository cannot answer reports insufficient evidence', async ({ page }) => {
  await page.goto('/')

  await page.getByLabel('Question').fill('xyzzy qwfpgj zxcvbnm')
  await page.getByRole('button', { name: 'Ask' }).click()

  await expect(page.getByRole('heading', { name: 'Not enough evidence' })).toBeVisible()
  await expect(page.getByText('TRACE could not find enough evidence')).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Evidence', exact: true })).toHaveCount(0)
  await expect(page.getByRole('figure')).toHaveCount(0)
})

test('index the demo repository again from the page', async ({ page }) => {
  await page.goto('/')

  const row = page.getByRole('listitem').filter({ hasText: 'trace-demo/sample-repo' })
  await expect(row).toContainText('Indexed at commit 4a07032f4525')

  await row.getByRole('button', { name: 'Index again' }).click()

  await expect(row.getByText('Indexing finished.')).toBeVisible()
  await expect(row).toContainText('Indexed at commit 4a07032f4525')
  await expect(row.getByRole('button', { name: 'Index again' })).toBeEnabled()
})

test('the form works from the keyboard alone', async ({ page }) => {
  await page.goto('/')

  await page.getByLabel('Repository').focus()
  await page.keyboard.press('Tab')
  await expect(page.getByLabel('Question')).toBeFocused()
  await page.keyboard.type('Which requests are let through without an API key?')
  await page.keyboard.press('Tab')
  await expect(page.getByRole('button', { name: 'Ask' })).toBeFocused()
  await page.keyboard.press('Enter')

  await expect(page.getByRole('heading', { name: 'Answer' })).toBeVisible()
  await expect(page.getByRole('figure').first()).toContainText('src/TaskBoard.Api/Auth/ApiKeyMiddleware.cs')
})
