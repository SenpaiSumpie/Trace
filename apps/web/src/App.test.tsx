import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import type { Investigation, InvestigationEvent, Repository } from './api.ts'
import App from './App.tsx'

const repository: Repository = {
  id: 'repo-1',
  owner: 'trace-demo',
  name: 'sample-repo',
  defaultBranch: 'main',
  latestSnapshot: {
    id: 'snap-1',
    commitSha: 'ee02b2da366aa000b30541463014b895d5cebb67',
    branch: 'main',
    completedAt: '2026-11-20T12:00:00Z',
  },
}

const answered: Investigation = {
  investigationId: 'inv-1',
  jobId: 'job-1',
  repositoryId: 'repo-1',
  snapshotId: 'snap-1',
  commitSha: repository.latestSnapshot!.commitSha,
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
      {
        source: 'code',
        path: 'src/TaskBoard.Api/Tasks/TaskRepository.cs',
        startLine: 24,
        endLine: 25,
        commitSha: repository.latestSnapshot!.commitSha,
        supports: 'Shows the skip calculation.',
        excerpt: '            // Pages are 1-based.\n            var skip = request.Page * request.PageSize;',
      },
    ],
    unknowns: ['Runtime behaviour was not observed.'],
    limitations: [],
  },
}

const events: InvestigationEvent[] = [
  { seq: 1, eventType: 'started', toolName: null, summary: 'Started investigating at commit ee02b2da366a.', occurredAt: '' },
  { seq: 2, eventType: 'tool_called', toolName: 'search_code', summary: 'Searched the code for "page": 5 result(s).', occurredAt: '' },
  { seq: 3, eventType: 'completed', toolName: null, summary: 'Answered with 1 validated evidence reference(s).', occurredAt: '' },
]

type Route = (init?: RequestInit) => Response | Promise<Response>

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
}

function renderApp(routes: Record<string, Route>) {
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

async function ask(question: string) {
  fireEvent.change(await screen.findByLabelText('Question'), { target: { value: question } })
  fireEvent.click(screen.getByRole('button', { name: 'Ask' }))
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

test('asks the selected repository at its latest snapshot and shows the answer, evidence and steps', async () => {
  const fetchMock = renderApp({
    'GET /api/v1/repositories': () => json([repository]),
    'POST /api/v1/investigations': () => json(answered, 201),
    'GET /api/v1/investigations/inv-1/events': () => json(events),
    'GET /health': () => new Response('Healthy'),
  })

  expect(await screen.findByRole('option', { name: 'trace-demo/sample-repo at ee02b2da366a' })).toBeTruthy()
  await ask('  Why does page one skip results?  ')

  expect(await screen.findByRole('heading', { name: 'Answer' })).toBeTruthy()
  expect(screen.getByText('List skips a full page before the first one.')).toBeTruthy()

  const passage = screen.getByRole('figure')
  expect(within(passage).getByText('src/TaskBoard.Api/Tasks/TaskRepository.cs, lines 24–25')).toBeTruthy()
  expect(within(passage).getByText('24')).toBeTruthy()
  expect(within(passage).getByText('25')).toBeTruthy()
  expect(within(passage).getByText('var skip = request.Page * request.PageSize;', { exact: false })).toBeTruthy()

  expect(screen.getByText('Runtime behaviour was not observed.')).toBeTruthy()
  expect(await screen.findByText('Searched the code for "page": 5 result(s).')).toBeTruthy()
  expect(screen.getByText('fake-extractive-v1')).toBeTruthy()

  const post = fetchMock.mock.calls.find(([, init]) => init?.method === 'POST')!
  const body = JSON.parse(post[1]!.body as string)
  expect(body).toMatchObject({
    repositoryId: 'repo-1',
    snapshotId: 'snap-1',
    type: 'question',
    question: 'Why does page one skip results?',
  })
  expect(body.idempotencyKey).toMatch(/^[0-9a-f-]{36}$/)
})

test('waits for a queued investigation, showing its steps as it runs, then shows the result', async () => {
  const queued: Investigation = { ...answered, state: 'queued', result: null, modelId: null, promptVersion: null }
  let polls = 0
  renderApp({
    'GET /api/v1/repositories': () => json([repository]),
    'POST /api/v1/investigations': () => json(queued, 202),
    'GET /api/v1/investigations/inv-1': () => json(++polls === 1 ? { ...queued, state: 'running' } : answered),
    'GET /api/v1/investigations/inv-1/events': () => json(events.slice(0, polls === 0 ? 0 : polls === 1 ? 2 : 3)),
  })

  await ask('Why does page one skip results?')

  expect(await screen.findByText('Waiting for a worker…')).toBeTruthy()
  expect(screen.getByRole('button', { name: 'Cancel' })).toBeTruthy()
  expect((screen.getByRole('button', { name: 'Ask' }) as HTMLButtonElement).disabled).toBe(true)
  expect(screen.queryByRole('heading', { name: 'Answer' })).toBeNull()

  expect(await screen.findByText('Looking through the code…', undefined, { timeout: 3000 })).toBeTruthy()
  expect(await screen.findByText('Searched the code for "page": 5 result(s).', undefined, { timeout: 3000 })).toBeTruthy()

  expect(await screen.findByRole('heading', { name: 'Answer' }, { timeout: 3000 })).toBeTruthy()
  expect(screen.queryByRole('button', { name: 'Cancel' })).toBeNull()
  expect(await screen.findByText('Answered with 1 validated evidence reference(s).')).toBeTruthy()
})

test('cancels a running investigation through its job', async () => {
  const running: Investigation = { ...answered, state: 'running', result: null }
  let cancelled = false
  const fetchMock = renderApp({
    'GET /api/v1/repositories': () => json([repository]),
    'POST /api/v1/investigations': () => json(running, 202),
    'POST /api/v1/jobs/job-1/cancel': () => {
      cancelled = true
      return json({ id: 'job-1', status: 'running', cancelRequested: true }, 202)
    },
    'GET /api/v1/investigations/inv-1': () => json(cancelled ? { ...running, state: 'cancelled' } : running),
    'GET /api/v1/investigations/inv-1/events': () => json(events.slice(0, 1)),
  })

  await ask('Why does page one skip results?')
  fireEvent.click(await screen.findByRole('button', { name: 'Cancel' }))

  expect(await screen.findByRole('heading', { name: 'The investigation was cancelled' }, { timeout: 3000 })).toBeTruthy()
  expect(fetchMock.mock.calls.some(([input, init]) => String(input) === '/api/v1/jobs/job-1/cancel' && init?.method === 'POST')).toBe(true)
  expect((screen.getByRole('button', { name: 'Ask' }) as HTMLButtonElement).disabled).toBe(false)
})

test('shows the insufficient-evidence state without an evidence section', async () => {
  const insufficient: Investigation = {
    ...answered,
    result: {
      outcome: 'insufficient_evidence',
      answer: 'TRACE could not find enough evidence in the indexed snapshot to answer this question.',
      evidence: [],
      unknowns: [],
      limitations: ['The model reported that the retrieved source did not answer the question.'],
    },
  }
  renderApp({
    'GET /api/v1/repositories': () => json([repository]),
    'POST /api/v1/investigations': () => json(insufficient, 201),
    'GET /api/v1/investigations/inv-1/events': () => json([]),
  })

  await ask('How is data replicated?')

  expect(await screen.findByRole('heading', { name: 'Not enough evidence' })).toBeTruthy()
  expect(screen.getByText(/could not find enough evidence/)).toBeTruthy()
  expect(screen.getByText(/did not answer the question/)).toBeTruthy()
  expect(screen.queryByRole('heading', { name: 'Evidence' })).toBeNull()
  expect(screen.queryByRole('heading', { name: 'Answer' })).toBeNull()
})

test('explains a failed investigation instead of showing a result', async () => {
  renderApp({
    'GET /api/v1/repositories': () => json([repository]),
    'POST /api/v1/investigations': () =>
      json({ ...answered, state: 'failed', result: null, errorCode: 'invalid_model_output' }, 201),
    'GET /api/v1/investigations/inv-1/events': () => json([]),
  })

  await ask('Anything?')

  expect(await screen.findByRole('heading', { name: 'The investigation failed' })).toBeTruthy()
  expect(screen.getByText(/did not return a usable answer/)).toBeTruthy()
  expect(screen.queryByRole('heading', { name: 'Answer' })).toBeNull()
})

test('shows the validation message when the API rejects the question', async () => {
  renderApp({
    'GET /api/v1/repositories': () => json([repository]),
    'POST /api/v1/investigations': () =>
      json({ title: 'Validation failed', errors: { question: ['question must be 1 to 4000 characters.'] } }, 400),
  })

  await ask('?')

  expect((await screen.findByRole('alert')).textContent).toBe(
    'The question could not be asked. question must be 1 to 4000 characters.',
  )
})

test('renders source text as text, never as markup', async () => {
  const hostile: Investigation = {
    ...answered,
    result: {
      ...answered.result!,
      answer: '<img src=x onerror=alert(1)>',
      evidence: [{ ...answered.result!.evidence[0], excerpt: '<script>alert(1)</script>' }],
    },
  }
  renderApp({
    'GET /api/v1/repositories': () => json([repository]),
    'POST /api/v1/investigations': () => json(hostile, 201),
    'GET /api/v1/investigations/inv-1/events': () => json([]),
  })

  await ask('Anything?')

  expect(await screen.findByText('<img src=x onerror=alert(1)>')).toBeTruthy()
  expect(screen.getByText('<script>alert(1)</script>')).toBeTruthy()
  expect(document.querySelector('img, script')).toBeNull()
})

test('cannot ask an empty question', async () => {
  renderApp({ 'GET /api/v1/repositories': () => json([repository]) })

  const button = (await screen.findByRole('button', { name: 'Ask' })) as HTMLButtonElement
  expect(button.disabled).toBe(true)

  fireEvent.change(screen.getByLabelText('Question'), { target: { value: '   ' } })
  expect(button.disabled).toBe(true)
})

test('says so when the API cannot be reached', async () => {
  renderApp({})

  expect((await screen.findByRole('alert')).textContent).toContain('The TRACE API could not be reached.')
  expect(screen.queryByRole('button', { name: 'Ask' })).toBeNull()
})

test('says so when no repository has been indexed', async () => {
  renderApp({ 'GET /api/v1/repositories': () => json([{ ...repository, latestSnapshot: null }]) })

  expect(await screen.findByText(/No repository has been indexed yet/)).toBeTruthy()
  expect(screen.queryByRole('button', { name: 'Ask' })).toBeNull()
})

test('offers sign-in and blocks asking when the caller may not start work', async () => {
  renderApp({
    'GET /api/v1/repositories': () => json([repository]),
    'GET /api/v1/me': () => json({ user: null, signInAvailable: true, canStartWork: false }),
  })

  const link = (await screen.findByRole('link', { name: 'Sign in with GitHub' })) as HTMLAnchorElement
  expect(link.getAttribute('href')).toBe('/api/v1/auth/login')

  fireEvent.change(await screen.findByLabelText('Question'), { target: { value: 'Anything?' } })
  expect((screen.getByRole('button', { name: 'Ask' }) as HTMLButtonElement).disabled).toBe(true)
})

test('shows who is signed in', async () => {
  renderApp({
    'GET /api/v1/repositories': () => json([repository]),
    'GET /api/v1/me': () => json({ user: { login: 'alice' }, signInAvailable: true, canStartWork: true }),
  })

  expect(await screen.findByText(/Signed in as alice\./)).toBeTruthy()
  expect(screen.getByRole('button', { name: 'Sign out' })).toBeTruthy()
  expect(screen.queryByRole('link', { name: 'Sign in with GitHub' })).toBeNull()
})
