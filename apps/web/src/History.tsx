import { useInfiniteQuery } from '@tanstack/react-query'
import { useId, useState } from 'react'
import { type HistoryItem, type HistoryType, listHistory, type Me, type Repository, wordingFor } from './api.ts'
import { Link, PageTitle } from './Page.tsx'
import { investigationPath, planPath } from './router.ts'

const typeLabels: Record<string, string> = {
  question: 'Question',
  issue_to_plan: 'Plan',
}

// One wording per fixed name the API uses. Anything else gets the generic wording.
const stateLabels: Record<string, string> = {
  queued: 'Waiting for a worker',
  running: 'In progress',
  failed: 'Failed',
  cancelled: 'Cancelled',
}

const outcomeLabels: Record<string, string> = {
  answered: 'Answered',
  planned: 'Planned',
  insufficient_evidence: 'Not enough evidence',
}

function describeState(item: HistoryItem): string {
  if (item.state === 'completed') {
    return wordingFor(outcomeLabels, item.outcome) ?? 'Finished'
  }
  return wordingFor(stateLabels, item.state) ?? 'Unknown state'
}

/** The address of a history row, built from an ID that was checked and from nothing else. */
function addressOf(item: HistoryItem): string | null {
  return item.type === 'issue_to_plan' ? planPath(item.planId) : investigationPath(item.investigationId)
}

/**
 * The caller's own past questions and plans, newest first. Each row is a link to the item's
 * own address. Every text in a row that a person or GitHub wrote is shown as text.
 */
export function History({ repositories, me }: { repositories: Repository[]; me: Me | undefined }) {
  const repositorySelect = useId()
  const typeSelect = useId()
  const [repositoryId, setRepositoryId] = useState('')
  const [type, setType] = useState<HistoryType | ''>('')

  // A repository that is no longer in the list (access was lost) is not sent as a filter.
  const repository = repositories.find((candidate) => candidate.id === repositoryId)
  const filter = { repositoryId: repository?.id ?? null, type: type === '' ? null : type }

  const history = useInfiniteQuery({
    queryKey: ['history', filter.repositoryId, filter.type],
    queryFn: ({ pageParam }) => listHistory(filter, pageParam),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  })
  const items = history.data?.pages.flatMap((page) => page.items) ?? []
  const filtered = filter.repositoryId !== null || filter.type !== null
  const signedOut = me !== undefined && me.user === null

  return (
    <section className="history" aria-labelledby="history-heading">
      <PageTitle title="History" />
      <h2 id="history-heading" data-page-heading tabIndex={-1}>
        History
      </h2>

      {signedOut ? (
        <p className="hint">
          {me.canStartWork
            ? 'You are not signed in. This list shows what was asked from this browser, and only this browser can list it.'
            : 'You are not signed in, so there is no history to show. Sign in to see the questions and plans you asked for.'}
        </p>
      ) : (
        <p className="hint">The questions and plans you asked for, newest first. Only you can list them.</p>
      )}

      <div className="filters">
        <div className="field">
          <label htmlFor={repositorySelect}>Repository</label>
          <select id={repositorySelect} value={repository?.id ?? ''} onChange={(event) => setRepositoryId(event.target.value)}>
            <option value="">All repositories</option>
            {repositories.map((candidate) => (
              <option key={candidate.id} value={candidate.id}>
                {candidate.owner}/{candidate.name}
              </option>
            ))}
          </select>
        </div>
        <div className="field">
          <label htmlFor={typeSelect}>Kind</label>
          <select
            id={typeSelect}
            value={type}
            onChange={(event) => setType(event.target.value === 'question' || event.target.value === 'issue_to_plan' ? event.target.value : '')}
          >
            <option value="">Questions and plans</option>
            <option value="question">Questions</option>
            <option value="issue_to_plan">Plans</option>
          </select>
        </div>
      </div>

      {history.isPending && <p role="status">Loading your history…</p>}

      {history.isError && !history.isFetchNextPageError && (
        <>
          <p role="alert">Your history could not be loaded. {history.error.message}</p>
          <button type="button" className="secondary" onClick={() => void history.refetch()}>
            Try again
          </button>
        </>
      )}

      {history.isSuccess && items.length === 0 && (
        <p role="status">
          {filtered
            ? 'Nothing in your history matches these filters.'
            : 'Nothing here yet. Questions you ask and plans you start will be listed here.'}
        </p>
      )}

      {items.length > 0 && (
        <ul className="history-list">
          {items.map((item) => (
            <HistoryRow key={item.investigationId} item={item} />
          ))}
        </ul>
      )}

      {history.isFetchNextPageError && <p role="alert">More could not be loaded. {history.error.message}</p>}

      {history.hasNextPage && (
        <button
          type="button"
          className="secondary"
          disabled={history.isFetchingNextPage}
          onClick={() => void history.fetchNextPage()}
        >
          Load more
        </button>
      )}
      {history.isFetchingNextPage && <p role="status">Loading more…</p>}
    </section>
  )
}

function HistoryRow({ item }: { item: HistoryItem }) {
  const address = addressOf(item)
  const title =
    item.type === 'issue_to_plan' && item.issue
      ? `Plan for issue #${Math.trunc(item.issue.number)}${item.issue.title ? `: ${item.issue.title}` : ''}`
      : (item.question ?? 'Investigation')
  const created = new Date(item.createdAt)

  return (
    <li>
      {address ? (
        <Link to={address} className="history-title">
          {title}
        </Link>
      ) : (
        <span className="history-title">{title}</span>
      )}
      <p className="meta">
        <span className="history-kind">{wordingFor(typeLabels, item.type) ?? 'Investigation'}</span> ·{' '}
        {item.repository.owner}/{item.repository.name} · <span className="history-state">{describeState(item)}</span>
        {!Number.isNaN(created.getTime()) && (
          <>
            {' '}
            · <time dateTime={created.toISOString()}>{created.toLocaleString()}</time>
          </>
        )}
      </p>
    </li>
  )
}
