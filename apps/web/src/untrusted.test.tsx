// Text from an issue, from source or from the model is untrusted. These tests feed hostile
// strings into every such field and check two things: each string is on the page exactly as
// written, and nothing it describes was created.

import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import type { Issue, Plan, Repository } from './api.ts'
import App from './App.tsx'

const BIDI = '‮gnp.exe‬ ⁦x⁩ ‏y'

/** One hostile string of each kind, made unique by the name of the field it goes into. */
function hostile(field: string): string[] {
  return [
    `<script>window.__pwned = '${field}'</script>`,
    `<img src=x onerror="window.__pwned='${field}'">`,
    `<a href="javascript:alert('${field}')">link</a>`,
    `[${field}](javascript:alert(1)) ![x](https://attacker.invalid/${field}.png)`,
    `# ${field} **bold** _em_ \`code\` <b>b</b> <iframe src="https://attacker.invalid"></iframe>`,
    `&lt;b&gt; &amp; &#x3C;svg onload=alert(1)&#x3E; ${field}`,
    `${field} ${BIDI}`,
  ]
}

const all = (field: string) => hostile(field).join(' ')

const repository: Repository = {
  id: 'repo-1',
  owner: 'trace-demo',
  name: 'sample-repo',
  defaultBranch: 'main',
  latestSnapshot: { id: 'snap-1', commitSha: 'ee02b2da366aa000b30541463014b895d5cebb67', branch: 'main', completedAt: '' },
}

const issue: Issue = {
  number: 5,
  title: all('title'),
  state: 'open',
  author: all('author'),
  labels: hostile('label'),
  commentCount: 1,
  createdAt: '',
  updatedAt: '',
  body: hostile('body').join('\n'),
  bodyTruncated: false,
  comments: [{ id: 1, author: all('comment-author'), body: hostile('comment').join('\n'), bodyTruncated: false, createdAt: '' }],
  commentsTruncated: false,
}

const E1 = '0193a5c0-0000-7000-8000-000000000001'

const plan: Plan = {
  planId: 'plan-1',
  investigationId: 'inv-9',
  jobId: 'job-9',
  repositoryId: 'repo-1',
  snapshotId: 'snap-1',
  commitSha: `ee02b2da366a${all('commit')}`,
  issue: { number: 5, title: all('plan-issue-title') },
  state: 'planned',
  errorCode: null,
  modelId: all('model'),
  promptVersion: all('prompt'),
  createdAt: '',
  updatedAt: '',
  plan: {
    schemaVersion: 'plan-v1',
    summary: { text: all('summary'), citations: [E1] },
    steps: [{ text: hostile('step').join('\n'), citations: [E1] }],
    filesToChange: [
      { path: all('file-path'), change: all('change'), reason: all('reason'), citations: [E1] },
      { path: 'javascript:alert(1)', change: 'create', reason: all('new-file-reason'), citations: [] },
    ],
    risks: [{ text: all('risk'), citations: [E1] }],
    verification: [{ text: all('verification'), citations: [E1] }],
    unknowns: [{ text: all('unknown'), citations: [] }],
    evidence: [
      { id: E1, source: 'code', path: all('evidence-path'), startLine: 1, endLine: 7, commitSha: '', excerpt: hostile('excerpt').join('\n') },
    ],
  },
  trimmed: { citations: 0, steps: 0, files: 0, risks: 0, verification: 0, unknowns: 0, summaryReplaced: false },
  limitations: [all('limitation')],
  reviewedAt: null,
  issueCheck: null,
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
}

function renderApp(routes: Record<string, () => Response>) {
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const route = routes[`${init?.method ?? 'GET'} ${String(input)}`]
      return route ? Promise.resolve(route()) : Promise.reject(new TypeError('No route'))
    }),
  )
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <App />
    </QueryClientProvider>,
  )
}

const routes = {
  'GET /api/v1/repositories': () => json([repository]),
  'GET /api/v1/repositories/repo-1/issues?limit=20': () =>
    json({ items: [{ ...issue, body: undefined, comments: undefined }], nextCursor: null }),
  'GET /api/v1/repositories/repo-1/issues/5': () => json(issue),
  'POST /api/v1/repositories/repo-1/issues/5/plans': () => json(plan, 202),
  'GET /api/v1/investigations/inv-9/events': () =>
    json([{ seq: 1, eventType: all('event-type'), toolName: null, summary: all('event'), occurredAt: '' }]),
}

/** Every hostile string for these fields is on the page as literal text. */
function expectLiteral(fields: string[]) {
  const text = document.body.textContent!
  for (const field of fields) {
    for (const payload of hostile(field)) {
      expect(text, `${field}: ${payload}`).toContain(payload)
    }
  }
}

/** Nothing a hostile string describes exists: no element it names, no link at all, no script that ran. */
function expectNoMarkup() {
  expect(document.querySelector('script, img, iframe, svg, b, strong, em, i, object, embed, style, link, form, input')).toBeNull()
  // This page has no links of its own in these tests (nobody is signed in and sign-in is not offered).
  expect(document.querySelector('a, [href], [src], [onerror], [onload], [onclick]')).toBeNull()
  // Headings are only the ones TRACE wrote: "# title" did not become one.
  for (const heading of document.querySelectorAll('h1, h2, h3, h4, h5, h6')) {
    expect(heading.textContent).not.toMatch(/bold|<script|javascript:|attacker/)
  }
  expect((window as unknown as { __pwned?: string }).__pwned).toBeUndefined()
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

test('hostile issue text in the list and on the issue screen is shown as text', async () => {
  renderApp(routes)

  fireEvent.click(await screen.findByRole('button', { name: 'Plan from an issue' }))
  const open = await screen.findByRole('button', { name: /^#5 <script>/ })
  expectLiteral(['title', 'author', 'label'])
  expectNoMarkup()
  // The title is the button's whole name, bidirectional controls included: nothing was stripped or reordered.
  expect(open.textContent).toBe(`#5 ${issue.title}`)
  expect(open.textContent).toContain(BIDI)

  fireEvent.click(open)
  await screen.findByRole('heading', { name: 'Description' })
  expectLiteral(['title', 'author', 'label', 'body', 'comment-author', 'comment'])
  expectNoMarkup()
  // Line breaks of the body are kept as characters, not turned into elements.
  expect(document.querySelector('.issue-text')!.textContent).toBe(issue.body)
  expect(document.querySelector('.issue-text')!.children).toHaveLength(0)
})

test('hostile text in a plan, its paths, its source excerpts and its run is shown as text', async () => {
  renderApp(routes)

  fireEvent.click(await screen.findByRole('button', { name: 'Plan from an issue' }))
  fireEvent.click(await screen.findByRole('button', { name: /^#5 <script>/ }))
  fireEvent.click(await screen.findByRole('button', { name: 'Start a plan' }))
  await screen.findByRole('heading', { name: 'Summary' })
  await screen.findByText(new RegExp('window.__pwned = \'event\''))

  // Open every citation so the cited source is on the page under each sentence too.
  for (const toggle of screen.getAllByRole('button', { name: 'Show evidence E1' })) {
    fireEvent.click(toggle)
  }

  expectLiteral([
    'plan-issue-title', 'summary', 'step', 'file-path', 'change', 'reason', 'new-file-reason', 'risk',
    'verification', 'unknown', 'evidence-path', 'excerpt', 'limitation', 'event', 'model', 'prompt',
  ])
  expectNoMarkup()
  // A path that looks like an address is text in a span, not something to follow.
  expect(screen.getByText('javascript:alert(1)').tagName).toBe('SPAN')
  // Source lines stay one element per line: an excerpt cannot add structure.
  for (const line of document.querySelectorAll('.line code')) {
    expect(line.children).toHaveLength(0)
  }
})

test('a hostile failure name, check status and export file name never reach the page as markup or as a name', async () => {
  const saved: string[] = []
  URL.createObjectURL = vi.fn(() => 'blob:trace-test')
  URL.revokeObjectURL = vi.fn()
  const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
    saved.push(this.download)
  })
  renderApp({
    ...routes,
    'GET /api/v1/plans/plan-1?checkIssue=true': () => json({ ...plan, issueCheck: { status: all('status'), code: all('code') } }),
    'GET /api/v1/plans/plan-1/export': () =>
      new Response('<script>window.__pwned = "export"</script>', {
        headers: { 'Content-Type': 'text/html', 'Content-Disposition': 'attachment; filename="<img src=x onerror=alert(1)>.html"' },
      }),
  })

  fireEvent.click(await screen.findByRole('button', { name: 'Plan from an issue' }))
  fireEvent.click(await screen.findByRole('button', { name: /^#5 <script>/ }))
  fireEvent.click(await screen.findByRole('button', { name: 'Start a plan' }))
  fireEvent.click(await screen.findByRole('button', { name: 'Check whether the issue changed' }))
  await screen.findByText('The issue could not be compared right now. Try again later.')
  fireEvent.click(screen.getByRole('button', { name: 'Download as Markdown' }))
  await screen.findByText('Saved as trace-plan-plan-1.md.')

  // Whatever the server sent, the file is saved as Markdown under a name made of safe characters.
  expect(saved).toEqual(['trace-plan-plan-1.md'])
  expect((vi.mocked(URL.createObjectURL).mock.calls[0][0] as Blob).type).toBe('text/markdown')
  expect(document.body.textContent).not.toContain('window.__pwned = \'status\'')
  expect(document.body.textContent).not.toContain('window.__pwned = \'code\'')
  expectNoMarkup()
  click.mockRestore()
})

test('hostile questions, issue titles and repository names in the history are shown as text, and no address is built from them', async () => {
  const INV = '0193a5c0-0000-7000-8000-0000000000c1'
  const PLAN = '0193a5c0-0000-7000-8000-0000000000d1'
  const row = {
    type: 'question',
    repository: { id: 'repo-1', owner: all('repo-owner'), name: all('repo-name') },
    state: 'completed',
    outcome: 'answered',
    createdAt: '2026-11-20T12:00:01Z',
    question: null,
    issue: null,
    planId: null,
  }
  window.history.replaceState(null, '', '/history')
  renderApp({
    'GET /api/v1/repositories': () => json([{ ...repository, owner: all('filter-owner'), name: all('filter-name') }]),
    'GET /api/v1/investigations?limit=20': () =>
      json({
        items: [
          { ...row, investigationId: INV, question: hostile('question').join('\n') },
          { ...row, investigationId: 'inv-plan', type: 'issue_to_plan', outcome: 'planned', issue: { number: 5, title: all('history-issue-title') }, planId: PLAN },
          // Names the API never sends, in the places where fixed names are expected.
          { ...row, investigationId: all('id'), type: all('type'), state: all('state'), outcome: all('outcome'), question: 'q', createdAt: all('time') },
          { ...row, investigationId: 'x', type: 'issue_to_plan', planId: 'javascript:alert(1)//', issue: { number: 6, title: null } },
        ],
        nextCursor: null,
      }),
  })

  await screen.findByText(/window\.__pwned = 'history-issue-title'/)
  expectLiteral(['question', 'history-issue-title', 'repo-owner', 'repo-name', 'filter-owner', 'filter-name'])

  // Nothing the strings describe exists. The only links are the two rows with a real ID, and
  // each address is the fixed word plus that ID: no text of the row is in it.
  expect(document.querySelector('script, img, iframe, svg, b, strong, em, i, object, embed, style, link, form, input')).toBeNull()
  expect(document.querySelector('[src], [onerror], [onload], [onclick]')).toBeNull()
  expect([...document.querySelectorAll('a')].map((link) => link.getAttribute('href'))).toEqual([`/investigations/${INV}`, `/plans/${PLAN}`])
  expect([...document.querySelectorAll('[href]')]).toHaveLength(2)
  for (const heading of document.querySelectorAll('h1, h2, h3, h4, h5, h6')) {
    expect(heading.textContent).not.toMatch(/bold|<script|javascript:|attacker/)
  }
  expect((window as unknown as { __pwned?: string }).__pwned).toBeUndefined()

  // A question keeps its line breaks as characters inside the link; it adds no element.
  const question = document.querySelector(`a[href="/investigations/${INV}"]`)!
  expect(question.textContent).toBe(hostile('question').join('\n'))
  expect(question.children).toHaveLength(0)
  expect(question.textContent).toContain(BIDI)

  // Made-up fixed names get the generic wording; none of them is on the page, as text or otherwise.
  for (const field of ['id', 'type', 'state', 'outcome', 'time']) {
    expect(document.body.textContent).not.toContain(`window.__pwned = '${field}'`)
  }
  // A time that is not a time is left out; the other three rows have theirs.
  expect(document.querySelectorAll('time')).toHaveLength(3)
  expect(document.title).toBe('History · TRACE')
})

test('no source file of the web app writes markup from a string', () => {
  const sources = import.meta.glob('./**/*.{ts,tsx}', { query: '?raw', import: 'default', eager: true }) as Record<string, string>
  const names = Object.keys(sources)
  expect(names).toContain('./PlanView.tsx')
  expect(names).toContain('./IssuePlanner.tsx')
  expect(names.length).toBeGreaterThan(8)

  // Written in pieces so this file does not match itself.
  const forbidden = [
    new RegExp('dangerously' + 'SetInnerHTML'),
    new RegExp('\\.(inner|outer)' + 'HTML\\b'),
    new RegExp('insertAdjacent' + 'HTML'),
    new RegExp('document\\.wri' + 'te'),
    new RegExp('create' + 'ContextualFragment'),
    new RegExp('DOM' + 'Parser'),
    new RegExp('\\bsrc' + 'doc\\b', 'i'),
    new RegExp('\\bev' + 'al\\('),
    new RegExp('new Func' + 'tion\\('),
  ]
  for (const [name, source] of Object.entries(sources)) {
    for (const pattern of forbidden) {
      expect(pattern.test(source), `${name} matches ${String(pattern)}`).toBe(false)
    }
  }
})
