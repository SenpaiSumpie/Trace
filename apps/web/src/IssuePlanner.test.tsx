import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, test, vi } from 'vitest'
import type { InvestigationEvent, Issue, IssueSummary, Plan, Repository } from './api.ts'
import App from './App.tsx'
import { PlanView } from './PlanView.tsx'

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

function summary(number: number, title: string, extra: Partial<IssueSummary> = {}): IssueSummary {
  return {
    number,
    title,
    state: 'open',
    author: 'lee-fixture',
    labels: ['bug'],
    commentCount: 0,
    createdAt: '2026-09-10T08:30:00Z',
    updatedAt: '2026-09-10T08:30:00Z',
    ...extra,
  }
}

const issue: Issue = {
  ...summary(5, 'Health paths skip the key check', { labels: ['bug', 'security'], commentCount: 1 }),
  body: 'Any path that has /health in it is let through.\n\nOnly the exact probe should be exempt.',
  bodyTruncated: false,
  comments: [{ id: 501, author: 'sam-fixture', body: 'Seen on staging too.', bodyTruncated: false, createdAt: '2026-09-11T08:30:00Z' }],
  commentsTruncated: false,
}

const E1 = '0193a5c0-0000-7000-8000-000000000001'
const E2 = '0193a5c0-0000-7000-8000-000000000002'

const planned: Plan = {
  planId: 'plan-1',
  investigationId: 'inv-9',
  jobId: 'job-9',
  repositoryId: 'repo-1',
  snapshotId: 'snap-1',
  commitSha: repository.latestSnapshot!.commitSha,
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
    summary: { text: 'The key check lets any path containing /health through. Match the exact path.', citations: [E1] },
    steps: [
      { text: 'Add a failing test for a path that merely contains /health.', citations: [E2] },
      { text: 'Compare the path exactly in ApiKeyMiddleware.', citations: [E1, E2] },
    ],
    filesToChange: [
      { path: 'src/TaskBoard.Api/Auth/ApiKeyMiddleware.cs', change: 'modify', reason: 'This is where the path is checked.', citations: [E1] },
      { path: 'tests/TaskBoard.Tests/HealthPathTests.cs', change: 'create', reason: 'A file for the new tests.', citations: [] },
    ],
    risks: [{ text: 'A probe that uses another path would start to fail.', citations: [E1] }],
    verification: [{ text: 'Run the middleware tests.', citations: [E2] }],
    unknowns: [{ text: 'Whether any deployment probes a different path.', citations: [] }],
    evidence: [
      {
        id: E1,
        source: 'code',
        path: 'src/TaskBoard.Api/Auth/ApiKeyMiddleware.cs',
        startLine: 11,
        endLine: 12,
        commitSha: repository.latestSnapshot!.commitSha,
        excerpt: '        // The health probe cannot send a key.\n        if (context.Request.Path.Value!.Contains("/health"))',
      },
      {
        id: E2,
        source: 'code',
        path: 'tests/TaskBoard.Tests/ApiKeyTests.cs',
        startLine: 8,
        endLine: 8,
        commitSha: repository.latestSnapshot!.commitSha,
        excerpt: '    public void Health_needs_no_key()',
      },
    ],
  },
  trimmed: { citations: 0, steps: 0, files: 0, risks: 0, verification: 0, unknowns: 0, summaryReplaced: false },
  limitations: [],
  reviewedAt: null,
  issueCheck: null,
}

const queued: Plan = {
  ...planned,
  state: 'queued',
  issue: { number: 5, title: null },
  modelId: null,
  promptVersion: null,
  plan: null,
  trimmed: null,
}

const events: InvestigationEvent[] = [
  { seq: 1, eventType: 'started', toolName: null, summary: 'Started investigating at commit ee02b2da366a.', occurredAt: '' },
  { seq: 2, eventType: 'tool_called', toolName: 'search_code', summary: 'Searched the code for "health": 5 result(s).', occurredAt: '' },
  { seq: 3, eventType: 'completed', toolName: null, summary: 'Planned 2 step(s) resting on 2 validated evidence reference(s).', occurredAt: '' },
]

const ISSUES = 'GET /api/v1/repositories/repo-1/issues?limit=20'
const ISSUE_5 = 'GET /api/v1/repositories/repo-1/issues/5'
const START = 'POST /api/v1/repositories/repo-1/issues/5/plans'

type Route = (init?: RequestInit) => Response | Promise<Response>

function json(body: unknown, status = 200, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } })
}

function problem(status: number, code: string, detail: string, reference = 'ref-123'): Response {
  return json({ title: 'Problem', status, detail, code }, status, { 'X-Correlation-ID': reference })
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

/** The routes of a run that is already finished when the plan is asked for. */
function finished(plan: Plan, more: Record<string, Route> = {}): Record<string, Route> {
  return {
    'GET /api/v1/repositories': () => json([repository]),
    [ISSUES]: () => json({ items: [summary(5, issue.title)], nextCursor: null }),
    [ISSUE_5]: () => json(issue),
    [START]: () => json(plan, 202),
    'GET /api/v1/investigations/inv-9/events': () => json(events),
    ...more,
  }
}

async function openPlanner() {
  fireEvent.click(await screen.findByRole('button', { name: 'Plan from an issue' }))
}

async function openIssue(name = '#5 Health paths skip the key check') {
  await openPlanner()
  fireEvent.click(await screen.findByRole('button', { name }))
}

async function startPlan() {
  await openIssue()
  fireEvent.click(await screen.findByRole('button', { name: 'Start a plan' }))
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('issue list', () => {
  test('says it is loading, then lists the open issues of the repository', async () => {
    let answer: (response: Response) => void = () => {}
    renderApp({
      'GET /api/v1/repositories': () => json([repository]),
      [ISSUES]: () => new Promise<Response>((resolve) => (answer = resolve)),
    })

    await openPlanner()

    expect(await screen.findByRole('heading', { level: 2, name: 'Open issues' })).toBeTruthy()
    expect(await screen.findByText('Loading issues…')).toBeTruthy()
    // The question form is not on this screen.
    expect(screen.queryByRole('button', { name: 'Ask' })).toBeNull()

    answer(
      json({
        items: [
          summary(16, 'Allow a page size above 100', { author: 'robin-fixture', labels: ['enhancement'], commentCount: 1 }),
          summary(5, 'Health paths skip the key check', { author: null, labels: [], commentCount: 2 }),
        ],
        nextCursor: null,
      }),
    )

    const first = (await screen.findByRole('button', { name: '#16 Allow a page size above 100' })).closest('li')!
    expect(within(first).getByText('Opened by robin-fixture · 1 comment')).toBeTruthy()
    expect(within(first).getByText('enhancement')).toBeTruthy()
    const second = screen.getByRole('button', { name: '#5 Health paths skip the key check' }).closest('li')!
    expect(within(second).getByText('Opened by an unknown author · 2 comments')).toBeTruthy()
    expect(screen.queryByText('Loading issues…')).toBeNull()
    expect(screen.queryByRole('button', { name: 'Load more issues' })).toBeNull()
  })

  test('loads more with the cursor of the last page, even when a page came back short', async () => {
    const fetchMock = renderApp({
      'GET /api/v1/repositories': () => json([repository]),
      [ISSUES]: () => json({ items: [summary(16, 'Sixteen')], nextCursor: 'MToz' }),
      [`${ISSUES}&cursor=MToz`]: () => json({ items: [], nextCursor: 'Mjoz' }),
      [`${ISSUES}&cursor=Mjoz`]: () => json({ items: [summary(3, 'Three')], nextCursor: null }),
    })

    await openPlanner()
    expect(await screen.findByRole('button', { name: '#16 Sixteen' })).toBeTruthy()

    fireEvent.click(screen.getByRole('button', { name: 'Load more issues' }))
    await waitFor(() => expect(fetchMock.mock.calls.some(([input]) => String(input).endsWith('cursor=MToz'))).toBe(true))
    // The empty page still had a cursor: the control stays.
    fireEvent.click(await screen.findByRole('button', { name: 'Load more issues' }))

    expect(await screen.findByRole('button', { name: '#3 Three' })).toBeTruthy()
    expect(screen.getByRole('button', { name: '#16 Sixteen' })).toBeTruthy()
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Load more issues' })).toBeNull())
  })

  test('says so when the repository has no open issues', async () => {
    renderApp({
      'GET /api/v1/repositories': () => json([repository]),
      [ISSUES]: () => json({ items: [], nextCursor: null }),
    })

    await openPlanner()

    expect(await screen.findByText('This repository has no open issues.')).toBeTruthy()
    expect(screen.queryByRole('button', { name: 'Load more issues' })).toBeNull()
  })

  test('keeps the issues already shown when loading more fails', async () => {
    renderApp({
      'GET /api/v1/repositories': () => json([repository]),
      [ISSUES]: () => json({ items: [summary(16, 'Sixteen')], nextCursor: 'MToz' }),
      [`${ISSUES}&cursor=MToz`]: () => problem(503, 'github_rate_limited', 'GitHub is limiting requests right now. Try again later.'),
    })

    await openPlanner()
    fireEvent.click(await screen.findByRole('button', { name: 'Load more issues' }))

    expect((await screen.findByRole('alert')).textContent).toBe(
      'GitHub is limiting requests right now. Wait a few minutes, then try again. (Reference: ref-123)',
    )
    expect(screen.getByRole('button', { name: '#16 Sixteen' })).toBeTruthy()
  })

  test.each([
    [409, 'issues_permission_missing', /needs the read-only Issues permission.*docs\/github-app-setup\.md.*\(Reference: ref-123\)$/],
    [409, 'issues_disabled', /^Issues are switched off for this repository on GitHub.*\(Reference: ref-123\)$/],
    [409, 'issues_unavailable', /^This repository has no source of issues.*\(Reference: ref-123\)$/],
    [503, 'github_rate_limited', /^GitHub is limiting requests right now.*\(Reference: ref-123\)$/],
    [502, 'github_unavailable', /^GitHub did not return the issues\. Try again\. \(Reference: ref-123\)$/],
  ])('explains %i %s in plain language, with the reference', async (status, code, expected) => {
    renderApp({
      'GET /api/v1/repositories': () => json([repository]),
      [ISSUES]: () => problem(status, code, 'server wording that the page replaces'),
    })

    await openPlanner()

    const alert = await screen.findByRole('alert')
    expect(alert.textContent).toMatch(expected)
    expect(alert.textContent).not.toContain(code)
    expect(alert.textContent).not.toContain('server wording')
    expect(screen.queryByRole('list', { name: 'Labels' })).toBeNull()
  })

  test('shows the API message and reference for an error it has no wording for', async () => {
    renderApp({
      'GET /api/v1/repositories': () => json([repository]),
      [ISSUES]: () => problem(400, 'invalid_cursor', 'The cursor is not one this API returned.'),
    })

    await openPlanner()

    expect((await screen.findByRole('alert')).textContent).toBe(
      'The issues could not be loaded. The cursor is not one this API returned. (Reference: ref-123)',
    )
  })
})

describe('one issue', () => {
  test('shows the title, author, labels, body and comments as text before a plan is started', async () => {
    const fetchMock = renderApp(finished(planned))

    await openIssue()

    const heading = await screen.findByRole('heading', { level: 2, name: 'Issue #5' })
    // The button that was pressed is gone; the keyboard is put at the new screen's heading.
    expect(document.activeElement).toBe(heading)
    expect(await screen.findByText('Health paths skip the key check')).toBeTruthy()
    expect(screen.getByText('trace-demo/sample-repo · Open · opened by lee-fixture · 1 comment')).toBeTruthy()
    const labels = screen.getByRole('list', { name: 'Labels' })
    expect(within(labels).getAllByRole('listitem').map((item) => item.textContent)).toEqual(['bug', 'security'])
    expect(screen.getByText(/Any path that has \/health in it is let through\./).textContent).toBe(issue.body)
    expect(screen.getByText('sam-fixture wrote:')).toBeTruthy()
    expect(screen.getByText('Seen on staging too.')).toBeTruthy()
    expect(screen.queryByText(/has been cut off/)).toBeNull()
    expect(screen.getByText(/indexed at commit/).textContent).toContain('ee02b2da366a')
    expect((screen.getByRole('button', { name: 'Start a plan' }) as HTMLButtonElement).disabled).toBe(false)
    // Reading an issue starts nothing.
    expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'POST')).toBe(false)

    fireEvent.click(screen.getByRole('button', { name: 'Back to issues' }))
    expect(await screen.findByRole('heading', { level: 2, name: 'Open issues' })).toBeTruthy()
  })

  test('says where the body and the comments were cut', async () => {
    renderApp(
      finished(planned, {
        [ISSUE_5]: () =>
          json({
            ...issue,
            bodyTruncated: true,
            commentCount: 40,
            commentsTruncated: true,
            comments: [{ ...issue.comments[0], bodyTruncated: true }],
          }),
      }),
    )

    await openIssue()

    expect(await screen.findByText('The description is longer than TRACE reads. It has been cut off here.')).toBeTruthy()
    expect(screen.getByText('This comment is longer than TRACE reads. It has been cut off here.')).toBeTruthy()
    expect(screen.getByText(/Only the first 1 of 40 comments are read\./)).toBeTruthy()
  })

  test('says so when the issue has no description', async () => {
    renderApp(finished(planned, { [ISSUE_5]: () => json({ ...issue, body: '', comments: [], commentCount: 0 }) }))

    await openIssue()

    expect(await screen.findByText('The issue has no description.')).toBeTruthy()
    expect(screen.queryByRole('heading', { name: 'Comments' })).toBeNull()
  })

  test('explains an issue that cannot be loaded and offers the way back', async () => {
    renderApp(finished(planned, { [ISSUE_5]: () => problem(404, 'issue_not_found', 'server wording') }))

    await openIssue()

    expect((await screen.findByRole('alert')).textContent).toBe(
      'This repository has no issue with that number. It may have been deleted, or it is a pull request. (Reference: ref-123)',
    )
    expect(screen.queryByRole('button', { name: 'Start a plan' })).toBeNull()
    expect(screen.getByRole('button', { name: 'Back to issues' })).toBeTruthy()
  })

  test('a repository that is not indexed can show its issues but cannot start a plan', async () => {
    const fetchMock = renderApp({
      ...finished(planned),
      'GET /api/v1/repositories': () => json([{ ...repository, latestSnapshot: null }]),
    })

    await openIssue()

    expect(await screen.findByText(/This repository is not indexed yet, so a plan cannot be made\. Use Index under Repositories below/)).toBeTruthy()
    const start = screen.getByRole('button', { name: 'Start a plan' }) as HTMLButtonElement
    expect(start.disabled).toBe(true)
    fireEvent.click(start)
    expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'POST')).toBe(false)
    expect(screen.getByRole('button', { name: 'Index trace-demo/sample-repo' })).toBeTruthy()
  })

  test('a caller who may not start work cannot start a plan', async () => {
    renderApp({
      ...finished(planned),
      'GET /api/v1/me': () => json({ user: null, signInAvailable: true, canStartWork: false }),
    })

    await screen.findByRole('link', { name: 'Sign in with GitHub' })
    await openIssue()

    expect(await screen.findByText('Sign in to start a plan.')).toBeTruthy()
    expect((screen.getByRole('button', { name: 'Start a plan' }) as HTMLButtonElement).disabled).toBe(true)
  })

  test('shows why a plan could not be started, with the reference, and stays on the issue', async () => {
    renderApp(finished(planned, { [START]: () => json({ detail: 'Too many requests. Try again shortly.' }, 429, { 'X-Correlation-ID': 'ref-429' }) }))

    await startPlan()

    expect((await screen.findByRole('alert')).textContent).toBe(
      'The plan could not be started. Too many requests. Try again shortly. (Reference: ref-429)',
    )
    expect(screen.getByRole('heading', { level: 2, name: 'Issue #5' })).toBeTruthy()
    expect((screen.getByRole('button', { name: 'Start a plan' }) as HTMLButtonElement).disabled).toBe(false)
  })
})

describe('a plan run', () => {
  test('waits for a queued run, shows its steps as it runs, then shows the plan', async () => {
    let polls = 0
    const fetchMock = renderApp(
      finished(queued, {
        'GET /api/v1/plans/plan-1': () => json(++polls === 1 ? { ...queued, state: 'running', modelId: 'fake-extractive-v1' } : planned),
        'GET /api/v1/investigations/inv-9/events': () => json(events.slice(0, polls === 0 ? 0 : polls === 1 ? 2 : 3)),
      }),
    )

    await startPlan()

    const heading = await screen.findByRole('heading', { level: 2, name: 'Plan for issue #5' })
    expect(document.activeElement).toBe(heading)
    expect(await screen.findByText('Waiting for a worker…')).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeTruthy()
    expect(screen.queryByRole('heading', { name: 'Summary' })).toBeNull()

    expect(await screen.findByText('Reading the issue and the code…', undefined, { timeout: 3000 })).toBeTruthy()
    expect(await screen.findByText('Searched the code for "health": 5 result(s).', undefined, { timeout: 3000 })).toBeTruthy()

    expect(await screen.findByRole('heading', { name: 'Summary' }, { timeout: 3000 })).toBeTruthy()
    expect(screen.getByText('The plan is ready.').getAttribute('role')).toBe('status')
    expect(screen.queryByRole('button', { name: 'Cancel' })).toBeNull()
    expect(await screen.findByText('Planned 2 step(s) resting on 2 validated evidence reference(s).')).toBeTruthy()

    const post = fetchMock.mock.calls.find(([, init]) => init?.method === 'POST')!
    expect(String(post[0])).toBe('/api/v1/repositories/repo-1/issues/5/plans')
    const body = JSON.parse(post[1]!.body as string)
    expect(body.snapshotId).toBe('snap-1')
    expect(body.idempotencyKey).toMatch(/^[0-9a-f-]{36}$/)
    expect(Object.keys(body).sort()).toEqual(['idempotencyKey', 'snapshotId'])
  })

  test('cancels a running plan through its job', async () => {
    const running: Plan = { ...queued, state: 'running' }
    let cancelled = false
    const fetchMock = renderApp(
      finished(running, {
        'POST /api/v1/jobs/job-9/cancel': () => {
          cancelled = true
          return json({ id: 'job-9', status: 'running', cancelRequested: true }, 202)
        },
        'GET /api/v1/plans/plan-1': () => json(cancelled ? { ...running, state: 'cancelled' } : running),
        'GET /api/v1/investigations/inv-9/events': () => json(events.slice(0, 1)),
      }),
    )

    await startPlan()
    fireEvent.click(await screen.findByRole('button', { name: 'Cancel' }))

    expect(await screen.findByRole('heading', { name: 'The plan was cancelled' }, { timeout: 3000 })).toBeTruthy()
    expect(screen.getByText('The run was cancelled.').getAttribute('role')).toBe('status')
    expect(fetchMock.mock.calls.some(([input, init]) => String(input) === '/api/v1/jobs/job-9/cancel' && init?.method === 'POST')).toBe(true)
    expect(screen.queryByRole('heading', { name: 'Summary' })).toBeNull()
    expect(screen.queryByRole('button', { name: 'Mark as reviewed' })).toBeNull()
    expect(screen.queryByRole('button', { name: 'Download as Markdown' })).toBeNull()

    // Back at the issue, a new plan can be started.
    fireEvent.click(screen.getByRole('button', { name: 'Back to the issue' }))
    expect((await screen.findByRole('button', { name: 'Start a plan' }) as HTMLButtonElement).disabled).toBe(false)
  })

  test.each([
    ['issue_not_found', 'The issue no longer exists, or it is a pull request.'],
    ['issues_permission_missing', 'The GitHub App is not allowed to read this repository’s issues.'],
    ['issues_disabled', 'Issues are switched off for this repository on GitHub.'],
    ['issues_unavailable', 'This repository has no source of issues.'],
    ['github_rate_limited', 'GitHub was limiting requests.'],
    ['github_unavailable', 'GitHub did not return the issue.'],
    ['invalid_model_output', 'The model did not return a usable plan, so nothing is shown.'],
    ['budget_exhausted', 'The spending limit for model calls was reached.'],
    ['model_error', 'The language model could not be reached.'],
    ['model_refused', 'The language model declined to answer.'],
    ['worker_interrupted', 'The worker stopped repeatedly before it could finish.'],
    ['timed_out', 'It took too long.'],
    ['internal_error', 'Something went wrong inside TRACE.'],
    ['<b>some_new_code</b>', 'The run stopped before producing a plan.'],
    ['constructor', 'The run stopped before producing a plan.'],
    ['__proto__', 'The run stopped before producing a plan.'],
  ])('a run that failed with %s gets a plain message and never the raw name', async (errorCode, message) => {
    renderApp(finished({ ...queued, state: 'failed', errorCode }))

    await startPlan()

    expect(await screen.findByRole('heading', { name: 'The plan could not be made' })).toBeTruthy()
    const alert = screen.getByRole('alert')
    expect(alert.textContent).toBe(`${message} Nothing was stored as a plan. Go back to the issue to try again.`)
    expect(document.body.textContent).not.toContain(errorCode === 'constructor' ? '[native code]' : errorCode)
    expect(screen.getByText('The run failed.').getAttribute('role')).toBe('status')
    expect(screen.queryByRole('heading', { name: 'Summary' })).toBeNull()
    expect(screen.queryByRole('button', { name: 'Mark as reviewed' })).toBeNull()
  })

  test('insufficient evidence has its own screen, not an empty plan', async () => {
    const insufficient: Plan = {
      ...planned,
      state: 'insufficient_evidence',
      plan: {
        ...planned.plan!,
        summary: { text: 'TRACE could not find enough evidence in the indexed snapshot to plan this change.', citations: [] },
        steps: [],
        filesToChange: [],
        risks: [],
        verification: [],
        unknowns: [{ text: 'Where reports are generated.', citations: [] }],
        evidence: [],
      },
      trimmed: { citations: 3, steps: 2, files: 1, risks: 0, verification: 0, unknowns: 0, summaryReplaced: true },
      limitations: ['No step of the proposal had a citation that could be verified.'],
    }
    renderApp(finished(insufficient))

    await startPlan()

    expect(await screen.findByRole('heading', { name: 'Not enough evidence for a plan' })).toBeTruthy()
    expect(screen.getByText('The run finished without enough evidence for a plan.').getAttribute('role')).toBe('status')
    expect(screen.getByText(/could not find enough evidence in the indexed snapshot/)).toBeTruthy()
    expect(screen.getByText('Where reports are generated.')).toBeTruthy()
    expect(screen.getByText('Not backed by evidence')).toBeTruthy()
    expect(screen.getByText('No step of the proposal had a citation that could be verified.')).toBeTruthy()
    expect(screen.getByText('2 steps')).toBeTruthy()
    expect(screen.getByText('1 file')).toBeTruthy()
    expect(screen.getByText('3 cited locations that failed the check')).toBeTruthy()
    expect(screen.getByText(/summary was not kept/)).toBeTruthy()

    for (const name of ['Summary', 'Steps', 'Files to change', 'Risks', 'How to verify', 'Evidence', 'Review and export']) {
      expect(screen.queryByRole('heading', { name })).toBeNull()
    }
    expect(screen.queryByRole('figure')).toBeNull()
    // Nothing to acknowledge, but the record of the outcome can still be saved.
    expect(screen.queryByRole('button', { name: 'Mark as reviewed' })).toBeNull()
    expect(screen.getByRole('button', { name: 'Download as Markdown' })).toBeTruthy()
  })
})

describe('the plan', () => {
  test('shows every part of the plan, with real source lines behind each citation', async () => {
    renderApp(finished(planned))

    await startPlan()

    expect(await screen.findByRole('heading', { level: 3, name: 'Summary' })).toBeTruthy()
    expect(screen.getByText('trace-demo/sample-repo · Health paths skip the key check')).toBeTruthy()
    expect(screen.getByText(/Match the exact path\./)).toBeTruthy()

    const steps = screen.getByRole('heading', { name: 'Steps' }).nextElementSibling!
    expect(steps.tagName).toBe('OL')
    expect([...steps.children].map((step) => step.querySelector('.statement')!.textContent)).toEqual([
      'Add a failing test for a path that merely contains /health.',
      'Compare the path exactly in ApiKeyMiddleware.',
    ])

    const files = screen.getByRole('heading', { name: 'Files to change' }).nextElementSibling!
    const [modified, created] = [...files.children] as HTMLElement[]
    expect(within(modified).getByText('src/TaskBoard.Api/Auth/ApiKeyMiddleware.cs')).toBeTruthy()
    expect(within(modified).getByText('Change')).toBeTruthy()
    expect(within(modified).getByText('This is where the path is checked.')).toBeTruthy()
    expect(within(created).getByText('tests/TaskBoard.Tests/HealthPathTests.cs')).toBeTruthy()
    expect(within(created).getByText('New file')).toBeTruthy()
    // A new file has no lines to cite: its reason is marked as the model's own statement.
    expect(within(created).getByText('Not backed by evidence')).toBeTruthy()
    expect(within(modified).queryByText('Not backed by evidence')).toBeNull()

    expect(screen.getByText('A probe that uses another path would start to fail.')).toBeTruthy()
    expect(screen.getByText('Run the middleware tests.')).toBeTruthy()

    const unknowns = screen.getByRole('heading', { name: 'Not established' }).nextElementSibling!.nextElementSibling as HTMLElement
    expect(within(unknowns).getByText('Whether any deployment probes a different path.')).toBeTruthy()
    expect(within(unknowns).getByText('Not backed by evidence')).toBeTruthy()
    // Exactly the two uncited entries are marked: the unknown and the new file.
    expect(screen.getAllByText('Not backed by evidence')).toHaveLength(2)

    expect(screen.getByRole('heading', { name: 'Removed by validation' })).toBeTruthy()
    expect(screen.getByText(/Nothing: every entry of the model’s proposal passed the check\./)).toBeTruthy()

    // All cited passages are listed once, with their labels and real line numbers.
    const evidence = screen.getByRole('heading', { name: 'Evidence' }).parentElement!.querySelector(':scope > ol.evidence-list') as HTMLElement
    const passages = within(evidence).getAllByRole('figure')
    expect(passages).toHaveLength(2)
    expect(within(passages[0]).getByText(/src\/TaskBoard\.Api\/Auth\/ApiKeyMiddleware\.cs, lines 11–12/).textContent).toBe(
      'E1 src/TaskBoard.Api/Auth/ApiKeyMiddleware.cs, lines 11–12',
    )
    expect(within(passages[0]).getByText('11')).toBeTruthy()
    expect(within(passages[0]).getByText('12')).toBeTruthy()
    expect(within(passages[0]).getByText('if (context.Request.Path.Value!.Contains("/health"))', { exact: false })).toBeTruthy()

    // A citation opens the cited lines in place, and closes again.
    const secondStep = steps.children[1] as HTMLElement
    const toggle = within(secondStep).getByRole('button', { name: 'Show evidence E1, E2' })
    expect(toggle.getAttribute('aria-expanded')).toBe('false')
    expect(within(secondStep).queryByRole('figure')).toBeNull()
    fireEvent.click(toggle)
    expect(toggle.getAttribute('aria-expanded')).toBe('true')
    expect(toggle.textContent).toBe('Hide evidence E1, E2')
    expect(within(secondStep).getAllByRole('figure')).toHaveLength(2)
    expect(within(secondStep).getByText('public void Health_needs_no_key()', { exact: false })).toBeTruthy()
    expect(document.getElementById(toggle.getAttribute('aria-controls')!)!.contains(within(secondStep).getAllByRole('figure')[0])).toBe(true)
    fireEvent.click(toggle)
    expect(within(secondStep).queryByRole('figure')).toBeNull()

    expect(screen.getByText('ee02b2da366a')).toBeTruthy()
    expect(screen.getByText('fake-extractive-v1')).toBeTruthy()
    expect(screen.getByText('plan-prompt-v1')).toBeTruthy()
    expect(await screen.findByText('Searched the code for "health": 5 result(s).')).toBeTruthy()
  })

  test('counts what validation removed', async () => {
    renderApp(
      finished({
        ...planned,
        trimmed: { citations: 4, steps: 1, files: 2, risks: 1, verification: 3, unknowns: 1, summaryReplaced: true },
        limitations: ['The model’s summary cited nothing that could be verified.'],
      }),
    )

    await startPlan()

    const list = (await screen.findByRole('heading', { name: 'Removed by validation' })).parentElement!.querySelector('ul.trimmed') as HTMLElement
    expect(within(list).getAllByRole('listitem').map((item) => item.textContent)).toEqual([
      '1 step',
      '2 files',
      '1 risk',
      '3 verification entries',
      '1 unknown',
      '4 cited locations that failed the check',
      'The model’s summary was not kept; a fixed sentence stands in its place.',
    ])
    expect(screen.getByText('The model’s summary cited nothing that could be verified.')).toBeTruthy()
  })

  test('marks a summary without citations as not backed by evidence', async () => {
    renderApp(finished({ ...planned, plan: { ...planned.plan!, summary: { text: 'A fixed sentence.', citations: [] } } }))

    await startPlan()

    const summary = await screen.findByText('A fixed sentence.')
    expect(summary.nextElementSibling!.textContent).toBe('Not backed by evidence')
  })

  test('marks the plan as reviewed with one click and shows when', async () => {
    const fetchMock = renderApp(
      finished(planned, {
        'POST /api/v1/plans/plan-1/review': () => json({ ...planned, reviewedAt: '2026-11-21T09:30:00Z' }),
      }),
    )

    await startPlan()

    expect(await screen.findByText(/It is not an approval, and it\s+changes nothing on GitHub\./)).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Mark as reviewed' }))

    const status = await screen.findByText(/Marked as reviewed on/)
    expect(status.getAttribute('role')).toBe('status')
    expect(status.textContent).toMatch(/It is not an approval, and nothing changed on\s+GitHub\./)
    expect(status.querySelector('time')!.getAttribute('datetime')).toBe('2026-11-21T09:30:00Z')
    expect(status.querySelector('time')!.textContent).toBe(new Date('2026-11-21T09:30:00Z').toLocaleString())
    expect(screen.queryByRole('button', { name: 'Mark as reviewed' })).toBeNull()
    expect(fetchMock.mock.calls.filter(([input]) => String(input) === '/api/v1/plans/plan-1/review')).toHaveLength(1)
  })

  test('a plan that was already reviewed shows when and offers no second mark', async () => {
    renderApp(finished({ ...planned, reviewedAt: '2026-11-21T09:30:00Z' }))

    await startPlan()

    expect(await screen.findByText(/Marked as reviewed on/)).toBeTruthy()
    expect(screen.queryByRole('button', { name: 'Mark as reviewed' })).toBeNull()
  })

  test('a refused review is explained and the plan is read again', async () => {
    let reads = 0
    renderApp(
      finished(planned, {
        'POST /api/v1/plans/plan-1/review': () => problem(409, 'plan_not_reviewable', 'Only a finished plan can be marked as reviewed.', 'ref-409'),
        'GET /api/v1/plans/plan-1': () => {
          reads++
          return json(planned)
        },
      }),
    )

    await startPlan()
    fireEvent.click(await screen.findByRole('button', { name: 'Mark as reviewed' }))

    expect((await screen.findByRole('alert')).textContent).toBe(
      'This plan cannot be marked as reviewed in its current state. Only a finished plan can be marked as reviewed. (Reference: ref-409)',
    )
    await waitFor(() => expect(reads).toBe(1))
    expect(screen.queryByText(/Marked as reviewed on/)).toBeNull()
    // The plan is still on the page.
    expect(screen.getByRole('heading', { name: 'Summary' })).toBeTruthy()
  })

  test('a caller who may not start work cannot mark a plan as reviewed', async () => {
    const fetchMock = vi.fn(() => Promise.resolve(json(events)))
    vi.stubGlobal('fetch', fetchMock)
    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <PlanView plan={planned} mayReview={false} />
      </QueryClientProvider>,
    )

    const button = (await screen.findByRole('button', { name: 'Mark as reviewed' })) as HTMLButtonElement
    expect(button.disabled).toBe(true)
    expect(screen.getByText('Sign in to mark a plan as reviewed.')).toBeTruthy()
    fireEvent.click(button)
    expect(fetchMock.mock.calls.some(([input]: unknown[]) => String(input).includes('/review'))).toBe(false)
  })

  test('downloads the Markdown export as a file named by the server', async () => {
    const saved: { name: string; address: string }[] = []
    const blobs: Blob[] = []
    URL.createObjectURL = vi.fn((blob: Blob) => {
      blobs.push(blob)
      return 'blob:trace-test'
    })
    URL.revokeObjectURL = vi.fn()
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      saved.push({ name: this.download, address: this.getAttribute('href')! })
    })
    renderApp(
      finished(planned, {
        'GET /api/v1/plans/plan-1/export': () =>
          new Response('# Implementation plan for issue 5\n', {
            headers: { 'Content-Type': 'text/markdown; charset=utf-8', 'Content-Disposition': 'attachment; filename=trace-plan-issue-5-0193a5c0.md' },
          }),
      }),
    )

    await startPlan()
    fireEvent.click(await screen.findByRole('button', { name: 'Download as Markdown' }))

    expect((await screen.findByText('Saved as trace-plan-issue-5-0193a5c0.md.')).getAttribute('role')).toBe('status')
    expect(saved).toEqual([{ name: 'trace-plan-issue-5-0193a5c0.md', address: 'blob:trace-test' }])
    expect(blobs).toHaveLength(1)
    expect(blobs[0].type).toBe('text/markdown')
    expect(blobs[0].size).toBe('# Implementation plan for issue 5\n'.length)
    // The export is saved, never rendered: no link to it stays on the page.
    expect(document.querySelector('a')).toBeNull()
  })

  test('does not take a file name that is not a plain .md name', async () => {
    const saved: string[] = []
    URL.createObjectURL = vi.fn(() => 'blob:trace-test')
    URL.revokeObjectURL = vi.fn()
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      saved.push(this.download)
    })
    renderApp(
      finished(planned, {
        'GET /api/v1/plans/plan-1/export': () =>
          new Response('# Plan\n', { headers: { 'Content-Disposition': 'attachment; filename="../../evil.exe"' } }),
      }),
    )

    await startPlan()
    fireEvent.click(await screen.findByRole('button', { name: 'Download as Markdown' }))

    expect(await screen.findByText('Saved as trace-plan-plan-1.md.')).toBeTruthy()
    expect(saved).toEqual(['trace-plan-plan-1.md'])
  })

  test('says so when the export is refused, with the reference', async () => {
    renderApp(
      finished(planned, {
        'GET /api/v1/plans/plan-1/export': () => problem(409, 'plan_not_finished', 'This plan has no document to export: it is unfinished, failed or cancelled.', 'ref-exp'),
      }),
    )

    await startPlan()
    fireEvent.click(await screen.findByRole('button', { name: 'Download as Markdown' }))

    expect((await screen.findByRole('alert')).textContent).toBe(
      'The plan could not be downloaded. This plan has no document to export: it is unfinished, failed or cancelled. (Reference: ref-exp)',
    )
  })

  test.each([
    ['unchanged', null, /are the same as when this plan was made\./],
    ['changed', null, /The issue has changed since this plan was made/],
    ['unavailable', 'github_unavailable', /The issue could not be compared right now\./],
    ['<i>new_status</i>', null, /The issue could not be compared right now\./],
    ['toString', null, /The issue could not be compared right now\./],
  ])('checks on request whether the issue changed: %s', async (status, code, expected) => {
    const fetchMock = renderApp(
      finished(planned, {
        'GET /api/v1/plans/plan-1?checkIssue=true': () => json({ ...planned, issueCheck: { status, code } }),
      }),
    )

    await startPlan()
    await screen.findByRole('heading', { name: 'Summary' })
    // Reading the issue again costs calls to GitHub: it is never done unasked.
    expect(fetchMock.mock.calls.some(([input]) => String(input).includes('checkIssue'))).toBe(false)

    fireEvent.click(screen.getByRole('button', { name: 'Check whether the issue changed' }))

    const result = await screen.findByText(expected)
    expect(result.getAttribute('role')).toBe('status')
    expect(document.body.textContent).not.toContain(status.includes('<') ? status : 'github_unavailable')
    expect(document.body.textContent).not.toContain('[native code]')
  })
})

test('a question in progress and a plan in progress both survive switching between the two', async () => {
  renderApp({
    ...finished(planned),
    'POST /api/v1/investigations': () =>
      json(
        {
          investigationId: 'inv-1', jobId: 'job-1', repositoryId: 'repo-1', snapshotId: 'snap-1', commitSha: planned.commitSha,
          type: 'question', question: 'Why?', state: 'completed', modelId: 'fake-extractive-v1', promptVersion: 'question-v1',
          createdAt: '', updatedAt: '', errorCode: null,
          result: { outcome: 'insufficient_evidence', answer: 'Nothing found.', evidence: [], unknowns: [], limitations: [] },
        },
        201,
      ),
    'GET /api/v1/investigations/inv-1/events': () => json([]),
  })

  fireEvent.change(await screen.findByLabelText('Question'), { target: { value: 'Why?' } })
  fireEvent.click(screen.getByRole('button', { name: 'Ask' }))
  expect(await screen.findByRole('heading', { name: 'Not enough evidence' })).toBeTruthy()

  await startPlan()
  expect(await screen.findByRole('heading', { name: 'Summary' })).toBeTruthy()
  expect(screen.queryByRole('heading', { name: 'Not enough evidence' })).toBeNull()
  expect(screen.getByRole('button', { name: 'Plan from an issue' }).getAttribute('aria-pressed')).toBe('true')

  fireEvent.click(screen.getByRole('button', { name: 'Question about the code' }))
  expect(await screen.findByRole('heading', { name: 'Not enough evidence' })).toBeTruthy()
  expect(screen.queryByRole('heading', { name: 'Summary' })).toBeNull()
  expect((screen.getByLabelText('Question') as HTMLTextAreaElement).value).toBe('Why?')

  fireEvent.click(screen.getByRole('button', { name: 'Plan from an issue' }))
  expect(await screen.findByRole('heading', { name: 'Summary' })).toBeTruthy()
})
