import { expect, type Locator, type Page, test } from '@playwright/test'

// History and addresses, against the real API with the fixture repository and the fake model.
// Nobody is signed in here: the API runs as in local development, where a browser may ask the
// demo repository and is listed what it asked itself. Each test is a new browser profile, so
// its history starts empty whatever earlier tests and earlier runs left in the database.

const QUESTION = 'Why does the first page of the task list skip the first results?'
const ISSUE_5 = '#5 Requests to paths that merely contain /health skip the API key check'
const ITEM = /\/(investigations|plans)\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/
const UNKNOWN_ID = '00000000-0000-7000-8000-00000000dead'

async function ask(page: Page, question: string): Promise<string> {
  await page.goto('/')
  await page.getByLabel('Question').fill(question)
  await page.getByRole('button', { name: 'Ask' }).click()
  await expect(page.getByRole('heading', { name: /^(Answer|Not enough evidence)$/ })).toBeVisible()
  await expect(page).toHaveURL(/\/investigations\/[0-9a-f-]{36}$/)
  return new URL(page.url()).pathname
}

async function startPlan(page: Page): Promise<string> {
  await page.goto('/plan')
  await page.getByRole('button', { name: ISSUE_5 }).click()
  await page.getByRole('button', { name: 'Start a plan' }).click()
  await expect(page.getByText('The plan is ready.')).toBeVisible()
  await expect(page).toHaveURL(/\/plans\/[0-9a-f-]{36}$/)
  return new URL(page.url()).pathname
}

async function expectAnswer(page: Page) {
  await expect(page.getByRole('heading', { name: 'Answer' })).toBeVisible()
  await expect(page.locator('.investigation .asked')).toHaveText(QUESTION)
  const passage = page.getByRole('figure').filter({ hasText: 'src/TaskBoard.Api/Tasks/TaskRepository.cs' })
  await expect(passage.locator('.line', { hasText: 'var skip = request.Page * request.PageSize;' }).locator('.line-number')).toHaveText('25')
  await expect(page.getByRole('list').filter({ hasText: 'Started investigating at commit 4a07032f4525.' }).getByRole('listitem')).toHaveCount(3)
  await expect(page.getByText('fake-extractive-v1')).toBeVisible()
}

async function expectPlan(page: Page) {
  await expect(page.getByRole('heading', { level: 2, name: 'Plan for issue #5' })).toBeVisible()
  await expect(page.getByText('The plan is ready.')).toBeVisible()
  await expect(page.getByText('trace-demo/sample-repo · Requests to paths that merely contain /health skip the API key check')).toBeVisible()
  await expect(page.locator('ol.plan-list > li')).toHaveCount(3)
  await expect(page.locator('ol.plan-list > li').first()).toContainText('Review src/TaskBoard.Api/Auth/ApiKeyMiddleware.cs lines 1-28')
  await expect(page.locator('.plan > ol.evidence-list > li')).toHaveCount(3)
  await expect(page.locator('.provenance')).toContainText('Read at commit 4a07032f4525 by model fake-extractive-v1 with prompt plan-prompt-v1.')
}

async function expectNoSidewaysScroll(page: Page) {
  const { scrollWidth, clientWidth } = await page.evaluate(() => ({
    scrollWidth: document.documentElement.scrollWidth,
    clientWidth: document.documentElement.clientWidth,
  }))
  expect(scrollWidth, 'the page must not scroll sideways').toBeLessThanOrEqual(clientWidth)
}

/** Presses Tab until the target has the keyboard, as a person would, and checks that the focus can be seen. */
async function tabTo(page: Page, target: Locator) {
  for (let presses = 0; presses < 150; presses++) {
    if ((await target.count()) === 1 && (await target.evaluate((element) => element === document.activeElement))) {
      break
    }
    await page.keyboard.press('Tab')
  }
  await expect(target).toBeFocused()
  const outline = await target.evaluate((element) => {
    const style = getComputedStyle(element)
    return { visible: element.matches(':focus-visible'), style: style.outlineStyle, width: parseFloat(style.outlineWidth) }
  })
  expect(outline.visible, 'focus reached by keyboard is shown').toBe(true)
  expect(outline.style).not.toBe('none')
  expect(outline.width).toBeGreaterThanOrEqual(2)
}

test('the history lists what was just asked and planned, newest first, and can be filtered', async ({ page }) => {
  await page.goto('/history')
  await expect(page.getByRole('heading', { level: 2, name: 'History' })).toBeVisible()
  await expect(page).toHaveTitle('History · TRACE')
  await expect(page.getByText('Nothing here yet. Questions you ask and plans you start will be listed here.')).toBeVisible()
  await expect(page.getByText('This list shows what was asked from this browser')).toBeVisible()

  const investigation = await ask(page, QUESTION)
  const plan = await startPlan(page)

  await page.getByRole('button', { name: 'History' }).click()
  await expect(page).toHaveURL(/\/history$/)
  await expect(page.getByRole('heading', { level: 2, name: 'History' })).toBeFocused()

  const rows = page.locator('.history-list > li')
  await expect(rows).toHaveCount(2)
  await expect(rows.nth(0).getByRole('link')).toHaveText('Plan for issue #5: Requests to paths that merely contain /health skip the API key check')
  await expect(rows.nth(0).getByRole('link')).toHaveAttribute('href', plan)
  await expect(rows.nth(0)).toContainText('Plan · trace-demo/sample-repo · Planned')
  await expect(rows.nth(1).getByRole('link')).toHaveText(QUESTION)
  await expect(rows.nth(1).getByRole('link')).toHaveAttribute('href', investigation)
  await expect(rows.nth(1)).toContainText('Question · trace-demo/sample-repo · Answered')
  await expect(rows.nth(1).locator('time')).toHaveAttribute('datetime', /^\d{4}-\d{2}-\d{2}T/)
  await expect(page.getByRole('button', { name: 'Load more' })).toHaveCount(0)

  await page.getByLabel('Kind').selectOption('question')
  await expect(rows).toHaveCount(1)
  await expect(rows.first().getByRole('link')).toHaveText(QUESTION)
  await page.getByLabel('Kind').selectOption('issue_to_plan')
  await expect(rows).toHaveCount(1)
  await expect(rows.first()).toContainText('Plan for issue #5')
  await page.getByLabel('Kind').selectOption('')
  await page.getByLabel('Repository').selectOption({ label: 'trace-demo/sample-repo' })
  await expect(rows).toHaveCount(2)

  // A row opens the item at its own address.
  await rows.nth(1).getByRole('link').click()
  await expect(page).toHaveURL(new RegExp(`${investigation}$`))
  await expect(page.getByRole('heading', { level: 2, name: 'Ask a question' })).toBeFocused()
  await expectAnswer(page)
})

test('another browser has its own history, and paging through one loads the rest', async ({ page, browser }) => {
  // 21 questions: one more than a page of the list holds.
  await page.goto('/')
  const repository = await page.evaluate(async () => {
    const [demo] = (await (await fetch('/api/v1/repositories')).json()) as { id: string; latestSnapshot: { id: string } }[]
    for (let number = 1; number <= 21; number++) {
      const response = await fetch('/api/v1/investigations', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ repositoryId: demo.id, snapshotId: demo.latestSnapshot.id, type: 'question', question: `Paging question ${number}` }),
      })
      if (response.status !== 202) {
        throw new Error(`asking failed with ${response.status}`)
      }
    }
    return demo.id
  })
  expect(repository).toMatch(/^[0-9a-f-]{36}$/)

  await page.goto('/history')
  const rows = page.locator('.history-list > li')
  await expect(rows).toHaveCount(20)
  await expect(rows.first().getByRole('link')).toHaveText('Paging question 21')
  await page.getByRole('button', { name: 'Load more' }).click()
  await expect(rows).toHaveCount(21)
  await expect(rows.last().getByRole('link')).toHaveText('Paging question 1')
  await expect(page.getByRole('button', { name: 'Load more' })).toHaveCount(0)

  // A second browser profile sees none of it in its list.
  const other = await browser.newContext()
  try {
    const stranger = await other.newPage()
    await stranger.goto('/history')
    await expect(stranger.getByText('Nothing here yet.')).toBeVisible()
    await expect(stranger.locator('.history-list')).toHaveCount(0)
  } finally {
    await other.close()
  }
})

test('a direct link opens a finished investigation and a finished plan in a fresh browser', async ({ page, browser }) => {
  const investigation = await ask(page, QUESTION)
  const plan = await startPlan(page)

  // Someone else who may see the demo repository, with nothing in memory and no cookie.
  const other = await browser.newContext()
  try {
    const fresh = await other.newPage()
    await fresh.goto(investigation)
    await expect(fresh).toHaveTitle('Question · TRACE')
    await expectAnswer(fresh)
    await expect(fresh.getByRole('button', { name: 'Question about the code' })).toHaveAttribute('aria-pressed', 'true')

    await fresh.goto(plan)
    await expect(fresh).toHaveTitle('Plan · TRACE')
    await expectPlan(fresh)
    await expect(fresh.getByRole('button', { name: 'Plan from an issue' })).toHaveAttribute('aria-pressed', 'true')

    // The link gave the reader the item, not the asker's history.
    await fresh.goto('/history')
    await expect(fresh.getByText('Nothing here yet.')).toBeVisible()
  } finally {
    await other.close()
  }
})

test('reloading an investigation and a plan shows the same result', async ({ page }) => {
  const investigation = await ask(page, QUESTION)
  await expectAnswer(page)
  await page.reload()
  await expect(page).toHaveURL(new RegExp(`${investigation}$`))
  await expectAnswer(page)

  const plan = await startPlan(page)
  await expectPlan(page)
  await page.getByRole('button', { name: 'Mark as reviewed' }).click()
  await expect(page.getByText('Marked as reviewed on')).toBeVisible()
  await page.reload()
  await expect(page).toHaveURL(new RegExp(`${plan}$`))
  await expectPlan(page)
  // What was done to the plan is still there too.
  await expect(page.getByText('Marked as reviewed on')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Mark as reviewed' })).toHaveCount(0)

  // From a reloaded plan the way back to its issue still works.
  await page.getByRole('button', { name: 'Back to the issue' }).click()
  await expect(page).toHaveURL(/\/plan$/)
  await expect(page.getByRole('heading', { level: 2, name: 'Issue #5' })).toBeFocused()
  await expect(page.locator('.issue-text')).toContainText('Only the exact health probe should be exempt.')
})

test('an investigation and a plan opened by address while in progress show progress, then the result', async ({ page }) => {
  const investigation = await ask(page, QUESTION)
  const plan = await startPlan(page)

  // The fake model finishes in milliseconds, so "in progress" is held open here: the real
  // answers of the first reads are passed on with the state they had while the run was going.
  // Everything else, and every later read, is the API's own.
  let investigationReads = 0
  let planReads = 0
  await page.route(`**/api/v1${investigation}`, async (route) => {
    const response = await route.fetch()
    const body = await response.json()
    await route.fulfill({ response, json: ++investigationReads <= 3 ? { ...body, state: 'running', result: null } : body })
  })
  await page.route(`**/api/v1${plan}`, async (route) => {
    const response = await route.fetch()
    const body = await response.json()
    await route.fulfill({ response, json: ++planReads <= 3 ? { ...body, state: 'running', plan: null, trimmed: null } : body })
  })

  await page.goto(investigation)
  await expect(page.getByText('Looking through the code…')).toBeVisible()
  await expect(page.getByRole('region', { name: 'Investigation in progress' })).toContainText('Started investigating at commit 4a07032f4525.')
  await expect(page.getByRole('region', { name: 'Investigation in progress' }).locator('.asked')).toHaveText(QUESTION)
  await expect(page.getByRole('button', { name: 'Cancel' })).toBeVisible()
  await expectAnswer(page)
  await expect(page.getByText('Looking through the code…')).toHaveCount(0)
  expect(investigationReads).toBeGreaterThan(3)

  await page.goto(plan)
  await expect(page.getByText('Reading the issue and the code…')).toBeVisible()
  await expect(page.getByRole('region', { name: 'Plan in progress' })).toContainText('Started investigating at commit 4a07032f4525.')
  await expectPlan(page)
  await expect(page.getByText('Reading the issue and the code…')).toHaveCount(0)
  expect(planReads).toBeGreaterThan(3)
})

test('an unknown or malformed address shows the not-found page, never an error, with a way back', async ({ page }) => {
  for (const address of [`/investigations/${UNKNOWN_ID}`, `/plans/${UNKNOWN_ID}`, '/investigations/not-an-id', '/plans/1', '/no/such/page']) {
    await page.goto(address)
    await expect(page.getByRole('heading', { level: 2, name: 'Page not found' }), address).toBeVisible()
    await expect(page).toHaveTitle('Not found · TRACE')
    await expect(page.getByRole('alert')).toHaveCount(0)
    await expect(page.getByRole('button', { name: 'Ask' })).toHaveCount(0)
    await expect(page.getByRole('heading', { name: 'Repositories' })).toHaveCount(0)
  }

  await page.getByRole('link', { name: 'See your history' }).click()
  await expect(page).toHaveURL(/\/history$/)
  await expect(page.getByRole('heading', { level: 2, name: 'History' })).toBeFocused()

  await page.goto(`/plans/${UNKNOWN_ID}`)
  await page.getByRole('link', { name: 'Ask a question' }).click()
  await expect(page).toHaveURL(/\/$/)
  await expect(page.getByRole('button', { name: 'Ask' })).toBeVisible()
  // The plan side does not lead back to the address that named nothing.
  await page.getByRole('button', { name: 'Plan from an issue' }).click()
  await expect(page).toHaveURL(/\/plan$/)
  await expect(page.getByRole('heading', { level: 2, name: 'Open issues' })).toBeVisible()
})

test('an address under /api that the API does not have is the API’s 404, not the app’s page', async ({ request }) => {
  for (const address of ['/api/v1/no-such-thing', `/api/v1/investigations/${UNKNOWN_ID}`, '/api/v1/investigations/not-an-id', '/api/v1/history']) {
    const response = await request.get(address)
    expect(response.status(), address).toBe(404)
    expect(await response.text(), address).not.toContain('<div id="root">')
    expect(response.headers()['content-type'] ?? '', address).not.toContain('text/html')
  }

  // And an address of the app is the app, whatever its depth.
  for (const address of ['/history', `/plans/${UNKNOWN_ID}`]) {
    const response = await request.get(address, { headers: { Accept: 'text/html' } })
    expect(response.status(), address).toBe(200)
    expect(await response.text(), address).toContain('<div id="root">')
  }
})

test('Back and Forward move between the pages that were visited', async ({ page }) => {
  const investigation = await ask(page, QUESTION)
  await page.getByRole('button', { name: 'History' }).click()
  await expect(page.locator('.history-list > li')).toHaveCount(1)
  await page.getByRole('button', { name: 'Plan from an issue' }).click()
  await expect(page.getByRole('heading', { level: 2, name: 'Open issues' })).toBeVisible()

  await page.goBack()
  await expect(page).toHaveURL(/\/history$/)
  await expect(page.getByRole('heading', { level: 2, name: 'History' })).toBeFocused()
  await expect(page.locator('.history-list > li')).toHaveCount(1)

  await page.goBack()
  await expect(page).toHaveURL(new RegExp(`${investigation}$`))
  await expectAnswer(page)

  // Before the question was asked: the empty form, without the answer.
  await page.goBack()
  await expect(page).toHaveURL(/\/$/)
  await expect(page.getByRole('button', { name: 'Ask' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Answer' })).toHaveCount(0)

  await page.goForward()
  await expect(page).toHaveURL(new RegExp(`${investigation}$`))
  await expectAnswer(page)
  await page.goForward()
  await expect(page).toHaveURL(/\/history$/)
  await page.goForward()
  await expect(page).toHaveURL(/\/plan$/)
  await expect(page.getByRole('heading', { level: 2, name: 'Open issues' })).toBeFocused()
})

test('a history item can be reached and opened with the keyboard alone', async ({ page }) => {
  const plan = await startPlan(page)
  await ask(page, QUESTION)

  // No pointer from here on.
  await page.goto('/')
  await expect(page.getByLabel('Question')).toBeVisible()
  await tabTo(page, page.getByRole('button', { name: 'History' }))
  await page.keyboard.press('Enter')
  await expect(page.getByRole('heading', { level: 2, name: 'History' })).toBeFocused()

  const planLink = page.getByRole('link', { name: /^Plan for issue #5/ })
  await tabTo(page, planLink)
  await page.keyboard.press('Enter')

  await expect(page).toHaveURL(new RegExp(`${plan}$`))
  await expect(page.getByRole('heading', { level: 2, name: 'Plan for issue #5' })).toBeFocused()
  await expectPlan(page)
  expect(plan).toMatch(ITEM)
})

test.describe('at phone width', () => {
  test.use({ viewport: { width: 375, height: 740 } })

  test('the history and the not-found page fit without scrolling sideways', async ({ page }) => {
    // A question without a single place to break, as long as a question may be.
    const unbroken = `Why${'x'.repeat(3990)}?`
    await ask(page, unbroken)
    await startPlan(page)
    await ask(page, QUESTION)

    await page.goto('/history')
    await expect(page.locator('.history-list > li')).toHaveCount(3)
    await expect(page.getByRole('link', { name: unbroken })).toBeVisible()
    await expectNoSidewaysScroll(page)
    const column = (await page.getByRole('main').boundingBox())!
    for (const row of await page.locator('.history-list > li').all()) {
      const box = (await row.boundingBox())!
      expect(box.x + box.width).toBeLessThanOrEqual(column.x + column.width)
    }

    await page.getByLabel('Kind').selectOption('issue_to_plan')
    await expect(page.locator('.history-list > li')).toHaveCount(1)
    await expectNoSidewaysScroll(page)

    await page.goto(`/investigations/${UNKNOWN_ID}`)
    await expect(page.getByRole('heading', { level: 2, name: 'Page not found' })).toBeVisible()
    await expectNoSidewaysScroll(page)

    await page.goto(`/${'this-is-not-one-of-the-apps-addresses/'.repeat(8)}and-it-is-rather-long`)
    await expect(page.getByRole('heading', { level: 2, name: 'Page not found' })).toBeVisible()
    await expectNoSidewaysScroll(page)
  })
})
