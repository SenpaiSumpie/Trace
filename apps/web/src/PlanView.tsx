import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useId, useState } from 'react'
import {
  ApiError,
  exportPlan,
  getPlan,
  type Plan,
  type PlanDocument,
  type PlanEvidence,
  type PlanTrimmed,
  reviewPlan,
  wordingFor,
} from './api.ts'
import { EvidencePassage, Provenance, Steps } from './InvestigationView.tsx'

// One plain sentence per fixed outcome name. The name itself is never shown, and no other
// text about a failure ever reaches this page.
const failureReasons: Record<string, string> = {
  issue_not_found: 'The issue no longer exists, or it is a pull request.',
  issues_permission_missing: 'The GitHub App is not allowed to read this repository’s issues.',
  issues_disabled: 'Issues are switched off for this repository on GitHub.',
  issues_unavailable: 'This repository has no source of issues.',
  github_rate_limited: 'GitHub was limiting requests.',
  github_unavailable: 'GitHub did not return the issue.',
  invalid_model_output: 'The model did not return a usable plan, so nothing is shown.',
  budget_exhausted: 'The spending limit for model calls was reached.',
  model_error: 'The language model could not be reached.',
  model_refused: 'The language model declined to answer.',
  worker_interrupted: 'The worker stopped repeatedly before it could finish.',
  timed_out: 'It took too long.',
  internal_error: 'Something went wrong inside TRACE.',
}

const issueCheckMessages: Record<string, string> = {
  unchanged: 'The issue’s title, body and first comments are the same as when this plan was made.',
  changed: 'The issue has changed since this plan was made, so the plan may be out of date. Start a new plan to use the current text.',
  unavailable: 'The issue could not be compared right now. Try again later.',
}

const changeKinds: Record<string, string> = {
  modify: 'Change',
  create: 'New file',
  delete: 'Delete',
}

export const UNBACKED = 'Not backed by evidence'

/** A finished plan run: the plan with its evidence, or why there is none. */
export function PlanView({ plan, mayReview }: { plan: Plan; mayReview: boolean }) {
  const document = plan.plan
  const labels = new Map((document?.evidence ?? []).map((evidence, index) => [evidence.id, `E${index + 1}`]))

  return (
    <div className="plan">
      {plan.state === 'failed' && (
        <>
          <h3>The plan could not be made</h3>
          <p role="alert">
            {wordingFor(failureReasons, plan.errorCode) ?? 'The run stopped before producing a plan.'} Nothing was
            stored as a plan. Go back to the issue to try again.
          </p>
        </>
      )}

      {plan.state === 'cancelled' && (
        <>
          <h3>The plan was cancelled</h3>
          <p>Nothing was stored as a plan. Go back to the issue to start again.</p>
        </>
      )}

      {plan.state === 'insufficient_evidence' && document && (
        <>
          <h3>Not enough evidence for a plan</h3>
          <p className="hint">
            TRACE did not find enough verified source to plan this change, so there are no steps. This is a
            result, not an error.
          </p>
          <p className="answer">{document.summary.text}</p>
          <Unknowns document={document} labels={labels} />
          <Limitations limitations={plan.limitations} />
          {plan.trimmed && <Trimmed trimmed={plan.trimmed} />}
          <section aria-label="Download">
            <DownloadButton plan={plan} />
          </section>
        </>
      )}

      {plan.state === 'planned' && document && (
        <>
          <p className="hint">
            Written by a model from the issue and the indexed source. Each evidence label (E1, E2, …) is a
            passage TRACE verified: the lines exist at the pinned commit and were read during this run. TRACE
            does not check that the lines support the sentence that cites them.
          </p>

          <h3>Summary</h3>
          <p className="answer">{document.summary.text}</p>
          <Citations citations={document.summary.citations} document={document} labels={labels} />

          <h3>Steps</h3>
          <ol className="plan-list">
            {document.steps.map((step, index) => (
              <li key={index}>
                <p className="statement">{step.text}</p>
                <Citations citations={step.citations} document={document} labels={labels} />
              </li>
            ))}
          </ol>

          <h3>Files to change</h3>
          {document.filesToChange.length === 0 ? (
            <p className="hint">The plan names no file.</p>
          ) : (
            <ul className="plan-list files">
              {document.filesToChange.map((file) => (
                <li key={file.path}>
                  <p className="file">
                    <span className="location">{file.path}</span>
                    <span className="change-kind" data-change={wordingFor(changeKinds, file.change) ? file.change : 'other'}>
                      {wordingFor(changeKinds, file.change) ?? file.change}
                    </span>
                  </p>
                  <p className="statement">{file.reason}</p>
                  <Citations citations={file.citations} document={document} labels={labels} />
                </li>
              ))}
            </ul>
          )}

          <StatementList title="Risks" empty="The plan names no risk." items={document.risks} document={document} labels={labels} />
          <StatementList
            title="How to verify"
            empty="The plan names no way to verify the change."
            items={document.verification}
            document={document}
            labels={labels}
          />
          <Unknowns document={document} labels={labels} />
          {plan.trimmed && <Trimmed trimmed={plan.trimmed} />}
          <Limitations limitations={plan.limitations} />

          <Review plan={plan} mayReview={mayReview} />

          <h3>Evidence</h3>
          <p className="hint">Every passage the plan cites, read from the indexed snapshot.</p>
          <ol className="evidence-list">
            {document.evidence.map((evidence) => (
              <li key={evidence.id}>
                <EvidencePassage evidence={evidence} label={labels.get(evidence.id)} />
              </li>
            ))}
          </ol>
        </>
      )}

      <Steps investigationId={plan.investigationId} live={false} />

      <Provenance commitSha={plan.commitSha} modelId={plan.modelId} promptVersion={plan.promptVersion} usage={plan.usage} />
    </div>
  )
}

type Labels = Map<string, string>

function StatementList({
  title,
  empty,
  items,
  document,
  labels,
}: {
  title: string
  empty: string
  items: PlanDocument['risks']
  document: PlanDocument
  labels: Labels
}) {
  return (
    <>
      <h3>{title}</h3>
      {items.length === 0 ? (
        <p className="hint">{empty}</p>
      ) : (
        <ul className="plan-list">
          {items.map((item, index) => (
            <li key={index}>
              <p className="statement">{item.text}</p>
              <Citations citations={item.citations} document={document} labels={labels} />
            </li>
          ))}
        </ul>
      )}
    </>
  )
}

function Unknowns({ document, labels }: { document: PlanDocument; labels: Labels }) {
  if (document.unknowns.length === 0) {
    return null
  }

  return (
    <>
      <h3>Not established</h3>
      <p className="hint">
        What the run could not settle. These are the model’s own statements; TRACE stores them without evidence.
      </p>
      <ul className="plan-list">
        {document.unknowns.map((unknown, index) => (
          <li key={index}>
            <p className="statement">{unknown.text}</p>
            <Citations citations={unknown.citations} document={document} labels={labels} />
          </li>
        ))}
      </ul>
    </>
  )
}

function Limitations({ limitations }: { limitations: string[] }) {
  if (limitations.length === 0) {
    return null
  }

  return (
    <>
      <h3>Limits of this result</h3>
      <ul>
        {limitations.map((limitation) => (
          <li key={limitation}>{limitation}</li>
        ))}
      </ul>
    </>
  )
}

/**
 * The evidence under one sentence. Without any, the sentence is marked as the model's
 * unsupported statement. With some, a button shows the cited source lines in place.
 */
function Citations({ citations, document, labels }: { citations: string[]; document: PlanDocument; labels: Labels }) {
  const [open, setOpen] = useState(false)
  const panelId = useId()
  const cited = citations
    .map((id) => document.evidence.find((evidence) => evidence.id === id))
    .filter((evidence): evidence is PlanEvidence => evidence !== undefined)

  if (cited.length === 0) {
    return <p className="unbacked">{UNBACKED}</p>
  }

  const names = cited.map((evidence) => labels.get(evidence.id)).join(', ')

  return (
    <div className="citations">
      <button
        type="button"
        className="link"
        aria-expanded={open}
        aria-controls={panelId}
        onClick={() => setOpen(!open)}
      >
        {open ? 'Hide' : 'Show'} evidence {names}
      </button>
      <div id={panelId} hidden={!open}>
        {open && (
          <ol className="evidence-list">
            {cited.map((evidence) => (
              <li key={evidence.id}>
                <EvidencePassage evidence={evidence} label={labels.get(evidence.id)} />
              </li>
            ))}
          </ol>
        )}
      </div>
    </div>
  )
}

function plural(count: number, one: string, many: string): string {
  return `${count} ${count === 1 ? one : many}`
}

/** What validation took out of the model's proposal. Only counts exist; the removed text is not kept in the plan. */
function Trimmed({ trimmed }: { trimmed: PlanTrimmed }) {
  const removed = [
    trimmed.steps > 0 && plural(trimmed.steps, 'step', 'steps'),
    trimmed.files > 0 && plural(trimmed.files, 'file', 'files'),
    trimmed.risks > 0 && plural(trimmed.risks, 'risk', 'risks'),
    trimmed.verification > 0 && plural(trimmed.verification, 'verification entry', 'verification entries'),
    trimmed.unknowns > 0 && plural(trimmed.unknowns, 'unknown', 'unknowns'),
    trimmed.citations > 0 && plural(trimmed.citations, 'cited location that failed the check', 'cited locations that failed the check'),
  ].filter((entry): entry is string => entry !== false)

  return (
    <>
      <h3>Removed by validation</h3>
      {removed.length === 0 && !trimmed.summaryReplaced ? (
        <p className="hint">Nothing: every entry of the model’s proposal passed the check.</p>
      ) : (
        <>
          <p className="hint">
            TRACE keeps an entry only when every location it cites exists at the pinned commit and was read
            during this run. These parts of the model’s proposal failed that check and are not in the plan:
          </p>
          <ul className="trimmed">
            {removed.map((entry) => (
              <li key={entry}>{entry}</li>
            ))}
            {trimmed.summaryReplaced && (
              <li>The model’s summary was not kept; a fixed sentence stands in its place.</li>
            )}
          </ul>
        </>
      )}
    </>
  )
}

function describeError(error: unknown): string {
  return error instanceof Error ? error.message : 'The request failed.'
}

/** Marking as reviewed, the download and the check for a changed issue. */
function Review({ plan, mayReview }: { plan: Plan; mayReview: boolean }) {
  const queryClient = useQueryClient()
  const headingId = useId()

  const review = useMutation({
    mutationFn: () => reviewPlan(plan.planId),
    onSuccess: (reviewed) => queryClient.setQueryData(['plan', plan.planId], reviewed),
    // Refused: the plan is not what this page thought it was. Read it again and show that.
    onError: () => void queryClient.invalidateQueries({ queryKey: ['plan', plan.planId] }),
  })
  const check = useMutation({ mutationFn: () => getPlan(plan.planId, true) })
  const checked = check.data?.issueCheck

  return (
    <section aria-labelledby={headingId} className="review">
      <h3 id={headingId}>Review and export</h3>

      {plan.reviewedAt ? (
        <p role="status" className="reviewed">
          Marked as reviewed on <time dateTime={plan.reviewedAt}>{new Date(plan.reviewedAt).toLocaleString()}</time>.
          This only records in TRACE that someone read the plan. It is not an approval, and nothing changed on
          GitHub.
        </p>
      ) : (
        <p className="hint">
          Marking as reviewed records in TRACE that you have read this plan. It is not an approval, and it
          changes nothing on GitHub.
        </p>
      )}

      <div className="actions">
        {!plan.reviewedAt && (
          <button type="button" disabled={!mayReview || review.isPending} onClick={() => review.mutate()}>
            Mark as reviewed
          </button>
        )}
        <DownloadButton plan={plan} />
        <button type="button" className="secondary" disabled={check.isPending} onClick={() => check.mutate()}>
          Check whether the issue changed
        </button>
      </div>

      {!plan.reviewedAt && !mayReview && <p role="status">Sign in to mark a plan as reviewed.</p>}

      {review.isError && (
        <p role="alert">
          {review.error instanceof ApiError && review.error.status === 409
            ? 'This plan cannot be marked as reviewed in its current state. '
            : 'The plan could not be marked as reviewed. '}
          {describeError(review.error)}
        </p>
      )}

      {check.isPending && <p role="status">Reading the issue again…</p>}
      {check.isError && <p role="alert">The issue could not be checked. {describeError(check.error)}</p>}
      {checked && (
        <p role="status" data-issue-check={wordingFor(issueCheckMessages, checked.status) ? checked.status : 'unavailable'}>
          {wordingFor(issueCheckMessages, checked.status) ?? issueCheckMessages.unavailable}
        </p>
      )}
    </section>
  )
}

/**
 * Downloads the server's Markdown export as a file. The text is saved, never shown: this page
 * renders the plan from its data, and the export stays what the server wrote.
 */
function DownloadButton({ plan }: { plan: Plan }) {
  const download = useMutation({
    mutationFn: async () => {
      const exported = await exportPlan(plan.planId)
      const address = URL.createObjectURL(exported.markdown)
      const link = window.document.createElement('a')
      link.href = address
      link.download = exported.fileName
      link.hidden = true
      window.document.body.append(link)
      link.click()
      link.remove()
      // The browser has taken the file by now; the address is only freed, a moment later.
      setTimeout(() => URL.revokeObjectURL(address), 1000)
      return exported.fileName
    },
  })

  return (
    <>
      <button type="button" className="secondary" disabled={download.isPending} onClick={() => download.mutate()}>
        Download as Markdown
      </button>
      {download.isSuccess && (
        <p role="status" className="action-note">
          Saved as {download.data}.
        </p>
      )}
      {download.isError && (
        <p role="alert" className="action-note">
          The plan could not be downloaded. {describeError(download.error)}
        </p>
      )}
    </>
  )
}
