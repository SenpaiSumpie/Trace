// Types mirror src/Trace.Contracts/V1/Contracts.cs.

export interface Snapshot {
  id: string
  commitSha: string
  branch: string
  completedAt: string
}

export interface Repository {
  id: string
  owner: string
  name: string
  defaultBranch: string
  latestSnapshot: Snapshot | null
}

export interface Evidence {
  source: string
  path: string
  startLine: number
  endLine: number
  commitSha: string
  supports: string
  excerpt: string
}

export interface InvestigationResult {
  outcome: 'answered' | 'insufficient_evidence'
  answer: string
  evidence: Evidence[]
  unknowns: string[]
  limitations: string[]
}

export interface Usage {
  inputTokens: number
  outputTokens: number
  cacheReadTokens?: number
  cacheWriteTokens?: number
}

export interface Investigation {
  investigationId: string
  jobId: string | null
  repositoryId: string
  snapshotId: string
  commitSha: string
  type: string
  question: string
  state: 'queued' | 'running' | 'completed' | 'failed' | 'cancelled'
  modelId: string | null
  promptVersion: string | null
  createdAt: string
  updatedAt: string
  result: InvestigationResult | null
  errorCode: string | null
  usage?: Usage
}

export interface InvestigationEvent {
  seq: number
  eventType: string
  toolName: string | null
  summary: string
  occurredAt: string
}

/** An error response from the API, with a message that is safe to show. */
export class ApiError extends Error {
  readonly status: number

  /** The API's fixed name for what went wrong, such as `issues_permission_missing`, when it gave one. */
  readonly code: string | null

  /** The correlation ID of the failed request, when the API sent one. */
  readonly reference: string | null

  constructor(status: number, message: string, code: string | null = null, reference: string | null = null) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.reference = reference
  }
}

interface ProblemDetails {
  title?: string
  detail?: string
  code?: unknown
  errors?: Record<string, string[]>
}

const CORRELATION_HEADER = 'X-Correlation-ID'

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, init)
  if (!response.ok) {
    throw await toError(response)
  }
  return (await response.json()) as T
}

async function toError(response: Response): Promise<ApiError> {
  const { message, code } = await describeProblem(response)
  return new ApiError(response.status, withReference(message, response), code, response.headers.get(CORRELATION_HEADER))
}

/**
 * The API tags every request with an ID that also appears in its logs and traces. Showing it
 * with an error lets whoever looks into the problem find exactly this request.
 */
function withReference(message: string, response: Response): string {
  const reference = response.headers.get(CORRELATION_HEADER)
  return reference ? `${message} (Reference: ${reference})` : message
}

async function describeProblem(response: Response): Promise<{ message: string; code: string | null }> {
  try {
    const problem = (await response.json()) as ProblemDetails
    const code = typeof problem.code === 'string' ? problem.code : null
    const fieldErrors = Object.values(problem.errors ?? {}).flat()
    if (fieldErrors.length > 0) {
      return { message: fieldErrors.join(' '), code }
    }
    return { message: problem.detail ?? problem.title ?? `The API returned ${response.status}.`, code }
  } catch {
    return { message: `The API returned ${response.status}.`, code: null }
  }
}

export function listRepositories(): Promise<Repository[]> {
  return request<Repository[]>('/api/v1/repositories')
}

export function askQuestion(input: {
  repositoryId: string
  snapshotId: string
  question: string
  idempotencyKey: string
}): Promise<Investigation> {
  return request<Investigation>('/api/v1/investigations', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ ...input, type: 'question' }),
  })
}

export function listEvents(investigationId: string): Promise<InvestigationEvent[]> {
  return request<InvestigationEvent[]>(`/api/v1/investigations/${investigationId}/events`)
}

export function getInvestigation(investigationId: string): Promise<Investigation> {
  return request<Investigation>(`/api/v1/investigations/${investigationId}`)
}

/** Asks for the investigation's background job to be cancelled. */
export function cancelJob(jobId: string): Promise<unknown> {
  return request<unknown>(`/api/v1/jobs/${jobId}/cancel`, { method: 'POST' })
}

export function isFinished(investigation: Investigation): boolean {
  return investigation.state === 'completed' || investigation.state === 'failed' || investigation.state === 'cancelled'
}

export interface Job {
  id: string
  kind: string
  status: 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled'
  attempts: number
  maxAttempts: number
  cancelRequested: boolean
  errorCode: string | null
}

/** Queues indexing of a repository at the latest commit of its default branch. */
export function startIndexing(repositoryId: string): Promise<Job> {
  return request<Job>(`/api/v1/repositories/${repositoryId}/index-jobs`, { method: 'POST' })
}

export function getJob(jobId: string): Promise<Job> {
  return request<Job>(`/api/v1/jobs/${jobId}`)
}

export function isJobFinished(job: Job): boolean {
  return job.status === 'succeeded' || job.status === 'failed' || job.status === 'cancelled'
}

export interface Me {
  user: { login: string } | null
  signInAvailable: boolean
  canStartWork: boolean
}

export function getMe(): Promise<Me> {
  return request<Me>('/api/v1/me')
}

export async function signOut(): Promise<void> {
  const response = await fetch('/api/v1/auth/logout', { method: 'POST' })
  if (!response.ok) {
    throw new ApiError(response.status, 'Signing out failed.')
  }
}

/**
 * Looks a fixed name from the API up in a table of wordings. Only the table's own entries
 * count: a name such as "constructor" finds nothing.
 */
export function wordingFor<T>(table: Record<string, T>, name: string | null | undefined): T | undefined {
  return typeof name === 'string' && Object.hasOwn(table, name) ? table[name] : undefined
}

// Issues and plans. Every string in these types that comes from an issue, from source or from
// the model is untrusted: it is shown as text and never used to build markup or an address.

export interface IssueSummary {
  number: number
  title: string
  state: string
  author: string | null
  labels: string[]
  commentCount: number
  createdAt: string
  updatedAt: string
}

export interface IssueList {
  items: IssueSummary[]
  nextCursor: string | null
}

export interface IssueComment {
  id: number
  author: string | null
  body: string
  bodyTruncated: boolean
  createdAt: string
}

export interface Issue extends IssueSummary {
  body: string
  bodyTruncated: boolean
  comments: IssueComment[]
  commentsTruncated: boolean
}

export const ISSUE_PAGE_SIZE = 20

/** One page of a repository's open issues, newest first. A page may hold fewer items than asked for. */
export function listIssues(repositoryId: string, cursor: string | null): Promise<IssueList> {
  const query = new URLSearchParams({ limit: String(ISSUE_PAGE_SIZE) })
  if (cursor) {
    query.set('cursor', cursor)
  }
  return request<IssueList>(`/api/v1/repositories/${encodeURIComponent(repositoryId)}/issues?${query.toString()}`)
}

export function getIssue(repositoryId: string, number: number): Promise<Issue> {
  return request<Issue>(`/api/v1/repositories/${encodeURIComponent(repositoryId)}/issues/${Math.trunc(number)}`)
}

export interface PlanStatement {
  text: string
  citations: string[]
}

export interface PlanFile {
  path: string
  change: string
  reason: string
  citations: string[]
}

export interface PlanEvidence {
  id: string
  source: string
  path: string
  startLine: number
  endLine: number
  commitSha: string
  excerpt: string
}

export interface PlanDocument {
  schemaVersion: string
  summary: PlanStatement
  steps: PlanStatement[]
  filesToChange: PlanFile[]
  risks: PlanStatement[]
  verification: PlanStatement[]
  unknowns: PlanStatement[]
  evidence: PlanEvidence[]
}

export interface PlanTrimmed {
  citations: number
  steps: number
  files: number
  risks: number
  verification: number
  unknowns: number
  summaryReplaced: boolean
}

export interface Plan {
  planId: string
  investigationId: string
  jobId: string | null
  repositoryId: string
  snapshotId: string
  commitSha: string
  issue: { number: number; title: string | null }
  state: 'queued' | 'running' | 'planned' | 'insufficient_evidence' | 'failed' | 'cancelled'
  errorCode: string | null
  modelId: string | null
  promptVersion: string | null
  createdAt: string
  updatedAt: string
  usage?: Usage
  plan: PlanDocument | null
  trimmed: PlanTrimmed | null
  limitations: string[]
  reviewedAt: string | null
  issueCheck: { status: string; code: string | null } | null
}

/** Queues an issue-to-plan run pinned to a snapshot. A worker runs it: poll the plan until it is finished. */
export function createPlan(input: {
  repositoryId: string
  issueNumber: number
  snapshotId: string
  idempotencyKey: string
}): Promise<Plan> {
  return request<Plan>(
    `/api/v1/repositories/${encodeURIComponent(input.repositoryId)}/issues/${Math.trunc(input.issueNumber)}/plans`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ snapshotId: input.snapshotId, idempotencyKey: input.idempotencyKey }),
    },
  )
}

/** With `checkIssue`, the API reads the issue again and says whether it still is what the plan was made from. */
export function getPlan(planId: string, checkIssue = false): Promise<Plan> {
  return request<Plan>(`/api/v1/plans/${encodeURIComponent(planId)}${checkIssue ? '?checkIssue=true' : ''}`)
}

/** Records in TRACE that a person has read the plan. Nothing outside TRACE changes. */
export function reviewPlan(planId: string): Promise<Plan> {
  return request<Plan>(`/api/v1/plans/${encodeURIComponent(planId)}/review`, { method: 'POST' })
}

export function isPlanFinished(plan: Plan): boolean {
  return plan.state !== 'queued' && plan.state !== 'running'
}

export interface PlanExport {
  fileName: string
  markdown: Blob
}

/** The plan as a Markdown file, exactly as the server wrote it. */
export async function exportPlan(planId: string): Promise<PlanExport> {
  const response = await fetch(`/api/v1/plans/${encodeURIComponent(planId)}/export`)
  if (!response.ok) {
    throw await toError(response)
  }
  return {
    fileName: exportFileName(response.headers.get('Content-Disposition'), planId),
    markdown: new Blob([await response.text()], { type: 'text/markdown' }),
  }
}

/**
 * The server builds the file name from the issue number and the plan's ID only. It is still
 * taken only when it is a plain name ending in .md; anything else gets a name made here.
 */
function exportFileName(disposition: string | null, planId: string): string {
  const name = /filename="?([A-Za-z0-9._-]+)"?/.exec(disposition ?? '')?.[1]
  if (name && /^[A-Za-z0-9][A-Za-z0-9._-]{0,120}\.md$/.test(name)) {
    return name
  }
  return `trace-plan-${planId.replace(/[^A-Za-z0-9-]/g, '')}.md`
}

// History: the caller's own past investigations. The question, the issue title and the
// repository's owner and name are untrusted text, shown as text and never put into an address.

export interface HistoryItem {
  investigationId: string
  type: string
  repository: { id: string; owner: string; name: string }
  state: string
  outcome: string | null
  createdAt: string
  question: string | null
  issue: { number: number; title: string | null } | null
  planId: string | null
}

export interface HistoryList {
  items: HistoryItem[]
  nextCursor: string | null
}

export type HistoryType = 'question' | 'issue_to_plan'

export const HISTORY_PAGE_SIZE = 20

/** One page of the caller's own investigations, newest first. */
export function listHistory(
  filter: { repositoryId: string | null; type: HistoryType | null },
  cursor: string | null,
): Promise<HistoryList> {
  const query = new URLSearchParams({ limit: String(HISTORY_PAGE_SIZE) })
  if (filter.repositoryId) {
    query.set('repositoryId', filter.repositoryId)
  }
  if (filter.type) {
    query.set('type', filter.type)
  }
  if (cursor) {
    query.set('cursor', cursor)
  }
  return request<HistoryList>(`/api/v1/investigations?${query.toString()}`)
}

/** Whether the API said that the thing asked for does not exist or may not be seen; it never says which. */
export function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404
}
