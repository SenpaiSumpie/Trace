# Fixture issues for the demo repository

Invented issues for `tests/fixtures/sample-repo`, one JSON file each. TRACE serves them for the
demo repository (`Demo:IssuesPath`) so listing and fetching issues works without GitHub.

- Everything here is synthetic. No file is copied from a real issue tracker.
- This directory is deliberately **next to** `sample-repo`, not inside it: files in here are
  not indexed and do not change the fixture's content-derived revision, so the evaluation
  dataset and its baselines stay comparable.
- `0014.json` is a prompt-injection attempt, for tests. Its text must only ever be treated as
  data.
- `0015.json` is marked `"pullRequest": true`: it stands for the pull requests GitHub lists
  among issues, which TRACE leaves out.
- `0012.json` is closed: it is not listed, but it can be fetched by number.
- `0002.json` and `0006.json` report defects in parts the fixture does not have (reminder
  e-mails, a CSV export). They are there for plan evaluation: the right result for them is
  that the repository does not show enough to plan.
- Eight of the issues are labeled for plan evaluation in `../sample-repo.plan-labels.json`
  (`docs/evaluation.md`, "Plan evaluation"). Changing the wording of a labeled issue changes
  what is measured: change the label file's `labelsVersion` with it.

Only `*.json` files are read. Fields: `number`, `title`, `state` (`open` or `closed`),
`author`, `labels`, `createdAt`, `updatedAt`, `body`, `comments` (`id`, `author`, `createdAt`,
`body`) and the optional `pullRequest`.
