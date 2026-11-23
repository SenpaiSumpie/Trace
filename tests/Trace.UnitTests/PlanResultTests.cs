using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Trace.Application.Plans;

namespace Trace.UnitTests;

/// <summary>
/// The plan result is a versioned contract (<c>docs/plan-result-schema.md</c>): it round-trips
/// through JSON, and neither writing nor reading lets through a document that breaks a rule.
/// </summary>
public partial class PlanResultTests
{
    private static readonly Guid CodeEvidence = Guid.Parse("0193a5c0-0000-7000-8000-000000000001");
    private static readonly Guid TestEvidence = Guid.Parse("0193a5c0-0000-7000-8000-000000000002");

    [Fact]
    public void A_plan_round_trips_through_json_with_every_field()
    {
        var plan = ValidPlan();

        var json = plan.ToJson();
        var read = PlanResult.FromJson(json);

        Assert.Equal(json, read.ToJson());
        Assert.Equal(PlanResult.CurrentSchemaVersion, read.SchemaVersion);
        Assert.Equal(PlanOutcome.Planned, read.Outcome);
        Assert.Equal(plan.Summary.Text, read.Summary.Text);
        Assert.Equal([CodeEvidence], read.Summary.Citations);
        Assert.Equal(plan.Steps.Select(step => step.Text), read.Steps.Select(step => step.Text));
        Assert.Equal([TestEvidence], read.Steps[0].Citations);
        Assert.Equal(
            [("src/Tasks/PageRequest.cs", PlanFileChange.Modify), ("tests/PageRequestCapTests.cs", PlanFileChange.Create)],
            read.FilesToChange.Select(file => (file.Path, file.Change)));
        Assert.Equal("Callers may rely on large pages.", Assert.Single(read.Risks).Text);
        Assert.Equal("Run the PageRequest tests.", Assert.Single(read.Verification).Text);
        Assert.Empty(Assert.Single(read.Unknowns).Citations);
        Assert.Equal(plan.Evidence, read.Evidence);
    }

    [Fact]
    public void The_json_uses_the_documented_names_and_values()
    {
        var json = JsonNode.Parse(ValidPlan().ToJson())!.AsObject();

        Assert.Equal(
            ["schemaVersion", "outcome", "summary", "steps", "filesToChange", "risks", "verification", "unknowns", "evidence"],
            json.Select(property => property.Key));
        Assert.Equal("plan-v1", (string?)json["schemaVersion"]);
        Assert.Equal("planned", (string?)json["outcome"]);
        Assert.Equal("modify", (string?)json["filesToChange"]![0]!["change"]);
        Assert.Equal("0193a5c0-0000-7000-8000-000000000001", (string?)json["summary"]!["citations"]![0]);
        Assert.Equal(
            ["id", "path", "startLine", "endLine", "excerpt"],
            json["evidence"]![0]!.AsObject().Select(property => property.Key));
    }

    [Fact]
    public void The_documented_example_is_a_valid_plan_and_round_trips()
    {
        var document = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "plan-result-schema.md"));
        var example = JsonBlock().Match(document).Groups["json"].Value;
        Assert.NotEmpty(example);

        var plan = PlanResult.FromJson(example);

        Assert.Empty(plan.Validate());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(example), JsonNode.Parse(plan.ToJson())));
        Assert.Contains($"**Version: `{PlanResult.CurrentSchemaVersion}`.**", document);
    }

    [Fact]
    public void An_insufficient_evidence_result_holds_a_summary_and_unknowns_only()
    {
        var insufficient = new PlanResult(
            PlanResult.CurrentSchemaVersion,
            PlanOutcome.InsufficientEvidence,
            new PlanStatement("Nothing in the snapshot shows where this would change.", []),
            Steps: [],
            FilesToChange: [],
            Risks: [],
            Verification: [],
            Unknowns: [new PlanStatement("Where exports are produced.", [])],
            Evidence: []);

        var read = PlanResult.FromJson(insufficient.ToJson());

        Assert.Equal(PlanOutcome.InsufficientEvidence, read.Outcome);
        Assert.Contains("\"outcome\":\"insufficient_evidence\"", insufficient.ToJson());
        Assert.NotEmpty((insufficient with { Steps = ValidPlan().Steps, Evidence = ValidPlan().Evidence }).Validate());
        Assert.NotEmpty((insufficient with { Risks = [new PlanStatement("A risk.", [])] }).Validate());
    }

    [Fact]
    public void A_planned_result_needs_at_least_one_step()
    {
        var plan = ValidPlan() with
        {
            Steps = [],
            Verification = [new PlanStatement("Run the PageRequest tests.", [TestEvidence])],
        };

        Assert.Contains(plan.Validate(), problem => problem.Contains("at least one step"));
        Assert.Throws<PlanSchemaException>(plan.ToJson);
    }

    [Fact]
    public void Steps_risks_verification_and_changed_files_must_be_cited()
    {
        var valid = ValidPlan();
        PlanResult[] uncited =
        [
            valid with { Steps = [.. valid.Steps, new PlanStatement("Also rewrite the worker.", [])] },
            valid with { Risks = [new PlanStatement("An uncited risk.", [])] },
            valid with { Verification = [.. valid.Verification, new PlanStatement("An uncited check.", [])] },
            valid with { FilesToChange = [new PlanFile("src/Worker.cs", PlanFileChange.Modify, "Uncited.", [])] },
            valid with { FilesToChange = [new PlanFile("src/Worker.cs", PlanFileChange.Delete, "Uncited.", [])] },
        ];

        Assert.All(uncited, plan =>
        {
            Assert.Contains(plan.Validate(), problem => problem.Contains("needs at least one citation"));
            Assert.Throws<PlanSchemaException>(plan.ToJson);
        });
    }

    [Fact]
    public void A_new_file_the_summary_and_an_unknown_may_be_uncited()
    {
        var valid = ValidPlan();
        var plan = valid with
        {
            Summary = new PlanStatement("A summary without a citation.", []),
            Risks = [new PlanStatement("Callers may rely on large pages.", [CodeEvidence])],
        };

        Assert.Empty(plan.Validate());
    }

    [Fact]
    public void A_citation_must_be_an_entry_of_the_plans_own_evidence()
    {
        var valid = ValidPlan();
        var elsewhere = Guid.Parse("0193a5c0-0000-7000-8000-00000000ffff");

        var plan = valid with { Steps = [new PlanStatement("Cites evidence the plan does not have.", [elsewhere])] };

        Assert.Contains(plan.Validate(), problem => problem.Contains("not in evidence"));
        Assert.Throws<PlanSchemaException>(plan.ToJson);
    }

    [Fact]
    public void Evidence_is_listed_once_and_every_entry_is_cited()
    {
        var valid = ValidPlan();
        var unused = new PlanEvidence(Guid.Parse("0193a5c0-0000-7000-8000-000000000003"), "src/Unused.cs", 1, 1, "x");

        Assert.Contains(
            (valid with { Evidence = [.. valid.Evidence, unused] }).Validate(),
            problem => problem.Contains("nothing cites"));
        Assert.Contains(
            (valid with { Evidence = [.. valid.Evidence, valid.Evidence[0]] }).Validate(),
            problem => problem.Contains("empty or repeated"));
        Assert.Contains(
            (valid with { Evidence = [valid.Evidence[0] with { Id = Guid.Empty }, valid.Evidence[1]] }).Validate(),
            problem => problem.Contains("empty or repeated"));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 4)]
    [InlineData(1, PlanResult.MaxEvidenceLines + 1)]
    public void Evidence_cites_a_bounded_range_of_real_line_numbers(int startLine, int endLine)
    {
        var valid = ValidPlan();

        var plan = valid with
        {
            Evidence = [valid.Evidence[0] with { StartLine = startLine, EndLine = endLine }, valid.Evidence[1]],
        };

        Assert.Contains(plan.Validate(), problem => problem.Contains("lines"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/etc/passwd")]
    [InlineData("../outside.cs")]
    [InlineData("src\\App.cs")]
    [InlineData("src//App.cs")]
    [InlineData("./src/App.cs")]
    [InlineData("src/\nApp.cs")]
    public void Paths_are_plain_repository_relative_paths(string path)
    {
        var valid = ValidPlan();

        var badFile = valid with
        {
            FilesToChange = [new PlanFile(path, PlanFileChange.Create, "A new file.", [])],
        };
        var badEvidence = valid with { Evidence = [valid.Evidence[0] with { Path = path }, valid.Evidence[1]] };

        Assert.Contains(badFile.Validate(), problem => problem.Contains("repository-relative"));
        Assert.Contains(badEvidence.Validate(), problem => problem.Contains("repository-relative"));
    }

    [Fact]
    public void A_file_is_listed_once()
    {
        var valid = ValidPlan();

        var plan = valid with { FilesToChange = [valid.FilesToChange[0], valid.FilesToChange[0]] };

        Assert.Contains(plan.Validate(), problem => problem.Contains("listed twice"));
    }

    [Fact]
    public void Counts_and_lengths_are_limited()
    {
        var valid = ValidPlan();
        var step = valid.Steps[0];
        PlanResult[] tooMuch =
        [
            valid with { Summary = valid.Summary with { Text = new string('x', PlanResult.MaxSummaryLength + 1) } },
            valid with { Steps = [step with { Text = new string('x', PlanResult.MaxStepLength + 1) }] },
            valid with { Steps = [.. Enumerable.Repeat(step, PlanResult.MaxSteps + 1)] },
            valid with { Risks = [.. Enumerable.Repeat(valid.Risks[0], PlanResult.MaxRisks + 1)] },
            valid with { Verification = [.. Enumerable.Repeat(valid.Verification[0], PlanResult.MaxVerification + 1)] },
            valid with { Unknowns = [.. Enumerable.Repeat(valid.Unknowns[0], PlanResult.MaxUnknowns + 1)] },
            valid with { Unknowns = [new PlanStatement(new string('x', PlanResult.MaxShortTextLength + 1), [])] },
            valid with
            {
                FilesToChange =
                [
                    .. Enumerable.Range(0, PlanResult.MaxFiles + 1)
                        .Select(index => new PlanFile($"src/New{index}.cs", PlanFileChange.Create, "New.", [])),
                ],
            },
            valid with { Steps = [step with { Citations = [.. Enumerable.Repeat(TestEvidence, 2)] }] },
            valid with
            {
                Evidence =
                [
                    valid.Evidence[0] with { Excerpt = new string('x', PlanResult.MaxTotalExcerptLength) },
                    valid.Evidence[1],
                ],
            },
        ];

        Assert.All(tooMuch, plan => Assert.NotEmpty(plan.Validate()));
        Assert.Empty((valid with { Steps = [.. Enumerable.Repeat(step, PlanResult.MaxSteps)] }).Validate());
        Assert.Empty((valid with { Summary = valid.Summary with { Text = new string('x', PlanResult.MaxSummaryLength) } }).Validate());
    }

    [Fact]
    public void More_evidence_or_citations_than_the_limit_is_refused()
    {
        var valid = ValidPlan();
        var many = Enumerable.Range(1, PlanResult.MaxEvidence + 1)
            .Select(index => new PlanEvidence(Guid.CreateVersion7(), $"src/File{index}.cs", 1, 1, "x"))
            .ToList();
        var steps = many.Select(entry => new PlanStatement("A step.", [entry.Id])).Take(PlanResult.MaxSteps).ToList();
        var rest = many.Skip(PlanResult.MaxSteps).Select(entry => new PlanStatement("A risk.", [entry.Id])).ToList();

        var tooMuchEvidence = valid with
        {
            Summary = new PlanStatement("Summary.", []),
            Steps = steps,
            FilesToChange = [],
            Risks = [.. rest.Take(PlanResult.MaxRisks)],
            Verification = [.. rest.Skip(PlanResult.MaxRisks)],
            Evidence = many,
        };
        var tooManyCitations = tooMuchEvidence with
        {
            Evidence = [.. many.Take(PlanResult.MaxCitationsPerItem + 1)],
            Steps = [new PlanStatement("A step.", [.. many.Take(PlanResult.MaxCitationsPerItem + 1).Select(entry => entry.Id)])],
            Risks = [],
            Verification = [],
        };

        Assert.Equal([$"evidence has more than {PlanResult.MaxEvidence} entries."], tooMuchEvidence.Validate());
        Assert.Equal([$"steps[0] has more than {PlanResult.MaxCitationsPerItem} citations."], tooManyCitations.Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nul\0inside")]
    public void Text_is_never_blank_and_never_holds_a_nul_character(string text)
    {
        var valid = ValidPlan();

        Assert.NotEmpty((valid with { Summary = new PlanStatement(text, []) }).Validate());
        Assert.NotEmpty((valid with { Unknowns = [new PlanStatement(text, [])] }).Validate());
        Assert.NotEmpty((valid with { Evidence = [valid.Evidence[0] with { Excerpt = text }, valid.Evidence[1]] }).Validate());
    }

    [Theory]
    [InlineData("plan-v0")]
    [InlineData("plan-v2")]
    [InlineData("1")]
    public void Another_schema_version_is_refused_when_reading_and_when_writing(string version)
    {
        var json = ValidPlan().ToJson().Replace("\"schemaVersion\":\"plan-v1\"", $"\"schemaVersion\":\"{version}\"");

        var exception = Assert.Throws<PlanSchemaException>(() => PlanResult.FromJson(json));

        Assert.Contains(version, exception.Message);
        Assert.Throws<PlanSchemaException>((ValidPlan() with { SchemaVersion = version }).ToJson);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"schemaVersion":1}""")]
    public void Text_that_is_not_a_plan_document_is_refused(string json)
    {
        Assert.Throws<PlanSchemaException>(() => PlanResult.FromJson(json));
    }

    [Fact]
    public void An_unknown_field_is_refused()
    {
        var json = JsonNode.Parse(ValidPlan().ToJson())!.AsObject();
        json["confidence"] = "high";
        var nested = JsonNode.Parse(ValidPlan().ToJson())!.AsObject();
        nested["steps"]![0]!["affectedPaths"] = new JsonArray();

        Assert.Throws<PlanSchemaException>(() => PlanResult.FromJson(json.ToJsonString()));
        Assert.Throws<PlanSchemaException>(() => PlanResult.FromJson(nested.ToJsonString()));
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("steps")]
    [InlineData("filesToChange")]
    [InlineData("risks")]
    [InlineData("verification")]
    [InlineData("unknowns")]
    [InlineData("evidence")]
    [InlineData("outcome")]
    public void A_missing_or_null_field_is_refused(string field)
    {
        var missing = JsonNode.Parse(ValidPlan().ToJson())!.AsObject();
        missing.Remove(field);
        var nulled = JsonNode.Parse(ValidPlan().ToJson())!.AsObject();
        nulled[field] = null;

        Assert.Throws<PlanSchemaException>(() => PlanResult.FromJson(missing.ToJsonString()));
        Assert.Throws<PlanSchemaException>(() => PlanResult.FromJson(nulled.ToJsonString()));
    }

    [Fact]
    public void A_missing_nested_field_is_refused()
    {
        var noCitations = JsonNode.Parse(ValidPlan().ToJson())!.AsObject();
        noCitations["steps"]![0]!.AsObject().Remove("citations");
        var noExcerpt = JsonNode.Parse(ValidPlan().ToJson())!.AsObject();
        noExcerpt["evidence"]![0]!.AsObject().Remove("excerpt");

        Assert.Throws<PlanSchemaException>(() => PlanResult.FromJson(noCitations.ToJsonString()));
        Assert.Throws<PlanSchemaException>(() => PlanResult.FromJson(noExcerpt.ToJsonString()));
    }

    [Theory]
    [InlineData("outcome", "\"done\"")]
    [InlineData("outcome", "0")]
    public void An_unknown_outcome_is_refused(string field, string value)
    {
        var json = JsonNode.Parse(ValidPlan().ToJson())!.AsObject();
        json[field] = JsonNode.Parse(value);

        Assert.Throws<PlanSchemaException>(() => PlanResult.FromJson(json.ToJsonString()));
    }

    [Theory]
    [InlineData("\"rename\"")]
    [InlineData("2")]
    public void An_unknown_kind_of_file_change_is_refused(string value)
    {
        var json = JsonNode.Parse(ValidPlan().ToJson())!.AsObject();
        json["filesToChange"]![0]!["change"] = JsonNode.Parse(value);

        Assert.Throws<PlanSchemaException>(() => PlanResult.FromJson(json.ToJsonString()));
        Assert.Throws<PlanSchemaException>(
            (ValidPlan() with
            {
                FilesToChange = [new PlanFile("src/New.cs", (PlanFileChange)99, "New.", [])],
            }).ToJson);
    }

    [Fact]
    public void A_citation_that_is_not_an_id_is_refused()
    {
        var json = JsonNode.Parse(ValidPlan().ToJson())!.AsObject();
        json["steps"]![0]!["citations"] = new JsonArray("src/Tasks/PageRequest.cs:1-3");

        Assert.Throws<PlanSchemaException>(() => PlanResult.FromJson(json.ToJsonString()));
    }

    private static PlanResult ValidPlan() => new(
        PlanResult.CurrentSchemaVersion,
        PlanOutcome.Planned,
        new PlanStatement("Cap the page size where the request is parsed.", [CodeEvidence]),
        Steps:
        [
            new PlanStatement("Add a failing test for an oversized page.", [TestEvidence]),
            new PlanStatement("Clamp the size in PageRequest.", [CodeEvidence]),
        ],
        FilesToChange:
        [
            new PlanFile("src/Tasks/PageRequest.cs", PlanFileChange.Modify, "Where the size is parsed.", [CodeEvidence]),
            new PlanFile("tests/PageRequestCapTests.cs", PlanFileChange.Create, "New tests for the cap.", []),
        ],
        Risks: [new PlanStatement("Callers may rely on large pages.", [CodeEvidence])],
        Verification: [new PlanStatement("Run the PageRequest tests.", [TestEvidence])],
        Unknowns: [new PlanStatement("Whether any client depends on large pages.", [])],
        Evidence:
        [
            new PlanEvidence(CodeEvidence, "src/Tasks/PageRequest.cs", 12, 14, "    public int Size { get; init; } = 20;\n\n    <T> & \"quoted\""),
            new PlanEvidence(TestEvidence, "tests/PageRequestTests.cs", 8, 9, "    [Fact]\n    public void Default_page_size_is_twenty()"),
        ]);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Trace.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate Trace.slnx above the test output directory.");
    }

    [GeneratedRegex("```json\\r?\\n(?<json>.*?)```", RegexOptions.Singleline)]
    private static partial Regex JsonBlock();
}
