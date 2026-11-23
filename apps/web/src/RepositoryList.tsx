import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'
import { getJob, isJobFinished, startIndexing, type Repository } from './api.ts'
import { POLL_INTERVAL_MS } from './InvestigationView.tsx'

/** Every repository the caller can see, with a way to index each at its latest commit. */
export function RepositoryList({ repositories, mayIndex }: { repositories: Repository[]; mayIndex: boolean }) {
  return (
    <section className="repositories">
      <h2>Repositories</h2>
      <p className="hint">
        Indexing reads a repository at its latest commit so questions can be asked about it. Index again after
        the code changes.
      </p>
      <ul className="repository-list">
        {repositories.map((repository) => (
          <RepositoryRow key={repository.id} repository={repository} mayIndex={mayIndex} />
        ))}
      </ul>
    </section>
  )
}

function RepositoryRow({ repository, mayIndex }: { repository: Repository; mayIndex: boolean }) {
  const queryClient = useQueryClient()
  const fullName = `${repository.owner}/${repository.name}`

  // Indexing only queues a job; a worker runs it. Poll the job until it reaches a final state.
  const start = useMutation({ mutationFn: () => startIndexing(repository.id) })
  const jobId = start.data?.id
  const polled = useQuery({
    queryKey: ['job', jobId],
    queryFn: () => getJob(jobId!),
    enabled: jobId !== undefined,
    initialData: start.data,
    staleTime: Infinity,
    refetchInterval: (query) => (query.state.data && !isJobFinished(query.state.data) ? POLL_INTERVAL_MS : false),
  })

  const job = start.isPending ? undefined : polled.data
  const working = start.isPending || (job !== undefined && !isJobFinished(job))
  const succeeded = job?.status === 'succeeded'

  // The new snapshot is what the question form should offer next.
  useEffect(() => {
    if (succeeded) {
      void queryClient.invalidateQueries({ queryKey: ['repositories'] })
    }
  }, [succeeded, queryClient])

  const action = repository.latestSnapshot ? 'Index again' : 'Index'

  return (
    <li>
      <div className="repository">
        <span className="location">{fullName}</span>
        <span className="supports">
          {repository.latestSnapshot
            ? `Indexed at commit ${repository.latestSnapshot.commitSha.slice(0, 12)}`
            : 'Not indexed yet'}
        </span>
      </div>
      <button
        type="button"
        className="secondary"
        aria-label={`${action} ${fullName}`}
        disabled={working || !mayIndex}
        onClick={() => start.mutate()}
      >
        {action}
      </button>
      {working && (
        <p role="status">
          {job?.status === 'running' ? 'Indexing…' : 'Waiting for a worker…'}
          {job && job.attempts > 1 && ` Attempt ${job.attempts} of ${job.maxAttempts}.`}
        </p>
      )}
      {start.isError && <p role="alert">Indexing could not be started. {start.error.message}</p>}
      {polled.isError && working && (
        <p role="alert">Lost contact with the API. Indexing continues in the background.</p>
      )}
      {job?.status === 'failed' && (
        <p role="alert">Indexing failed. Nothing was changed; the last index is still in use. Try again.</p>
      )}
      {job?.status === 'cancelled' && <p role="status">Indexing was cancelled.</p>}
      {succeeded && <p role="status">Indexing finished.</p>}
    </li>
  )
}
