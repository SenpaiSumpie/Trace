import { useQuery } from '@tanstack/react-query'
import { type Evidence, type Investigation, listEvents, type Usage } from './api.ts'

export const POLL_INTERVAL_MS = 500

const failureReasons: Record<string, string> = {
  invalid_model_output: 'The model did not return a usable answer, so nothing is shown.',
  model_error: 'The language model could not be reached.',
  worker_interrupted: 'The worker stopped repeatedly before it could finish.',
  timed_out: 'It took too long.',
  internal_error: 'Something went wrong inside TRACE.',
}

export function InvestigationView({ investigation }: { investigation: Investigation }) {
  const result = investigation.result
  const usage = investigation.usage

  return (
    <article className="investigation" aria-labelledby="result-heading">
      <p className="asked">{investigation.question}</p>

      {investigation.state === 'failed' && (
        <>
          <h2 id="result-heading">The investigation failed</h2>
          <p role="alert">
            {failureReasons[investigation.errorCode ?? ''] ?? 'It stopped before producing a result.'} Ask
            again to retry.
          </p>
        </>
      )}

      {investigation.state === 'cancelled' && (
        <>
          <h2 id="result-heading">The investigation was cancelled</h2>
          <p>Ask again to retry.</p>
        </>
      )}

      {result?.outcome === 'insufficient_evidence' && (
        <>
          <h2 id="result-heading">Not enough evidence</h2>
          <p className="answer">{result.answer}</p>
        </>
      )}

      {result?.outcome === 'answered' && (
        <>
          <h2 id="result-heading">Answer</h2>
          <p className="answer">{result.answer}</p>

          <h3>Evidence</h3>
          <p className="hint">
            Each passage was read from the indexed snapshot. Check that it supports the answer.
          </p>
          <ol className="evidence-list">
            {result.evidence.map((evidence) => (
              <li key={`${evidence.path}:${evidence.startLine}-${evidence.endLine}`}>
                <EvidencePassage evidence={evidence} />
              </li>
            ))}
          </ol>
        </>
      )}

      {result && result.unknowns.length > 0 && (
        <>
          <h3>Not settled by the evidence</h3>
          <ul>
            {result.unknowns.map((unknown) => (
              <li key={unknown}>{unknown}</li>
            ))}
          </ul>
        </>
      )}

      {result && result.limitations.length > 0 && (
        <>
          <h3>Limits of this result</h3>
          <ul>
            {result.limitations.map((limitation) => (
              <li key={limitation}>{limitation}</li>
            ))}
          </ul>
        </>
      )}

      <Steps investigationId={investigation.investigationId} live={false} />

      <p className="provenance">
        Read at commit <code>{investigation.commitSha.slice(0, 12)}</code>
        {investigation.modelId && (
          <>
            {' '}
            by model <code>{investigation.modelId}</code>
          </>
        )}
        {investigation.promptVersion && (
          <>
            {' '}
            with prompt <code>{investigation.promptVersion}</code>
          </>
        )}
        .{usage && usage.inputTokens + usage.outputTokens > 0 && ` ${describeUsage(usage)}`}
      </p>
    </article>
  )
}

/** Tokens are what the model provider bills for; cached input is billed at a fraction of the normal price. */
function describeUsage(usage: Usage): string {
  const count = (value: number) => value.toLocaleString('en-US')
  const cached = usage.cacheReadTokens ?? 0
  return `Used ${count(usage.inputTokens)} input tokens${cached > 0 ? ` (${count(cached)} from cache)` : ''} and ${count(usage.outputTokens)} output tokens.`
}

function EvidencePassage({ evidence }: { evidence: Evidence }) {
  const lines = evidence.excerpt.split('\n')

  return (
    <figure className="passage">
      <figcaption>
        <span className="location">
          {evidence.path}, lines {evidence.startLine}–{evidence.endLine}
        </span>
        {evidence.supports && <span className="supports">{evidence.supports}</span>}
      </figcaption>
      <pre>
        {lines.map((line, index) => (
          <span className="line" key={evidence.startLine + index}>
            <span className="line-number">{evidence.startLine + index}</span>
            <code>{line || ' '}</code>
          </span>
        ))}
      </pre>
    </figure>
  )
}

/** The steps taken so far. While `live`, the list is refreshed as the investigation runs. */
export function Steps({ investigationId, live }: { investigationId: string; live: boolean }) {
  const events = useQuery({
    queryKey: ['investigation-events', investigationId, live ? 'live' : 'final'],
    queryFn: () => listEvents(investigationId),
    refetchInterval: live ? POLL_INTERVAL_MS : false,
  })

  return (
    <section aria-labelledby="steps-heading">
      <h3 id="steps-heading">What TRACE did</h3>
      {events.isPending && <p role="status">Loading the steps…</p>}
      {events.isError && <p role="alert">The steps could not be loaded. Reload the page to try again.</p>}
      {events.data && (
        <ol className="steps">
          {events.data.map((event) => (
            <li key={event.seq} data-event-type={event.eventType}>
              {event.summary}
            </li>
          ))}
        </ol>
      )}
    </section>
  )
}
