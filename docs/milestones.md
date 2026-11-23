# Milestones to v0.1

Six milestones take TRACE from where it is to a finished v0.1 as the
[handoff](TRACE_ENGINEERING_HANDOFF.md) defines it (Section 2). They have no dates: each one
is finished when every box under "Done when" is ticked, and not before.

**Azure comes in at M5.** Everything before it runs on your machine at no monthly cost. The
[Azure gate](#the-azure-gate) below says exactly what must be true first.

| | Milestone | Status |
|---|---|---|
| M0 | Working slice | Done |
| M1 | Measured answers | Next |
| M2 | Issue to plan | Not started |
| M3 | A product you can use daily | Not started |
| M4 | Safe to put online | Not started |
| M5 | Live on Azure, with a demo | Not started; needs the Azure gate |

Sizes are relative to each other (S, M, L), not estimates of days.

M1 to M4 are broken into 25 steps in [plan/plan.json](plan/plan.json), which the step
orchestrator works through one at a time. What it needs from you collects in
`plan/owner-actions.md`.

## M0: Working slice — done

Sign in with GitHub, index a repository, ask a question, get an answer with checked evidence.

- [x] Sign in with GitHub; a GitHub App with read-only access to chosen repositories
- [x] Indexing pinned to a commit, from the page (Index button)
- [x] Search by words and by meaning, combined
- [x] Answers whose every citation is checked against the indexed source
- [x] An honest "not enough evidence" result
- [x] Background jobs that survive restarts, retry, and can be cancelled
- [x] Real models (Claude, Voyage) with spending limits
- [x] Tracing and a correlation ID from request to job to model and tool calls
- [x] Tests and CI; container images and an Azure template (never deployed)
- [x] One real end-to-end run on a real repository (2026-11-22)

## M1: Measured answers — size M

Today the quality of answers rests on a handful of runs someone looked at. This milestone
replaces that with numbers that can be re-run after any change. It comes first because every
later change to prompts, search or cost needs it.

Done when:

- [ ] A command runs a set of labeled questions against the real models and stores, per
      question: the answer, evidence, tool calls, tokens, cost and time, with the commit,
      index version, model and prompt version
      (the command exists and stores all of this, see [evaluation.md](evaluation.md); it has
      only been run with the fake models, so this box stays open until a real-model run)
- [ ] The fixture repository has at least 25 labeled questions, split into development and
      held-out sets, including at least 5 that cannot be answered
- [ ] A second labeled set of at least 15 questions exists for one real repository of yours
- [ ] A baseline is recorded for each handoff metric that applies to questions: retrieval
      recall at 10, citation validity, correct "not enough evidence" results, cost and time
      (the metrics are defined and computed by `report`, see
      [evaluation.md](evaluation.md#metrics); no real-model run exists, so there is no
      baseline yet)
- [ ] Citation validity is at least 95% and recall at 10 is at least 85% on the held-out
      set, or the shortfall is written down with its cause
- [ ] A small smoke subset runs in CI with the fake models
- [x] Results from two runs can be compared question by question
      (`compare`, see [evaluation.md](evaluation.md#compare-two-runs); only ever used on
      fake-model runs)

## M2: Issue to plan — size L

The second half of the v0.1 journey: pick a GitHub issue and get a cited implementation plan.

Done when:

- [ ] The GitHub App asks for read-only access to issues, and TRACE can list and fetch them
      for a repository the user can see (you will need to approve the new permission)
- [ ] An issue-to-plan investigation produces a structured plan: steps, files to change,
      risks, how to verify, and what is unknown, each tied to cited source lines
- [ ] Issue text is treated as untrusted, like source: tests show it cannot add tools,
      reach other repositories or get uncited claims into a plan
- [ ] Plans are stored, shown in the web app, and can be exported as Markdown
- [ ] A plan can be marked as reviewed; this changes nothing in GitHub
- [ ] The fixture has labeled issues, and plans score at least 80% on a written rubric, or
      the shortfall is written down with its cause
- [ ] Nothing TRACE does can write to a repository, open a pull request or edit an issue

## M3: A product you can use daily — size M

Right now the page shows one question at a time and forgets it on reload.

Done when:

- [ ] Past investigations are listed, and each has its own address that can be reopened
- [ ] Running index jobs and investigations are still shown after a reload
- [ ] A repository page shows what is indexed: commit, date, file and passage counts, and
      what was skipped and why
- [ ] Lists are paged; the API has a published OpenAPI description
- [ ] A user can delete an investigation and remove a repository's indexed data
- [ ] Every screen works from the keyboard and at phone width, with clear empty, loading
      and error states
- [ ] Browser tests cover sign-in (against a stub), indexing, a question and a plan

## M4: Safe to put online — size M

The checks that matter once strangers can reach it and it costs money while idle.

Done when:

- [ ] Spending limits apply per user and per repository, not only overall, and fail closed
- [ ] Rate limits apply per signed-in user
- [ ] The security test suite covers every "must-have defense" in handoff Section 10, and
      each one has a named test
- [ ] Traces and metrics have been sent to a real collector locally, and the trace of one
      question has been read end to end
- [ ] Baseline numbers exist for API response time, queue wait, indexing time and
      end-to-end question time (median and 95th percentile)
- [ ] A retention rule exists for investigations, events and logs, and it is enforced
- [ ] The Azure template uses a private network for the database and a secret store, and a
      pipeline can deploy it without manual steps
- [ ] A restore from a database backup has been rehearsed locally
- [ ] A short runbook covers: a failed deployment, a leaked key, a provider outage, and
      runaway spending

## The Azure gate

Bring in Azure when all of these are true:

1. M1 to M4 are done.
2. You have used TRACE locally for at least a week on a real repository and still want it.
3. You have run the template through Azure's pricing calculator and accept the monthly cost.
   My rough figure is $40 to $110 a month; it is from memory, not a quote.
4. You have decided who may sign in. Today anyone with a GitHub account who installs the app
   can use your model budget.

One thing only Azure (or any public address) unlocks: automatic re-indexing when you push.
Until then, press Index again.

If you want a public demo sooner, the cheapest honest shortcut is to deploy after M2 with
sign-in limited to your own account. That skips most of M4, so it should not be shared.

## M5: Live on Azure, with a demo — size M

Done when:

- [ ] The first deployment works from the pipeline, and the problems it turned up are fixed
      in the template
- [ ] The GitHub App points at the public address, and a push re-indexes automatically
- [ ] Sign-in, indexing, a question and a plan all work on the deployed site
- [ ] Logs, traces and metrics are visible in Azure, with an alert for failures and one for
      spending
- [ ] One week of real cost is recorded: Azure, Claude and Voyage
- [ ] The README has a short case study and a demo recording (handoff Section 17)

That completes v0.1.

## After v0.1

The handoff names these and says not to build toward them early:

- **v0.2 Investigator:** logs, incident timelines, tracing a failure back to code
- **v0.3 Engineer:** proposed patches, tested in a sandbox, approved by a person
- **v0.4 Intelligence:** a map of the codebase, impact analysis, engineering memory
- **v1.0 Platform:** teams, several repositories, model comparison
