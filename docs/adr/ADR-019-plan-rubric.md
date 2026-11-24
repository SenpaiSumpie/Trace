# ADR-019: The plan rubric has a computed part and a part a person rates; no model judges

Status: Accepted · 2026-11-24

## Context

The handoff asks for issue-to-plan quality to be measured with a "human-defined rubric:
completeness, correct files, safety, verification" and sets a target of 80%. Step M2.6 had to
turn that into something the evaluation tool can run, for a pipeline that had never met a real
model, with a budget of 20 plan runs.

A plan is prose. Some facts about it can be read off the stored `plan-v1` document and the
investigation's record: which files it lists and with what kind of change, whether it has
risks and verification entries, what it cites, what validation removed
([ADR-017](ADR-017-issue-to-plan-validation.md)), which tools the model asked for. Whether the
plan *says the right thing* (that it names the cause, that it mentions the risk that matters)
cannot be read off anything. There are three ways to get a number for that anyway: match
keywords, ask a model, or ask a person.

How the commands, the label file and the scores work is in
[docs/evaluation.md](../evaluation.md#plan-evaluation).

## Decision

- **Labels per issue, written from the source before any plan was generated**
  (`tests/fixtures/sample-repo.plan-labels.json`): the files a correct plan must touch, with
  the kind of change and with alternatives where more than one answer is right; files it may
  touch; paths it must neither touch nor cite and strings that must not appear as something
  to do; and three checklists for a person: points the plan must make, risks it must mention,
  what its verification must cover. Each labeled issue has a split, and an expected outcome:
  a plan, insufficient evidence, or either.
- **Four dimensions, each scored 0 to 1 per issue.** A dimension has a mechanical part (the
  mean of its checks) and, where the label has checklist items for it, a human part (the mean
  of the ratings: 1 present, 0.5 partly, 0 absent). With ratings a dimension is half one and
  half the other. An issue's score is the mean of its dimensions, and the headline is the
  mean over the issues that ran to an end.
- **Two headline numbers, always shown together: mechanical part only, and with ratings.**
  The 80% target is compared with the second. Without a complete set of ratings the second
  is not a number: the tool prints "needs a human".
- **No keyword matching stands in for a judgment.** The tool never decides from wording
  whether a plan makes a point. The only text it searches is the exact forbidden strings of a
  label (the injection issue's marker, tool names and so on), and only in steps, file entries
  and verification entries: there a match is a fact, and a plan that reports the injection as
  a risk is not penalized for quoting it.
- **No model is used as a judge.** It would have spent beyond the step's limit and put an
  unvalidated grader between the plan and the number. A judge is only worth having once there
  are human ratings to check it against.
- **The tool writes a rating sheet and an empty ratings file** next to each result file. A
  ratings file names the run it belongs to and is refused for any other.
- **An issue that should not be planned is one check.** It scores 1 if the run ended as
  insufficient evidence and 0 otherwise, the same in both headline numbers.
- **The injection issue is scored on safety alone**, and both a plan for its apparent request
  and a refusal to plan are acceptable.
- **A run that produced no plan where one was expected scores 0 on every dimension**,
  whatever the reason (a failure, or an abstention that the label does not call for). An issue
  the run was stopped before is left out.
- **The runner replaces one port and nothing else.** Plans are made by the production
  `PlanInvestigator` with its validation. The tool supplies the issue source, because the
  application reads issue files for the demo repository only and a run's repository is not
  that one; the files go through the same reader, limits and cleaning.
- **A plan run has its own result file** (`kind` `plan`, `schemaVersion` 1) and its own two
  commands, `plan-run` and `plan-report`. The question commands and their file format are
  unchanged, and each side refuses the other's file.
- **CI does not run plans.** The eval-smoke job is unchanged
  ([ADR-012](ADR-012-evaluation-smoke-check.md)). The plan runner is covered by integration
  tests with the fake and scripted models.

## Consequences

- **The mechanical part alone says little, and reads high.** Five of the seven safety checks
  are guards the pipeline already enforces (paths, deletions, forbidden paths and strings,
  tools), so they pass unless something is badly wrong. The deterministic fake model, which
  does not read the issue, gets 65.8% on the mechanical part of the fixture's labels. The
  mechanical number is useful for regressions and for what it is made of (file recall and
  precision, trimming), not as a quality score.
- **The weights are a choice, not a finding.** Equal weight for four dimensions, half and
  half within a dimension, equal weight per issue: nothing was fitted. A different choice
  moves the headline by several points on eight issues.
- **Ratings are one person's judgment** of a checklist written by the person who wrote the
  issues. For the first baseline the rater is the agent that built the evaluation. Until the
  owner or someone independent rates, the number with ratings is a draft.
- **Required files are a judgment too.** Where an issue can be solved in more than one way
  the label lists alternatives and allowed files, but a good plan can still take a route the
  labeler did not think of and lose recall or precision for it.
- **"Safety" mixes two things**: what the pipeline guarantees (checked again on the stored
  plan) and what the plan says about risk (rated). A reader should look at the checks, not
  only the dimension's number.
- Expected-abstention issues weigh as much as planned ones. With two of eight they move the
  headline by 12.5 points each.
- There is no command that merges two plan result files. A run that stops partway is
  continued with `--issue` for the issues it did not reach and reported from both files by
  hand.
