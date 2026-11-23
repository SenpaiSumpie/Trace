# Plan result schema

The result of an issue-to-plan investigation is stored as JSON in `plans.result_json`. This
page is the contract for that JSON. The code is `PlanResult` in
`src/Trace.Application/Plans/PlanResult.cs`; why it looks like this is in
[ADR-016](adr/ADR-016-plan-storage.md).

**Version: `plan-v1`.** The workflow that writes it is `PlanInvestigator`
([ADR-017](adr/ADR-017-issue-to-plan-validation.md)); the API that returns it is M2.4. So far
only the deterministic fake model and scripted test models have produced one.

## Example

A test parses this block, checks it against every rule below and writes it back unchanged
(`PlanResultTests.The_documented_example_is_a_valid_plan_and_round_trips`), so it cannot
drift from the code.

```json
{
  "schemaVersion": "plan-v1",
  "outcome": "planned",
  "summary": {
    "text": "The page size is read from the query string without an upper bound. Cap it where the request is parsed and cover the cap with a test.",
    "citations": ["0193a5c0-0000-7000-8000-000000000001"]
  },
  "steps": [
    {
      "text": "Add a failing test that asks for a page larger than the cap.",
      "citations": ["0193a5c0-0000-7000-8000-000000000002"]
    },
    {
      "text": "Clamp the requested size in PageRequest.",
      "citations": ["0193a5c0-0000-7000-8000-000000000001"]
    }
  ],
  "filesToChange": [
    {
      "path": "src/TaskBoard.Api/Tasks/PageRequest.cs",
      "change": "modify",
      "reason": "This is where the size is parsed.",
      "citations": ["0193a5c0-0000-7000-8000-000000000001"]
    },
    {
      "path": "tests/TaskBoard.Tests/PageRequestCapTests.cs",
      "change": "create",
      "reason": "A separate file for the new cap tests.",
      "citations": []
    }
  ],
  "risks": [
    {
      "text": "Callers that rely on large pages will receive fewer items than they asked for.",
      "citations": ["0193a5c0-0000-7000-8000-000000000001"]
    }
  ],
  "verification": [
    {
      "text": "Run the PageRequest tests.",
      "citations": ["0193a5c0-0000-7000-8000-000000000002"]
    }
  ],
  "unknowns": [
    {
      "text": "Whether any client depends on pages larger than the cap; the repository has no usage data.",
      "citations": []
    }
  ],
  "evidence": [
    {
      "id": "0193a5c0-0000-7000-8000-000000000001",
      "path": "src/TaskBoard.Api/Tasks/PageRequest.cs",
      "startLine": 12,
      "endLine": 14,
      "excerpt": "    public int Size { get; init; } = 20;\n\n    public int Skip => (Page - 1) * Size;"
    },
    {
      "id": "0193a5c0-0000-7000-8000-000000000002",
      "path": "tests/TaskBoard.Tests/PageRequestTests.cs",
      "startLine": 8,
      "endLine": 9,
      "excerpt": "    [Fact]\n    public void Default_page_size_is_twenty()"
    }
  ]
}
```

The excerpts above are invented for the example.

## Fields

Every field is required and none may be `null`. An empty list is written as `[]`. A field
that is not listed here makes the document invalid.

| Field | Type | Meaning |
|---|---|---|
| `schemaVersion` | string | Always `plan-v1` for this version. |
| `outcome` | string | `planned` or `insufficient_evidence`. |
| `summary` | statement | What the issue asks for and the shape of the change. |
| `steps` | statement[] | What to do, in the order to do it. The order of the array is the order of the steps. |
| `filesToChange` | file[] | Every file the plan says to modify, create or delete. Each path appears once. |
| `risks` | statement[] | What could go wrong or break. |
| `verification` | statement[] | How to check that the change works. |
| `unknowns` | statement[] | What the investigation could not establish. |
| `evidence` | evidence[] | Every source location the plan cites, once each. |

**statement**

| Field | Type | Meaning |
|---|---|---|
| `text` | string | Plain text. Not Markdown, not HTML: a reader must show it as text. |
| `citations` | string[] | IDs of entries in `evidence`. |

**file**

| Field | Type | Meaning |
|---|---|---|
| `path` | string | Repository-relative, forward slashes, no empty, `.` or `..` segment. |
| `change` | string | `modify`, `create` or `delete`. |
| `reason` | string | Why this file. Plain text. |
| `citations` | string[] | IDs of entries in `evidence`. |

**evidence**

| Field | Type | Meaning |
|---|---|---|
| `id` | string (UUID, lowercase with hyphens) | The ID of the stored evidence reference, `evidence_refs.id`. |
| `path` | string | The cited file, exactly as it is indexed. |
| `startLine`, `endLine` | integer | 1-based, inclusive. |
| `excerpt` | string | The cited lines as they are in the snapshot, joined with `\n`. |

## What a citation is

A citation is the ID of one entry in the plan's own `evidence` list. That entry is one row of
`evidence_refs`, the same table question answers use: it belongs to the plan's investigation,
points at a passage of the investigation's pinned snapshot, and holds the location and a hash
of the cited text. The commit is the investigation's snapshot; it is not repeated in the plan.

Three layers make a citation mean something:

1. **The workflow** ([ADR-017](adr/ADR-017-issue-to-plan-validation.md)) accepts a location
   only if it exists in the pinned snapshot and its lines were returned to the model during
   the same run, and reads the excerpt from the snapshot rather than from the model.
2. **This schema** refuses a plan in which a citation is not in `evidence`, an evidence entry
   is cited by nothing, or a claim that needs a citation has none.
3. **The database** refuses to finish a plan unless every string under a `citations` key and
   every `evidence[].id` is the ID of a code evidence row of the same investigation, whose
   foreign keys tie it to a passage of that investigation's snapshot. Evidence rows of a
   finished plan can no longer be changed or deleted on their own.

Only source code can be cited in `plan-v1`. The issue itself is not evidence.

## Rules

Which items must be cited:

| Item | Citations |
|---|---|
| each step, risk and verification entry | at least 1 |
| each file with `modify` or `delete` | at least 1 |
| each file with `create` | 0 or more (the file has no lines yet) |
| the summary, each unknown | 0 or more |

An item that needs a citation and has none cannot be stored. The workflow removes such an
item, and any item with a citation that failed, before it builds the document; it also
requires a cited summary, which this schema does not, and replaces one that fails with a fixed
sentence. A proposal with no valid citation or no surviving step is stored as
`insufficient_evidence` ([ADR-017](adr/ADR-017-issue-to-plan-validation.md)). What was removed
is not in this document: it is counted in the investigation's result and listed in its events.

Outcome:

- `planned` has at least one step.
- `insufficient_evidence` has a summary and may have unknowns; `steps`, `filesToChange`,
  `risks`, `verification` and `evidence` are empty.

Limits (characters are UTF-16 code units):

| What | Limit |
|---|---|
| `summary.text`, each step's `text` | 1 to 2,000 characters |
| a file's `reason`, each risk, verification entry and unknown | 1 to 1,000 characters |
| steps | 30 |
| files | 50 |
| risks, verification entries, unknowns | 20 each |
| citations on one item | 10, no ID twice |
| evidence entries | 50 |
| lines in one evidence entry | 200 |
| all excerpts together | 1,000,000 characters |
| a path | 1,024 characters |
| the whole document | 2,000,000 characters of JSON |

No text is blank, and none contains a NUL character (PostgreSQL cannot store one).

What this schema does **not** check, and the workflow does: that a cited location exists in
the snapshot and was retrieved in the run; that a `modify` or `delete` path exists in the
snapshot and a `create` path does not. Nothing checks that the cited lines support the sentence that
cites them.

## The version, and the rule for changing it

The version string is `plan-v1`. It is stored twice: in the document (`schemaVersion`) and in
the column `plans.schema_version`; the database refuses a row in which they differ.

**Any change to this page's Fields, Rules or limits is a new version** (`plan-v2`, and so
on): adding, removing or renaming a field, changing a type or what a value means, adding an
enum value, making something required or optional, and raising or lowering a limit. There are
no minor versions and no optional fields, so "it has version N" says exactly what is in it.

A new version needs, in the same change:

1. this page updated, with the old version's differences noted;
2. `PlanResult.CurrentSchemaVersion` changed, and a reader for every older version that may
   still be stored: a finished plan cannot be rewritten (the database forbids it), so old rows
   keep their old version for as long as they exist;
3. a look at the database check for citations, which finds them by the names `citations` and
   `evidence[].id`.

The code reads only the versions it knows. A stored plan of any other version is refused
with an error, never read on a guess.
