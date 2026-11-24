// History, addresses and direct links, through the whole app with a mocked API.

import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, test, vi } from 'vitest'
import type { HistoryItem, Investigation, InvestigationEvent, Plan, Repository } from './api.ts'
import App from './App.tsx'

const REPO = '0193a5c0-0000-7000-8000-0000000000a1'
const OTHER_REPO = '0193a5c0-0000-7000-8000-0000000000a2'
const SNAPSHOT = '0193a5c0-0000-7000-8000-0000000000b1'
const INV = '0193a5c0-0000-7000-8000-0000000000c1'
const PLAN_INV = '0193a5c0-0000-7000-8000-0000000000c2'
const PLAN = '0193a5c0-0000-7000-8000-0000000000d1'
const UNKNOWN = '0193a5c0-0000-7000-8000-0000000000ff'
const E1 = '0193a5c0-0000-7000-8000-0000000000e1'
const SHA = 'ee02b2da366aa000b30541463014b895d5cebb67'

const repository: Repository = {
  id: REPO,
  owner: 'trace-demo',
  name: 'sample-repo',
  defaultBranch: 'main',
  latestSnapshot: { id: SNAPSHOT, commitSha: SHA, branch: 'main', completedAt: '2026-11-20T12:00:00Z' },
}
const otherRepository: Repository = { ...repository, id: OTHER_REPO, owner: 'acme', name: 'billing' }

const answered: Investigation = {
  investigationId: INV,
  jobId: 'job-1',
  repositoryId: REPO,
  snapshotId: SNAPSHOT,
  commitSha: SHA,
  type: 'question',
  question: 'Why does page one skip results?',
  state: 'completed',
  modelId: 'fake-extractive-v1',
  promptVersion: 'question-v1',
  createdAt: '2026-11-20T12:00:01Z',
  updatedAt: '2026-11-20T12:00:02Z',
  errorCode: null,
  result: {
    outcome: 'answered',
    answer: 'List skips a full page before the first one.',
    evidence: [
      { source: 'code', path: 'src/Tasks/TaskRepository.cs', startLine: 24, endLine: 24, commitSha: SHA, supports: 'Shows the skip.', excerpt: 'var skip = request.Page * request.PageSize;' },
    ],
    unknowns: [],
    limitations: [],
  },
}
const running: Investigation = { ...answered, state: 'running', result: null }

const planned: Plan = {
  planId: PLAN,
  investigationId: PLAN_INV,
  jobId: 'job-9',
  repositoryId: REPO,
  snapshotId: SNAPSHOT,
  commitSha: SHA,
  issue: { number: 5, title: 'Health paths skip the key check' },
  state: 'planned',
  errorCode: null,
  modelId: 'fake-extractive-v1',
  promptVersion: 'plan-prompt-v1',
  createdAt: '2026-11-20T12:00:01Z',
  updatedAt: '2026-11-20T12:00:02Z',
  usage: { inputTokens: 0, outputTokens: 0 },
  plan: {
    schemaVersion: 'plan-v1',
    summary: { text: 'Match the exact health path.', citations: [E1] },
    steps: [{ text: 'Compare the path exactly in ApiKeyMiddleware.', citations: [E1] }],
    filesToChange: [{ path: 'src/Auth/ApiKeyMiddleware.cs', change: 'modify', reason: 'The path is checked here.', citations: [E1] }],
    risks: [],
    verification: [],
    unknowns: [],
    evidence: [{ id: E1, source: 'code', path: 'src/Auth/ApiKeyMiddleware.cs', startLine: 12, endLine: 12, commitSha: SHA, excerpt: 'if (path.Contains("/health"))' }],
  },
  trimmed: { citations: 0, steps: 0, files: 0, risks: 0, verification: 0, unknowns: 0, summaryReplaced: false },
  limitations: [],
  reviewedAt: null,
  issueCheck: null,
}
const runningPlan: Plan = { ...planned, state: 'running', plan: null, trimmed: null }

const events: InvestigationEvent[] = [
  { seq: 1, eventType: 'started', toolName: null, summary: 'Started investigating at commit ee02b2da366a.', occurredAt: '' },
  { seq: 2, eventType: 'completed', toolName: null, summary: 'Answered with 1 validated evidence reference(s).', occurredAt: '' },
]

const questionRow: HistoryItem = {
  investigationId: INV,
  type: 'question',
  repository: { id: REPO, owner: 'trace-demo', name: 'sample-repo' },
  state: 'completed',
  outcome: 'answered',
  createdAt: '2026-11-20T12:00:01Z',
  question: 'Why does page one skip results?',
  issue: null,
  planId: null,
}
const planRow: HistoryItem = {
  investigationId: PLAN_INV,
  type: 'issue_to_plan',
  repository: { id: REPO, owner: 'trace-demo', name: 'sample-repo' },
  state: 'completed',
  outcome: 'planned',
  createdAt: '2026-11-21T09:30:00Z',
  question: null,
  issue: { number: 5, title: 'Health paths skip the key check' },
  planId: PLAN,
}

const HISTORY = 'GET /api/v1/investigations?limit=20'

type Route = (init?: RequestInit) => Response | Promise<Response>

function json(body: unknown, status = 200, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } })
}

const notFound = () => json({ title: 'Not Found', status: 404 }, 404, { 'X-Correlation-ID': 'ref-404' })

function renderAt(path: string, routes: Record<string, Route>) {
  window.history.replaceState(null, '', path)
  const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const key = `${init?.method ?? 'GET'} ${String(input)}`
    const route = routes[key]
    return route ? Promise.resolve(route(init)) : Promise.reject(new TypeError(`No route for ${key}`))
  })
  vi.stubGlobal('fetch', fetchMock)
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <App />
    </QueryClientProvider>,
  )
  return fetchMock
}

const base: Record<string, Route> = {
  'GET /api/v1/repositories': () => json([repository, otherRepository]),
  'GET /api/v1/me': () => json({ user: { login: 'alice' }, signInAvailable: true, canStartWork: true }),
}

function requested(fetchMock: ReturnType<typeof renderAt>): string[] {
  return fetchMock.mock.calls.map(([input, init]) => `${init?.method ?? 'GET'} ${String(input)}`)
}

/** What the Back and Forward buttons do: the address changes and the page is told. */
async function go(delta: -1 | 1, expectedPath: string) {
  act(() => window.history.go(delta))
  await waitFor(() => expect(window.location.pathname).toBe(expectedPath))
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

describe('the history list', () => {
  test('lists questions and plans with kind, repository, state and time, each as a link to its own address', async () => {
    renderAt('/history', { ...base, [HISTORY]: () => json({ items: [planRow, questionRow], nextCursor: null }) })

    expect(await screen.findByRole('heading', { level: 2, name: 'History' })).toBeTruthy()
    await screen.findByRole('link', { name: 'Why does page one skip results?' })
    const rows = within(document.querySelector('.history-list') as HTMLElement).getAllByRole('listitem')
    expect(rows).toHaveLength(2)

    const plan = within(rows[0]).getByRole('link', { name: 'Plan for issue #5: Health paths skip the key check' }) as HTMLAnchorElement
    expect(plan.getAttribute('href')).toBe(`/plans/${PLAN}`)
    expect(rows[0].textContent).toContain('Plan · trace-demo/sample-repo · Planned')
    expect(rows[0].querySelector('time')!.getAttribute('datetime')).toBe('2026-11-21T09:30:00.000Z')

    const question = within(rows[1]).getByRole('link', { name: 'Why does page one skip results?' }) as HTMLAnchorElement
    expect(question.getAttribute('href')).toBe(`/investigations/${INV}`)
    expect(rows[1].textContent).toContain('Question · trace-demo/sample-repo · Answered')

    expect(screen.getByText(/Only you can list them/)).toBeTruthy()
    expect(screen.getByRole('button', { name: 'History' }).getAttribute('aria-pressed')).toBe('true')
    expect(screen.queryByRole('button', { name: 'Load more' })).toBeNull()
    expect(document.title).toBe('History · TRACE')
    // The other pages are not on this one.
    expect(screen.queryByRole('button', { name: 'Ask' })).toBeNull()
    expect(screen.queryByRole('heading', { name: 'Repositories' })).toBeNull()
  })

  test('words every state and outcome, and an unknown one generically', async () => {
    const row = (index: number, state: string, outcome: string | null): HistoryItem => ({
      ...questionRow,
      investigationId: `0193a5c0-0000-7000-8000-00000000010${index}`,
      question: `question ${index}`,
      state,
      outcome,
    })
    renderAt('/history', {
      ...base,
      [HISTORY]: () =>
        json({
          items: [
            row(1, 'queued', null),
            row(2, 'running', null),
            row(3, 'completed', 'insufficient_evidence'),
            row(4, 'failed', null),
            row(5, 'cancelled', null),
            row(6, 'completed', null),
            row(7, 'constructor', 'toString'),
          ],
          nextCursor: null,
        }),
    })

    await screen.findByText('question 1')
    expect([...document.querySelectorAll('.history-state')].map((state) => state.textContent)).toEqual([
      'Waiting for a worker',
      'In progress',
      'Not enough evidence',
      'Failed',
      'Cancelled',
      'Finished',
      'Unknown state',
    ])
  })

  test('shows a loading state, then an empty state that says what will appear', async () => {
    let release: (response: Response) => void = () => {}
    renderAt('/history', { ...base, [HISTORY]: () => new Promise<Response>((resolve) => (release = resolve)) })

    expect(await screen.findByText('Loading your history…')).toBeTruthy()
    release(json({ items: [], nextCursor: null }))

    expect(await screen.findByText('Nothing here yet. Questions you ask and plans you start will be listed here.')).toBeTruthy()
    expect(screen.queryByText('Loading your history…')).toBeNull()
    expect(document.querySelector('.history-list')).toBeNull()
  })

  test('filters by repository and kind, asking the API again, and says when nothing matches', async () => {
    const fetchMock = renderAt('/history', {
      ...base,
      [HISTORY]: () => json({ items: [planRow, questionRow], nextCursor: null }),
      [`${HISTORY}&repositoryId=${OTHER_REPO}`]: () => json({ items: [], nextCursor: null }),
      [`${HISTORY}&type=issue_to_plan`]: () => json({ items: [planRow], nextCursor: null }),
      [`${HISTORY}&repositoryId=${OTHER_REPO}&type=question`]: () => json({ items: [], nextCursor: null }),
    })
    await screen.findByRole('link', { name: 'Why does page one skip results?' })

    const repositoryFilter = screen.getByLabelText('Repository') as HTMLSelectElement
    expect([...repositoryFilter.options].map((option) => option.textContent)).toEqual(['All repositories', 'trace-demo/sample-repo', 'acme/billing'])

    fireEvent.change(repositoryFilter, { target: { value: OTHER_REPO } })
    expect(await screen.findByText('Nothing in your history matches these filters.')).toBeTruthy()

    fireEvent.change(repositoryFilter, { target: { value: '' } })
    fireEvent.change(screen.getByLabelText('Kind'), { target: { value: 'issue_to_plan' } })
    await waitFor(() => expect(document.querySelectorAll('.history-list > li')).toHaveLength(1))
    expect(screen.getByRole('link', { name: /^Plan for issue #5/ })).toBeTruthy()

    fireEvent.change(repositoryFilter, { target: { value: OTHER_REPO } })
    fireEvent.change(screen.getByLabelText('Kind'), { target: { value: 'question' } })
    expect(await screen.findByText('Nothing in your history matches these filters.')).toBeTruthy()
    expect(requested(fetchMock)).toContain(`${HISTORY}&repositoryId=${OTHER_REPO}&type=question`)
  })

  test('loads more with the cursor of the page before, and stops offering when there is none', async () => {
    const fetchMock = renderAt('/history', {
      ...base,
      [HISTORY]: () => json({ items: [planRow], nextCursor: 'cursor-1' }),
      [`${HISTORY}&cursor=cursor-1`]: () => json({ items: [questionRow], nextCursor: null }),
    })

    fireEvent.click(await screen.findByRole('button', { name: 'Load more' }))

    expect(await screen.findByRole('link', { name: 'Why does page one skip results?' })).toBeTruthy()
    expect(document.querySelectorAll('.history-list > li')).toHaveLength(2)
    expect(screen.queryByRole('button', { name: 'Load more' })).toBeNull()
    expect(requested(fetchMock).filter((call) => call.startsWith(HISTORY))).toEqual([HISTORY, `${HISTORY}&cursor=cursor-1`])
  })

  test('keeps what is listed when loading more fails, and says so with the reference', async () => {
    renderAt('/history', {
      ...base,
      [HISTORY]: () => json({ items: [planRow], nextCursor: 'cursor-1' }),
      [`${HISTORY}&cursor=cursor-1`]: () => json({ detail: 'The cursor is not one this API returned.', code: 'invalid_cursor' }, 400, { 'X-Correlation-ID': 'ref-more' }),
    })

    fireEvent.click(await screen.findByRole('button', { name: 'Load more' }))

    expect((await screen.findByRole('alert')).textContent).toBe(
      'More could not be loaded. The cursor is not one this API returned. (Reference: ref-more)',
    )
    expect(screen.getByRole('link', { name: /^Plan for issue #5/ })).toBeTruthy()
  })

  test('shows an error with the reference when the list cannot be loaded, and can try again', async () => {
    let attempts = 0
    renderAt('/history', {
      ...base,
      [HISTORY]: () =>
        ++attempts === 1
          ? json({ detail: 'Something went wrong.' }, 500, { 'X-Correlation-ID': 'ref-500' })
          : json({ items: [questionRow], nextCursor: null }),
    })

    expect((await screen.findByRole('alert')).textContent).toBe('Your history could not be loaded. Something went wrong. (Reference: ref-500)')
    expect(screen.queryByText(/Nothing here yet/)).toBeNull()
    expect(screen.queryByRole('heading', { name: 'Page not found' })).toBeNull()

    fireEvent.click(screen.getByRole('button', { name: 'Try again' }))
    expect(await screen.findByRole('link', { name: 'Why does page one skip results?' })).toBeTruthy()
  })

  test('tells a signed-out visitor who may ask that the list is this browser’s', async () => {
    renderAt('/history', {
      ...base,
      'GET /api/v1/me': () => json({ user: null, signInAvailable: false, canStartWork: true }),
      [HISTORY]: () => json({ items: [questionRow], nextCursor: null }),
    })

    expect(await screen.findByText(/You are not signed in\. This list shows what was asked from this browser/)).toBeTruthy()
    expect(await screen.findByRole('link', { name: 'Why does page one skip results?' })).toBeTruthy()
  })

  test('tells a signed-out visitor who may not ask why the list is empty', async () => {
    renderAt('/history', {
      ...base,
      'GET /api/v1/me': () => json({ user: null, signInAvailable: true, canStartWork: false }),
      [HISTORY]: () => json({ items: [], nextCursor: null }),
    })

    expect(await screen.findByText(/You are not signed in, so there is no history to show\. Sign in to see/)).toBeTruthy()
    expect(await screen.findByText(/Nothing here yet/)).toBeTruthy()
  })

  test('a row whose ID is not an ID is text, not a link', async () => {
    renderAt('/history', {
      ...base,
      [HISTORY]: () =>
        json({
          items: [
            { ...questionRow, investigationId: '../../api/v1/auth/logout', question: 'odd one' },
            { ...planRow, planId: 'javascript:alert(1)' },
            { ...planRow, investigationId: UNKNOWN, planId: null, issue: { number: 7, title: null } },
          ],
          nextCursor: null,
        }),
    })

    expect((await screen.findByText('odd one')).tagName).toBe('SPAN')
    expect(screen.getByText('Plan for issue #5: Health paths skip the key check').tagName).toBe('SPAN')
    expect(screen.getByText('Plan for issue #7').tagName).toBe('SPAN')
    expect(document.querySelector('.history a')).toBeNull()
  })
})

describe('moving between pages', () => {
  test('opens a history row at its address, moves the keyboard to the page heading, and Back and Forward work', async () => {
    renderAt('/history', {
      ...base,
      [HISTORY]: () => json({ items: [planRow, questionRow], nextCursor: null }),
      [`GET /api/v1/investigations/${INV}`]: () => json(answered),
      [`GET /api/v1/investigations/${INV}/events`]: () => json(events),
    })

    fireEvent.click(await screen.findByRole('link', { name: 'Why does page one skip results?' }))

    expect(window.location.pathname).toBe(`/investigations/${INV}`)
    expect(await screen.findByRole('heading', { name: 'Answer' })).toBeTruthy()
    expect(screen.getByText('List skips a full page before the first one.')).toBeTruthy()
    expect(screen.queryByRole('heading', { name: 'History' })).toBeNull()
    expect(document.activeElement).toBe(screen.getByRole('heading', { level: 2, name: 'Ask a question' }))
    expect(document.title).toBe('Question · TRACE')
    expect(screen.getByRole('button', { name: 'Question about the code' }).getAttribute('aria-pressed')).toBe('true')

    await go(-1, '/history')
    expect(await screen.findByRole('link', { name: /^Plan for issue #5/ })).toBeTruthy()
    expect(document.activeElement).toBe(screen.getByRole('heading', { level: 2, name: 'History' }))
    expect(document.title).toBe('History · TRACE')

    await go(1, `/investigations/${INV}`)
    expect(await screen.findByRole('heading', { name: 'Answer' })).toBeTruthy()
  })

  test('a click with a modifier key is left to the browser, so a row opens in a new tab', async () => {
    renderAt('/history', { ...base, [HISTORY]: () => json({ items: [questionRow], nextCursor: null }) })

    const link = await screen.findByRole('link', { name: 'Why does page one skip results?' })
    // jsdom does not follow links: the page stays, and so does the address.
    fireEvent.click(link, { ctrlKey: true })
    fireEvent.click(link, { metaKey: true })
    fireEvent.click(link, { button: 1 })

    expect(window.location.pathname).toBe('/history')
    expect(screen.getByRole('heading', { level: 2, name: 'History' })).toBeTruthy()
  })

  test('the three sides each have an address and a title, and the keyboard follows', async () => {
    renderAt('/', {
      ...base,
      [HISTORY]: () => json({ items: [], nextCursor: null }),
      [`GET /api/v1/repositories/${REPO}/issues?limit=20`]: () => json({ items: [], nextCursor: null }),
    })
    await screen.findByRole('button', { name: 'Ask' })
    expect(document.title).toBe('TRACE')

    fireEvent.click(screen.getByRole('button', { name: 'History' }))
    expect(window.location.pathname).toBe('/history')
    expect(document.activeElement).toBe(await screen.findByRole('heading', { level: 2, name: 'History' }))

    fireEvent.click(screen.getByRole('button', { name: 'Plan from an issue' }))
    expect(window.location.pathname).toBe('/plan')
    expect(document.activeElement).toBe(await screen.findByRole('heading', { level: 2, name: 'Open issues' }))
    expect(document.title).toBe('Plan from an issue · TRACE')

    fireEvent.click(screen.getByRole('button', { name: 'Question about the code' }))
    expect(window.location.pathname).toBe('/')
    expect(document.activeElement).toBe(screen.getByRole('heading', { level: 2, name: 'Ask a question' }))
    expect(document.title).toBe('TRACE')

    await go(-1, '/plan')
    expect(await screen.findByRole('heading', { level: 2, name: 'Open issues' })).toBeTruthy()
    expect(screen.queryByRole('button', { name: 'Ask' })).toBeNull()
  })

  test('asking gives the new investigation its address, and Back returns to the empty form', async () => {
    renderAt('/', {
      ...base,
      'POST /api/v1/investigations': () => json(answered, 202),
      [`GET /api/v1/investigations/${INV}/events`]: () => json(events),
    })

    fireEvent.change(await screen.findByLabelText('Question'), { target: { value: 'Why does page one skip results?' } })
    const askButton = screen.getByRole('button', { name: 'Ask' })
    askButton.focus()
    fireEvent.click(askButton)

    expect(await screen.findByRole('heading', { name: 'Answer' })).toBeTruthy()
    expect(window.location.pathname).toBe(`/investigations/${INV}`)
    // Nothing moved on the screen, so the keyboard was not sent anywhere.
    expect(document.activeElement).not.toBe(screen.getByRole('heading', { level: 2, name: 'Ask a question' }))

    await go(-1, '/')
    await waitFor(() => expect(screen.queryByRole('heading', { name: 'Answer' })).toBeNull())
    expect(screen.getByRole('button', { name: 'Ask' })).toBeTruthy()
  })

  test('starting a plan gives it its address, and Back to issues leaves it', async () => {
    const issue = { number: 5, title: 'Health paths skip the key check', state: 'open', author: 'lee', labels: [], commentCount: 0, createdAt: '', updatedAt: '' }
    renderAt('/plan', {
      ...base,
      [`GET /api/v1/repositories/${REPO}/issues?limit=20`]: () => json({ items: [issue], nextCursor: null }),
      [`GET /api/v1/repositories/${REPO}/issues/5`]: () => json({ ...issue, body: 'Body.', bodyTruncated: false, comments: [], commentsTruncated: false }),
      [`POST /api/v1/repositories/${REPO}/issues/5/plans`]: () => json(planned, 202),
      [`GET /api/v1/investigations/${PLAN_INV}/events`]: () => json(events),
    })

    fireEvent.click(await screen.findByRole('button', { name: '#5 Health paths skip the key check' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Start a plan' }))

    expect(await screen.findByRole('heading', { name: 'Summary' })).toBeTruthy()
    expect(window.location.pathname).toBe(`/plans/${PLAN}`)
    expect(document.title).toBe('Plan · TRACE')

    fireEvent.click(screen.getByRole('button', { name: 'Back to the issue' }))
    expect(window.location.pathname).toBe('/plan')
    expect(await screen.findByRole('heading', { level: 2, name: 'Issue #5' })).toBeTruthy()

    await go(-1, `/plans/${PLAN}`)
    expect(await screen.findByRole('heading', { name: 'Summary' })).toBeTruthy()

    fireEvent.click(screen.getByRole('button', { name: 'Back to issues' }))
    expect(window.location.pathname).toBe('/plan')
    expect(await screen.findByRole('heading', { level: 2, name: 'Open issues' })).toBeTruthy()
  })

  test('each side comes back to the item it was showing', async () => {
    renderAt(`/plans/${PLAN}`, {
      ...base,
      [`GET /api/v1/plans/${PLAN}`]: () => json(planned),
      [`GET /api/v1/investigations/${PLAN_INV}/events`]: () => json(events),
      [HISTORY]: () => json({ items: [], nextCursor: null }),
    })
    await screen.findByRole('heading', { name: 'Summary' })

    fireEvent.click(screen.getByRole('button', { name: 'History' }))
    await screen.findByRole('heading', { level: 2, name: 'History' })
    fireEvent.click(screen.getByRole('button', { name: 'Plan from an issue' }))

    expect(window.location.pathname).toBe(`/plans/${PLAN}`)
    expect(await screen.findByRole('heading', { name: 'Summary' })).toBeTruthy()
  })
})

describe('a direct link', () => {
  test('to a finished investigation shows the same result as the flow does', async () => {
    const fetchMock = renderAt(`/investigations/${INV}`, {
      ...base,
      [`GET /api/v1/investigations/${INV}`]: () => json(answered),
      [`GET /api/v1/investigations/${INV}/events`]: () => json(events),
    })

    expect(await screen.findByRole('heading', { name: 'Answer' })).toBeTruthy()
    expect(screen.getByText('Why does page one skip results?')).toBeTruthy()
    expect(screen.getByText('List skips a full page before the first one.')).toBeTruthy()
    expect(within(screen.getByRole('figure')).getByText('src/Tasks/TaskRepository.cs, lines 24–24')).toBeTruthy()
    expect(await screen.findByText('Answered with 1 validated evidence reference(s).')).toBeTruthy()
    expect(screen.getByText('fake-extractive-v1')).toBeTruthy()
    expect(document.title).toBe('Question · TRACE')
    expect(requested(fetchMock)).not.toContain('POST /api/v1/investigations')
    // The form to ask another question is there, as it is after asking.
    expect(screen.getByRole('button', { name: 'Ask' })).toBeTruthy()
  })

  test('to an investigation in progress keeps showing progress until it finishes', async () => {
    let polls = 0
    renderAt(`/investigations/${INV}`, {
      ...base,
      [`GET /api/v1/investigations/${INV}`]: () => json(++polls <= 2 ? running : answered),
      [`GET /api/v1/investigations/${INV}/events`]: () => json(events.slice(0, polls <= 2 ? 1 : 2)),
    })

    expect(await screen.findByText('Looking through the code…')).toBeTruthy()
    expect(screen.getByText('Why does page one skip results?')).toBeTruthy()
    expect(await screen.findByText('Started investigating at commit ee02b2da366a.')).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeTruthy()
    expect(screen.queryByRole('heading', { name: 'Answer' })).toBeNull()

    expect(await screen.findByRole('heading', { name: 'Answer' }, { timeout: 4000 })).toBeTruthy()
    expect(polls).toBeGreaterThanOrEqual(3)
    expect(screen.queryByText('Looking through the code…')).toBeNull()
    expect(await screen.findByText('Answered with 1 validated evidence reference(s).')).toBeTruthy()
  })

  test('to a finished plan shows the plan', async () => {
    renderAt(`/plans/${PLAN}`, {
      ...base,
      [`GET /api/v1/plans/${PLAN}`]: () => json(planned),
      [`GET /api/v1/investigations/${PLAN_INV}/events`]: () => json(events),
    })

    expect(await screen.findByRole('heading', { level: 2, name: 'Plan for issue #5' })).toBeTruthy()
    expect(await screen.findByText('The plan is ready.')).toBeTruthy()
    expect(screen.getByText('trace-demo/sample-repo · Health paths skip the key check')).toBeTruthy()
    expect(screen.getByText('Match the exact health path.')).toBeTruthy()
    expect(screen.getByText('Compare the path exactly in ApiKeyMiddleware.')).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Mark as reviewed' })).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Plan from an issue' }).getAttribute('aria-pressed')).toBe('true')
    expect(document.title).toBe('Plan · TRACE')
  })

  test('to a plan in progress keeps showing progress until it finishes', async () => {
    let polls = 0
    renderAt(`/plans/${PLAN}`, {
      ...base,
      [`GET /api/v1/plans/${PLAN}`]: () => json(++polls <= 2 ? runningPlan : planned),
      [`GET /api/v1/investigations/${PLAN_INV}/events`]: () => json(events.slice(0, 1)),
    })

    expect(await screen.findByText('Reading the issue and the code…')).toBeTruthy()
    expect(await screen.findByText('Started investigating at commit ee02b2da366a.')).toBeTruthy()
    expect(screen.queryByRole('heading', { name: 'Summary' })).toBeNull()

    expect(await screen.findByText('The plan is ready.', undefined, { timeout: 4000 })).toBeTruthy()
    expect(screen.getByRole('heading', { name: 'Summary' })).toBeTruthy()
    expect(polls).toBeGreaterThanOrEqual(3)
  })

  test.each([
    ['an investigation', `/investigations/${UNKNOWN}`, `GET /api/v1/investigations/${UNKNOWN}`],
    ['a plan', `/plans/${UNKNOWN}`, `GET /api/v1/plans/${UNKNOWN}`],
  ])('to %s that does not exist or may not be seen shows the not-found page, with a way back', async (_, path, request) => {
    // The API answers 404 for both "no such thing" and "not yours": the page cannot tell them apart either.
    const fetchMock = renderAt(path, { ...base, [request]: notFound })

    expect(await screen.findByRole('heading', { level: 2, name: 'Page not found' })).toBeTruthy()
    expect(screen.getByText(/It may never have existed, it may have been removed, or it belongs to a repository you do not have access to/)).toBeTruthy()
    expect(screen.queryByRole('alert')).toBeNull()
    expect(document.body.textContent).not.toContain('ref-404')
    expect(document.title).toBe('Not found · TRACE')
    expect(screen.queryByRole('button', { name: 'Ask' })).toBeNull()
    expect(screen.queryByRole('heading', { name: 'Repositories' })).toBeNull()
    expect(requested(fetchMock)).toContain(request)

    // The way back is a real link, and the side's button does not lead back to the dead address.
    const back = screen.getByRole('link', { name: 'Ask a question' }) as HTMLAnchorElement
    expect(back.getAttribute('href')).toBe('/')
    fireEvent.click(back)
    expect(window.location.pathname).toBe('/')
    expect(await screen.findByRole('button', { name: 'Ask' })).toBeTruthy()
    expect(screen.queryByRole('heading', { name: 'Page not found' })).toBeNull()
  })

  test.each([
    '/investigations/inv-1',
    '/plans/..%2F..%2Fapi',
    `/plans/${PLAN}0`,
    '/somewhere-else',
    '/history/extra',
  ])('to a malformed or unknown address shows the same not-found page and asks the API for nothing of it: %s', async (path) => {
    const fetchMock = renderAt(path, base)

    expect(await screen.findByRole('heading', { level: 2, name: 'Page not found' })).toBeTruthy()
    expect(document.title).toBe('Not found · TRACE')
    await screen.findByRole('button', { name: 'History' })
    expect(requested(fetchMock).filter((call) => /investigations|plans/.test(call))).toEqual([])
    expect(screen.queryByRole('button', { name: 'Ask' })).toBeNull()
  })

  test('to an investigation the API fails to return shows an error with the reference, not the not-found page', async () => {
    let attempts = 0
    renderAt(`/investigations/${INV}`, {
      ...base,
      [`GET /api/v1/investigations/${INV}`]: () =>
        ++attempts === 1 ? json({ detail: 'Something went wrong.' }, 500, { 'X-Correlation-ID': 'ref-inv-500' }) : json(answered),
      [`GET /api/v1/investigations/${INV}/events`]: () => json(events),
    })

    expect((await screen.findByRole('alert')).textContent).toBe(
      'The investigation could not be loaded. Something went wrong. (Reference: ref-inv-500)',
    )
    expect(screen.queryByRole('heading', { name: 'Page not found' })).toBeNull()

    fireEvent.click(screen.getByRole('button', { name: 'Try again' }))
    expect(await screen.findByRole('heading', { name: 'Answer' })).toBeTruthy()
  })

  test('to a plan when the network is down shows an error, not the not-found page', async () => {
    renderAt(`/plans/${PLAN}`, { ...base })

    expect((await screen.findByRole('alert')).textContent).toContain('The plan could not be loaded.')
    expect(screen.queryByRole('heading', { name: 'Page not found' })).toBeNull()
    expect(screen.getByRole('button', { name: 'Try again' })).toBeTruthy()
  })

  test('to a plan the API fails to return shows the reference', async () => {
    renderAt(`/plans/${PLAN}`, {
      ...base,
      [`GET /api/v1/plans/${PLAN}`]: () => json({ detail: 'The stored plan is of a schema version this server cannot read.', code: 'plan_unreadable' }, 500, { 'X-Correlation-ID': 'ref-plan-500' }),
    })

    expect((await screen.findByRole('alert')).textContent).toBe(
      'The plan could not be loaded. The stored plan is of a schema version this server cannot read. (Reference: ref-plan-500)',
    )
    expect(screen.queryByRole('heading', { name: 'Page not found' })).toBeNull()
  })
})
