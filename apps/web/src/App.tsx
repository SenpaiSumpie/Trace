import { useMutation, useQuery } from '@tanstack/react-query'
import { type FormEvent, useState } from 'react'
import {
  askQuestion,
  cancelJob,
  getInvestigation,
  getMe,
  isFinished,
  listRepositories,
  signOut,
  type Repository,
} from './api.ts'
import { ApiStatus } from './ApiStatus.tsx'
import { InvestigationView, POLL_INTERVAL_MS, Steps } from './InvestigationView.tsx'

const MAX_QUESTION_LENGTH = 4000

export default function App() {
  const repositories = useQuery({ queryKey: ['repositories'], queryFn: listRepositories })

  // If this cannot be loaded the form stays usable; the server decides what is allowed anyway.
  const me = useQuery({ queryKey: ['me'], queryFn: getMe })
  const mayAsk = me.data?.canStartWork ?? true
  const leave = useMutation({ mutationFn: signOut, onSuccess: () => window.location.reload() })
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [question, setQuestion] = useState('')

  // Asking only queues the investigation; a worker runs it. Poll until it reaches a final state.
  const ask = useMutation({ mutationFn: askQuestion })
  const askedId = ask.data?.investigationId
  const polled = useQuery({
    queryKey: ['investigation', askedId],
    queryFn: () => getInvestigation(askedId!),
    enabled: askedId !== undefined,
    initialData: ask.data,
    staleTime: Infinity,
    refetchInterval: (query) =>
      query.state.data && !isFinished(query.state.data) ? POLL_INTERVAL_MS : false,
  })
  const cancel = useMutation({ mutationFn: cancelJob })

  const investigation = ask.isPending ? undefined : polled.data
  const working = ask.isPending || (investigation !== undefined && !isFinished(investigation))

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

      {repositories.isSuccess && indexed.length === 0 && (
        <p role="status">
          No repository has been indexed yet. Start the API in Development to index the demo repository.
        </p>
      )}

      {selected && (
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

      {ask.isError && <p role="alert">The question could not be asked. {ask.error.message}</p>}

      {working && (
        <section className="working" aria-label="Investigation in progress">
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

      {investigation && !working && <InvestigationView investigation={investigation} />}

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