# Implementation plan for issue 3

> Exported from TRACE. Text in code formatting (in backticks, or in a fenced block) is
> quoted from the issue, the model or the repository. It is untrusted: TRACE did not
> write it, and it may be wrong or written to mislead. Everything outside code
> formatting was written by TRACE.

## Plan record

- Repository: `trace-demo/sample-repo`
- Issue number: 3
- Issue title when the plan was made: `First page of the task list skips the oldest tasks`
- Pinned commit: `0123456789abcdef0123456789abcdef01234567`
- Outcome: planned
- Model: `claude-test-model`
- Prompt version: `plan-prompt-v1`
- Plan schema: `plan-v1`
- Plan ID: `01938a00-0000-7000-8000-0000000000a1`
- Investigation ID: `01938a00-0000-7000-8000-0000000000b2`
- Requested at: 2026-11-23T10:00:00Z
- Reviewed: yes, marked in TRACE at 2026-11-23T12:30:45Z

## How to read this

- Evidence labels (E1, E2, ...) point to the entries under "Evidence". TRACE verified each one:
  the lines exist at the pinned commit and were retrieved during the run that made this plan.
- TRACE does not check that the cited lines support the sentence that cites them.
- An item marked "not backed by evidence" is the model's unsupported statement. Unknowns and
  the reasons given for new files are stored without evidence.
- "Reviewed" means a person acknowledged reading the plan in TRACE. It is not an approval,
  and it changed nothing on GitHub.

## Summary

`The list skips a full page on page 1; skip (Page - 1) pages instead.`

Evidence: E1.

## Steps

1. `Add a failing test for the first page.` (evidence: E2)
2. `Skip (Page - 1) * PageSize items in List.` (evidence: E1, E3)

## Files to change

- modify `src/TaskBoard.Api/Tasks/TaskRepository.cs`: `The skip is computed here.` (evidence: E1)
- create `tests/TaskBoard.Tests/TaskListPagingTests.cs`: `A file for the paging tests.` (not backed by evidence: the model's unsupported statement)
- delete `src/TaskBoard.Api/Tasks/PageRequest.cs`: `Folded into the repository.` (evidence: E3)

## Risks

- `Clients that ask for page 0 will change behaviour.` (evidence: E1)

## Verification

- `Run the repository tests.` (evidence: E2)

## Unknowns

- `Whether any client asks for page 0.` (not backed by evidence: the model's unsupported statement)

## Removed by validation

TRACE removed these parts of the model's proposal because it could not verify them.
They are not in this plan.

- Steps: 1
- Files to change: 2
- Risks: 0
- Verification entries: 0
- Unknowns: 3
- Citations discarded: 4
- Summary replaced by a fixed sentence: no

## Evidence

### E1: `src/TaskBoard.Api/Tasks/TaskRepository.cs`, lines 24 to 26

```
        var skipped = request.Page * request.PageSize;
        return tasks.Skip(skipped)
            .Take(request.PageSize);
```

### E2: `tests/TaskBoard.Tests/TaskRepositoryTests.cs`, lines 8 to 9

```
    [Fact]
    public void First_page()
```

### E3: `src/TaskBoard.Api/Tasks/PageRequest.cs`, line 5

```
public sealed record PageRequest(int Page, int PageSize);
```
