import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { type FormEvent, useEffect, useRef, useState } from 'react'
import {
  askQuestion,
  cancelJob,
  getInvestigation,
  getMe,
  getPlan,
  isFinished,
  isNotFound,
  listRepositories,
  signOut,
  type Repository,
} from './api.ts'
import { ApiStatus } from './ApiStatus.tsx'
import { History } from './History.tsx'
import { InvestigationView, POLL_INTERVAL_MS, Steps } from './InvestigationView.tsx'
import { IssuePlanner } from './IssuePlanner.tsx'
import { NotFound, PageTitle } from './Page.tsx'
import { RepositoryList } from './RepositoryList.tsx'
import { focusPageHeading, HISTORY_PATH, HOME_PATH, investigationPath, navigate, PLAN_PATH, useRoute } from './router.ts'

const MAX_QUESTION_LENGTH = 4000

export default function App() {
  const queryClient = useQueryClient()
  const { route, path, focusRequest } = useRoute()
  const repositories = useQuery({ queryKey: ['repositories'], queryFn: listRepositories })

  // If this cannot be loaded the form stays usable; the server decides what is allowed anyway.
  const me = useQuery({ queryKey: ['me'], queryFn: getMe })
  const mayAsk = me.data?.canStartWork ?? true
  const leave = useMutation({ mutationFn: signOut, onSuccess: () => window.location.reload() })
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [question, setQuestion] = useState('')

  // One thing is shown at a time: the question form, planning from an issue, or the history.
  // The address says which (ADR-021). The planner is created when first opened and then kept,
  // so its place in the issue list survives a look at another side; the state of a question
  // lives here, so it survives too.
  const side =
    route.name === 'home' || route.name === 'investigation'
      ? 'question'
      : route.name === 'plan' || route.name === 'plan-item'
        ? 'plan'
        : route.name
  const [plannerOpened, setPlannerOpened] = useState(side === 'plan')
  if (side === 'plan' && !plannerOpened) {
    setPlannerOpened(true)
  }


  // After a change of page the keyboard goes to the new page's heading.
  useEffect(() => {
    if (focusRequest > 0) {
      focusPageHeading()
    }
  }, [focusRequest])

  // Asking only queues the investigation; a worker runs it. Poll until it reaches a final state.
  // The investigation on the page is the one the address names; one that was just asked gets
  // its address at once, so a reload or a shared link shows the same thing.
  const ask = useMutation({
    mutationFn: askQuestion,
    onSuccess: (created) => {
      const address = investigationPath(created.investigationId)
      if (address) {
        queryClient.setQueryData(['investigation', created.investigationId], created)
        navigate(address, { focus: false })
      }
    },
  })
  const routeInvestigationId = route.name === 'investigation' ? route.id : null
  const askedId = routeInvestigationId ?? ask.data?.investigationId
  const polled = useQuery({
    queryKey: ['investigation', askedId],
    queryFn: () => getInvestigation(askedId!),
    enabled: askedId !== undefined,
    initialData: () => (ask.data?.investigationId === askedId ? ask.data : undefined),
    staleTime: Infinity,
    refetchInterval: (query) =>
      query.state.data && !isFinished(query.state.data) ? POLL_INTERVAL_MS : false,
  })
  const cancel = useMutation({ mutationFn: cancelJob })

  // Leaving an investigation's address for the empty form (Back, or another side) forgets the
  // question that was asked here, so the form is not shown with a result its address does not name.
  const shownInvestigationId = useRef(routeInvestigationId)
  const resetAsk = ask.reset
  useEffect(() => {
    if (shownInvestigationId.current !== null && routeInvestigationId === null) {
      resetAsk()
    }
    shownInvestigationId.current = routeInvestigationId
  }, [routeInvestigationId, resetAsk])

  const investigation = ask.isPending ? undefined : polled.data
  const working = ask.isPending || (investigation !== undefined && !isFinished(investigation))
  const investigationMissing = routeInvestigationId !== null && !ask.isPending && isNotFound(polled.error)
  const loadingInvestigation = askedId !== undefined && !ask.isPending && investigation === undefined && polled.isPending
  const loadFailed = askedId !== undefined && !ask.isPending && investigation === undefined && polled.isError

  // The planner reads the plan the address names; this only watches the same query, to know
  // when the answer was "no such plan" and the whole page is the not-found page.
  const routePlanId = route.name === 'plan-item' ? route.id : null
  const watchedPlan = useQuery({
    queryKey: ['plan', routePlanId],
    queryFn: () => getPlan(routePlanId!),
    enabled: false,
  })
  const planMissing = routePlanId !== null && isNotFound(watchedPlan.error)

  const missing = route.name === 'not-found' || investigationMissing || planMissing
  const showQuestion = side === 'question' && !missing
  const showPlan = side === 'plan' && !missing

  // Each side remembers its last address, so switching sides comes back to the same item.
  // An address that turned out to name nothing is not remembered.
  const lastPath = useRef({ question: HOME_PATH, plan: PLAN_PATH })
  useEffect(() => {
    if (side === 'question') {
      lastPath.current.question = missing ? HOME_PATH : path
    } else if (side === 'plan') {
      lastPath.current.plan = missing ? PLAN_PATH : path
    }
  }, [side, path, missing])

  const indexed = (repositories.data ?? []).filter((repository) => repository.latestSnapshot !== null)
  const selected = indexed.find((repository) => repository.id === selectedId) ?? indexed[0]
  const canAsk = selected !== undefined && question.trim().length > 0 && !working && mayAsk

  function submit(event: FormEvent) {
    event.preventDefault()
    if (!canAsk || !selected.latestSnapshot) {
      return
    }
    cancel.reset()
    ask.mutate({
      repositoryId: selected.id,
      snapshotId: selected.latestSnapshot.id,
      question: question.trim(),
      // A fresh key per submission: a retried or double-sent request cannot create a duplicate.
      idempotencyKey: crypto.randomUUID(),
    })
  }

  return (
    <main>
      <header>
        <h1>TRACE</h1>
        <p className="tagline">Ask about a codebase. Check the evidence.</p>
        {me.data?.user && (
          <p className="account">
            Signed in as {me.data.user.login}.{' '}
            <button type="button" className="link" onClick={() => leave.mutate()} disabled={leave.isPending}>
              Sign out
            </button>
          </p>
        )}
        {me.data && !me.data.user && me.data.signInAvailable && (
          <p className="account">
            <a href="/api/v1/auth/login">Sign in with GitHub</a> to use your own repositories
            {mayAsk ? '.' : ' and to ask questions.'}
          </p>
        )}
      </header>

      {repositories.isPending && <p role="status">Loading repositories…</p>}

      {repositories.isError && (
        <p role="alert">
          The TRACE API could not be reached. Start it, then reload this page.
        </p>
      )}

      {repositories.isSuccess && repositories.data.length > 0 && (
        <nav className="modes" aria-label="What to do">
          <button type="button" aria-pressed={side === 'question'} onClick={() => navigate(lastPath.current.question)}>
            Question about the code
          </button>
          <button type="button" aria-pressed={side === 'plan'} onClick={() => navigate(lastPath.current.plan)}>
            Plan from an issue
          </button>
          <button type="button" aria-pressed={side === 'history'} onClick={() => navigate(HISTORY_PATH)}>
            History
          </button>
        </nav>
      )}

      {missing && <NotFound />}

      {showQuestion && <PageTitle title={routeInvestigationId === null ? null : 'Question'} />}

      {repositories.isSuccess && indexed.length === 0 && (showQuestion || repositories.data.length === 0) && (
        <p role="status">
          {repositories.data.length === 0
            ? 'No repository is available yet. Sign in with GitHub and install the TRACE app on a repository.'
            : 'No repository has been indexed yet. Index one below to ask about it.'}
        </p>
      )}

      {showQuestion && selected && (
        <h2 data-page-heading tabIndex={-1}>
          Ask a question
        </h2>
      )}

      {showQuestion && selected && (
        <form onSubmit={submit}>
          <label htmlFor="repository">Repository</label>
          <select
            id="repository"
            value={selected.id}
            onChange={(event) => setSelectedId(event.target.value)}
            disabled={working}
          >
            {indexed.map((repository) => (
              <option key={repository.id} value={repository.id}>
                {describe(repository)}
              </option>
            ))}
          </select>

          <label htmlFor="question">Question</label>
          <textarea
            id="question"
            rows={3}
            maxLength={MAX_QUESTION_LENGTH}
            placeholder="Why does the first page of the task list skip results?"
            value={question}
            onChange={(event) => setQuestion(event.target.value)}
            disabled={working}
          />

          <button type="submit" disabled={!canAsk}>
            Ask
          </button>
        </form>
      )}

      {showQuestion && ask.isError && <p role="alert">The question could not be asked. {ask.error.message}</p>}

      {showQuestion && loadingInvestigation && <p role="status">Loading the investigation…</p>}

      {showQuestion && loadFailed && (
        <>
          <p role="alert">The investigation could not be loaded. {polled.error.message}</p>
          <button type="button" className="secondary" onClick={() => void polled.refetch()}>
            Try again
          </button>
        </>
      )}

      {showQuestion && working && (
        <section className="working" aria-label="Investigation in progress">
          {investigation && routeInvestigationId !== null && <p className="asked">{investigation.question}</p>}
          <p role="status">
            {investigation?.state === 'running' ? 'Looking through the code…' : 'Waiting for a worker…'}
          </p>
          {investigation?.jobId && (
            <button
              type="button"
              className="secondary"
              disabled={cancel.isPending || cancel.isSuccess}
              onClick={() => cancel.mutate(investigation.jobId!)}
            >
              {cancel.isSuccess ? 'Cancelling…' : 'Cancel'}
            </button>
          )}
          {cancel.isError && <p role="alert">The investigation could not be cancelled. {cancel.error.message}</p>}
          {polled.isError && (
            <p role="alert">Lost contact with the API. The investigation continues in the background.</p>
          )}
          {investigation && <Steps investigationId={investigation.investigationId} live />}
        </section>
      )}

      {showQuestion && investigation && !working && <InvestigationView investigation={investigation} />}

      {plannerOpened && repositories.isSuccess && (
        <div hidden={!showPlan}>
          <IssuePlanner
            repositories={repositories.data}
            mayStart={mayAsk}
            active={showPlan}
            planId={routePlanId}
            onNavigate={navigate}
          />
        </div>
      )}

      {side === 'history' && repositories.isSuccess && <History repositories={repositories.data} me={me.data} />}

      {repositories.isSuccess && repositories.data.length > 0 && (
        <div hidden={!showQuestion && !showPlan}>
          <RepositoryList repositories={repositories.data} mayIndex={mayAsk} />
        </div>
      )}

      <footer>
        <ApiStatus />
      </footer>
    </main>
  )
}

function describe(repository: Repository): string {
  const snapshot = repository.latestSnapshot
  return snapshot
    ? `${repository.owner}/${repository.name} at ${snapshot.commitSha.slice(0, 12)}`
    : `${repository.owner}/${repository.name}`
}
