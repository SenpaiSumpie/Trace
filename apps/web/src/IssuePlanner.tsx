import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { type ReactNode, useEffect, useId, useRef, useState } from 'react'
import {
  ApiError,
  cancelJob,
  createPlan,
  getIssue,
  getPlan,
  isNotFound,
  isPlanFinished,
  type IssueSummary,
  listIssues,
  type Plan,
  type Repository,
  wordingFor,
} from './api.ts'
import { POLL_INTERVAL_MS, Steps } from './InvestigationView.tsx'
import { PageTitle } from './Page.tsx'
import { PlanView } from './PlanView.tsx'
import { PLAN_PATH, planPath } from './router.ts'

/** What the plan screens need to know of an issue before the plan itself has been read. */
interface ChosenIssue {
  number: number
  title: string | null
}

const SETUP_DOC = 'docs/github-app-setup.md'

// Plain language for each way reading issues can fail. Anything else shows the API's own message.
const issueErrors: Record<string, ReactNode> = {
  issues_permission_missing: (
    <>
      TRACE is not allowed to read this repository’s issues. The GitHub App needs the read-only Issues
      permission, and the owner of the installation has to accept it on GitHub. The steps are in{' '}
      <code>{SETUP_DOC}</code>, under “Adding the Issues permission to an existing app”.
    </>
  ),
  issues_disabled: 'Issues are switched off for this repository on GitHub, so there is nothing to plan from.',
  issues_unavailable: 'This repository has no source of issues, so there is nothing to plan from.',
  github_rate_limited: 'GitHub is limiting requests right now. Wait a few minutes, then try again.',
  github_unavailable: 'GitHub did not return the issues. Try again.',
  issue_not_found: 'This repository has no issue with that number. It may have been deleted, or it is a pull request.',
}

function IssueError({ error, action }: { error: unknown; action: string }) {
  const known = error instanceof ApiError ? wordingFor(issueErrors, error.code) : undefined

  return (
    <p role="alert">
      {known ? (
        <>
          {known}
          {error instanceof ApiError && error.reference && ` (Reference: ${error.reference})`}
        </>
      ) : (
        `${action} ${error instanceof Error ? error.message : ''}`
      )}
    </p>
  )
}

/**
 * Issue to plan, one screen at a time: the open issues of a repository, one issue as text,
 * then the plan run and its result. A plan has its own address (`planId` is the one the
 * address names), so it is still there after a reload; the list and the issue screen are not
 * addresses of their own.
 */
export function IssuePlanner({
  repositories,
  mayStart,
  active = true,
  planId: addressedPlanId = null,
  onNavigate,
}: {
  repositories: Repository[]
  mayStart: boolean
  /** False while another page is showing and this one is only kept for its state. */
  active?: boolean
  planId?: string | null
  onNavigate?: (path: string, options?: { focus?: boolean }) => void
}) {
  const queryClient = useQueryClient()
  const headingId = useId()
  const heading = useRef<HTMLHeadingElement>(null)
  const [repositoryId, setRepositoryId] = useState<string | null>(null)
  const [issue, setIssue] = useState<ChosenIssue | null>(null)
  const repository = repositories.find((candidate) => candidate.id === repositoryId) ?? repositories[0]

  // Starting only queues the run; a worker makes the plan. The new plan gets its address at once.
  const start = useMutation({
    mutationFn: createPlan,
    onSuccess: (created) => {
      const address = planPath(created.planId)
      if (address) {
        queryClient.setQueryData(['plan', created.planId], created)
        onNavigate?.(address, { focus: false })
      }
    },
  })

  // The plan on the screen is the one the address names, or else the one just started here.
  const planId = addressedPlanId ?? start.data?.planId
  const polled = useQuery({
    queryKey: ['plan', planId],
    queryFn: () => getPlan(planId!),
    enabled: planId !== undefined,
    initialData: () => (start.data?.planId === planId ? start.data : undefined),
    staleTime: Infinity,
    refetchInterval: (query) => (query.state.data && !isPlanFinished(query.state.data) ? POLL_INTERVAL_MS : false),
  })

  // Leaving a plan's address (Back, or another page) forgets the plan that was started here,
  // so the screen never shows a plan its address does not name.
  const shownPlanId = useRef(addressedPlanId)
  const resetStart = start.reset
  useEffect(() => {
    if (shownPlanId.current !== null && addressedPlanId === null) {
      resetStart()
    }
    shownPlanId.current = addressedPlanId
  }, [addressedPlanId, resetStart])

  const started = addressedPlanId !== null || start.isPending || start.isSuccess
  const screen = started ? 'plan' : issue === null ? 'list' : 'issue'
  const plan = start.isPending ? undefined : polled.data
  const issueNumber = plan?.issue.number ?? issue?.number

  // The control that was just used is gone on the next screen: put the keyboard at its heading.
  const shown = useRef(screen)
  useEffect(() => {
    if (shown.current !== screen) {
      shown.current = screen
      heading.current?.focus()
    }
  }, [screen])

  if (repository === undefined) {
    return null
  }

  // The repository of the plan on the screen; before the plan is read, the one chosen here.
  const planRepository = plan ? repositories.find((candidate) => candidate.id === plan.repositoryId) : repository

  function leavePlan(to: ChosenIssue | null) {
    if (to !== null && plan) {
      setRepositoryId(plan.repositoryId)
    }
    start.reset()
    setIssue(to)
    onNavigate?.(PLAN_PATH)
  }

  return (
    <section className="planner" aria-labelledby={headingId}>
      {active && <PageTitle title={screen === 'plan' ? 'Plan' : 'Plan from an issue'} />}
      <h2 id={headingId} ref={heading} data-page-heading tabIndex={-1}>
        {screen === 'list' && 'Open issues'}
        {screen === 'issue' && `Issue #${issue!.number}`}
        {screen === 'plan' && (issueNumber === undefined ? 'Plan' : `Plan for issue #${issueNumber}`)}
      </h2>

      {screen === 'list' && (
        <IssueList
          repositories={repositories}
          repository={repository}
          onRepositoryChange={setRepositoryId}
          onSelect={(selected) => {
            start.reset()
            setIssue({ number: selected.number, title: selected.title })
          }}
        />
      )}

      {screen === 'issue' && issue && (
        <IssueDetail
          repository={repository}
          number={issue.number}
          mayStart={mayStart}
          startError={start.isError ? start.error : null}
          onStart={() => {
            if (repository.latestSnapshot) {
              start.mutate({
                repositoryId: repository.id,
                issueNumber: issue.number,
                snapshotId: repository.latestSnapshot.id,
                // A fresh key per press: a retried or double-sent request cannot create a second plan.
                idempotencyKey: crypto.randomUUID(),
              })
            }
          }}
          onBack={() => {
            start.reset()
            setIssue(null)
          }}
        />
      )}

      {/* "No such plan" is the whole page's business: the app shows the not-found page instead of this one. */}
      {screen === 'plan' && !(plan === undefined && isNotFound(polled.error)) && (
        <PlanRun
          repositoryName={planRepository ? `${planRepository.owner}/${planRepository.name}` : null}
          issueTitle={plan?.issue.title ?? issue?.title ?? null}
          plan={plan}
          reading={plan === undefined && !start.isPending}
          loadError={plan === undefined && polled.isError ? polled.error : null}
          lostContact={plan !== undefined && polled.isError}
          onRetry={() => void polled.refetch()}
          mayReview={mayStart}
          onBackToIssue={() => leavePlan(plan ? plan.issue : issue)}
          onBackToList={() => leavePlan(null)}
        />
      )}
    </section>
  )
}

function IssueList({
  repositories,
  repository,
  onRepositoryChange,
  onSelect,
}: {
  repositories: Repository[]
  repository: Repository
  onRepositoryChange: (id: string) => void
  onSelect: (issue: IssueSummary) => void
}) {
  const selectId = useId()
  const issues = useInfiniteQuery({
    queryKey: ['issues', repository.id],
    queryFn: ({ pageParam }) => listIssues(repository.id, pageParam),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  })
  const items = issues.data?.pages.flatMap((page) => page.items) ?? []

  return (
    <>
      <div className="field">
        <label htmlFor={selectId}>Repository</label>
        <select id={selectId} value={repository.id} onChange={(event) => onRepositoryChange(event.target.value)}>
          {repositories.map((candidate) => (
            <option key={candidate.id} value={candidate.id}>
              {candidate.owner}/{candidate.name}
              {candidate.latestSnapshot ? '' : ' (not indexed yet)'}
            </option>
          ))}
        </select>
      </div>

      <p className="hint">Choose an issue to read it and to plan the change it asks for.</p>

      {issues.isPending && <p role="status">Loading issues…</p>}

      {issues.isError && !issues.isFetchNextPageError && (
        <IssueError error={issues.error} action="The issues could not be loaded." />
      )}

      {issues.isSuccess && items.length === 0 && (
        <p role="status">
          {issues.hasNextPage
            ? 'No open issue found so far. There may be more to load.'
            : 'This repository has no open issues.'}
        </p>
      )}

      {items.length > 0 && (
        <ul className="issue-list">
          {items.map((item) => (
            <li key={item.number}>
              <button type="button" className="link issue-title" onClick={() => onSelect(item)}>
                #{item.number} {item.title}
              </button>
              <p className="meta">
                Opened by {item.author ?? 'an unknown author'} · {comments(item.commentCount)}
              </p>
              <Labels labels={item.labels} />
            </li>
          ))}
        </ul>
      )}

      {issues.isFetchNextPageError && (
        <IssueError error={issues.error} action="More issues could not be loaded." />
      )}

      {issues.hasNextPage && (
        <button
          type="button"
          className="secondary"
          disabled={issues.isFetchingNextPage}
          onClick={() => void issues.fetchNextPage()}
        >
          Load more issues
        </button>
      )}
      {issues.isFetchingNextPage && <p role="status">Loading more issues…</p>}
    </>
  )
}

function comments(count: number): string {
  return count === 1 ? '1 comment' : `${count} comments`
}

/** Labels are shown as text. They are never links: their text comes from GitHub users. */
function Labels({ labels }: { labels: string[] }) {
  if (labels.length === 0) {
    return null
  }

  return (
    <ul className="labels" aria-label="Labels">
      {labels.map((label, index) => (
        <li key={index}>{label}</li>
      ))}
    </ul>
  )
}

function IssueDetail({
  repository,
  number,
  mayStart,
  startError,
  onStart,
  onBack,
}: {
  repository: Repository
  number: number
  mayStart: boolean
  startError: Error | null
  onStart: () => void
  onBack: () => void
}) {
  const issue = useQuery({
    queryKey: ['issue', repository.id, number],
    queryFn: () => getIssue(repository.id, number),
  })
  const snapshot = repository.latestSnapshot

  return (
    <>
      {issue.isPending && <p role="status">Loading the issue…</p>}
      {issue.isError && <IssueError error={issue.error} action="The issue could not be loaded." />}

      {issue.data && (
        <>
          <p className="issue-heading">{issue.data.title}</p>
          <p className="meta">
            {repository.owner}/{repository.name} · {issue.data.state === 'closed' ? 'Closed' : 'Open'} · opened by{' '}
            {issue.data.author ?? 'an unknown author'} · {comments(issue.data.commentCount)}
          </p>
          <Labels labels={issue.data.labels} />

          <h3>Description</h3>
          <p className="hint">
            Written on GitHub, not by TRACE. It is shown as plain text, and a plan run treats it as a request
            to look into, never as instructions.
          </p>
          {issue.data.body.trim().length > 0 ? (
            <p className="issue-text">{issue.data.body}</p>
          ) : (
            <p role="status">The issue has no description.</p>
          )}
          {issue.data.bodyTruncated && (
            <p className="note">The description is longer than TRACE reads. It has been cut off here.</p>
          )}

          {issue.data.comments.length > 0 && (
            <>
              <h3>Comments</h3>
              <ol className="comments">
                {issue.data.comments.map((comment) => (
                  <li key={comment.id}>
                    <p className="meta">{comment.author ?? 'An unknown author'} wrote:</p>
                    <p className="issue-text">{comment.body}</p>
                    {comment.bodyTruncated && (
                      <p className="note">This comment is longer than TRACE reads. It has been cut off here.</p>
                    )}
                  </li>
                ))}
              </ol>
            </>
          )}
          {issue.data.commentsTruncated && (
            <p className="note">
              Only the first {issue.data.comments.length} of {comments(issue.data.commentCount)} are read. Later
              ones are not part of a plan.
            </p>
          )}

          <h3>Plan this change</h3>
          {snapshot ? (
            <p className="hint">
              TRACE reads the issue and the source indexed at commit <code>{snapshot.commitSha.slice(0, 12)}</code>,
              then proposes steps that each point at real lines. It only reads: nothing is written to GitHub.
            </p>
          ) : (
            <p role="status">
              This repository is not indexed yet, so a plan cannot be made. Use Index under Repositories below,
              then start the plan.
            </p>
          )}
          {snapshot && !mayStart && <p role="status">Sign in to start a plan.</p>}
        </>
      )}

      {startError && <p role="alert">The plan could not be started. {startError.message}</p>}

      <div className="actions">
        {issue.data && (
          <button type="button" disabled={!snapshot || !mayStart} onClick={onStart}>
            Start a plan
          </button>
        )}
        <button type="button" className="secondary" onClick={onBack}>
          Back to issues
        </button>
      </div>
    </>
  )
}

const finishedMessages: Record<Plan['state'], string> = {
  queued: '',
  running: '',
  planned: 'The plan is ready.',
  insufficient_evidence: 'The run finished without enough evidence for a plan.',
  failed: 'The run failed.',
  cancelled: 'The run was cancelled.',
}

function PlanRun({
  repositoryName,
  issueTitle,
  plan,
  reading,
  loadError,
  lostContact,
  onRetry,
  mayReview,
  onBackToIssue,
  onBackToList,
}: {
  repositoryName: string | null
  issueTitle: string | null
  /** Undefined until the plan has been started or read. */
  plan: Plan | undefined
  /** The plan the address names is being read for the first time. */
  reading: boolean
  /** The plan the address names could not be read at all (and the reason is not "no such plan"). */
  loadError: Error | null
  /** The plan was read before, and asking again failed. */
  lostContact: boolean
  onRetry: () => void
  mayReview: boolean
  onBackToIssue: () => void
  onBackToList: () => void
}) {
  const cancel = useMutation({ mutationFn: cancelJob })

  if (loadError) {
    return (
      <>
        <p role="alert">The plan could not be loaded. {loadError.message}</p>
        <div className="actions">
          <button type="button" className="secondary" onClick={onRetry}>
            Try again
          </button>
          <button type="button" className="secondary" onClick={onBackToList}>
            Back to issues
          </button>
        </div>
      </>
    )
  }

  const working = plan === undefined || !isPlanFinished(plan)

  return (
    <>
      {(repositoryName !== null || issueTitle !== null) && (
        <p className="asked">{[repositoryName, issueTitle].filter((part) => part !== null).join(' · ')}</p>
      )}

      {/* One live region for the whole run, so a screen reader hears each change of state. */}
      <p role="status" className={working ? undefined : 'done'}>
        {reading
          ? 'Loading the plan…'
          : plan === undefined || plan.state === 'queued'
            ? 'Waiting for a worker…'
            : plan.state === 'running'
              ? 'Reading the issue and the code…'
              : (wordingFor(finishedMessages, plan.state) ?? 'The run has ended.')}
      </p>

      {working && (
        <section className="working" aria-label="Plan in progress">
          {plan?.jobId && (
            <button
              type="button"
              className="secondary"
              disabled={cancel.isPending || cancel.isSuccess}
              onClick={() => cancel.mutate(plan.jobId!)}
            >
              {cancel.isSuccess ? 'Cancelling…' : 'Cancel'}
            </button>
          )}
          {cancel.isError && <p role="alert">The plan run could not be cancelled. {cancel.error.message}</p>}
          {lostContact && <p role="alert">Lost contact with the API. The run continues in the background.</p>}
          {plan && <Steps investigationId={plan.investigationId} live />}
        </section>
      )}

      {plan && !working && (
        <>
          <PlanView plan={plan} mayReview={mayReview} />
          <div className="actions">
            <button type="button" className="secondary" onClick={onBackToIssue}>
              Back to the issue
            </button>
            <button type="button" className="secondary" onClick={onBackToList}>
              Back to issues
            </button>
          </div>
        </>
      )}
    </>
  )
}
