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
  usage?: { inputTokens: number; outputTokens: number }
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

  constructor(status: number, message: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
  }
}

interface ProblemDetails {
  title?: string
  detail?: string
  errors?: Record<string, string[]>
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, init)
  if (!response.ok) {
    throw new ApiError(response.status, await describeProblem(response))
  }
  return (await response.json()) as T
}

async function describeProblem(response: Response): Promise<string> {
  try {
    const problem = (await response.json()) as ProblemDetails
    const fieldErrors = Object.values(problem.errors ?? {}).flat()
    if (fieldErrors.length > 0) {
      return fieldErrors.join(' ')
    }
    return problem.detail ?? problem.title ?? `The API returned ${response.status}.`
  } catch {
    return `The API returned ${response.status}.`
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
