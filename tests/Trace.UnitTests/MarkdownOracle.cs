using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Trace.TestSupport;

/// <summary>
/// What an independent Markdown parser makes of a document. Markdig is a CommonMark parser that
/// TRACE itself does not use; here it runs with its extensions switched on (bare-URL autolinks,
/// pipe and grid tables, footnotes, task lists, definition lists, raw HTML and more), so that a
/// construct TRACE's own code does not know about still shows up. Shared by the unit and the
/// integration tests.
/// </summary>
internal sealed class MarkdownOracle
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private readonly List<MarkdownObject> nodes;

    public MarkdownOracle(string markdown)
    {
        Document = Markdown.Parse(markdown, Pipeline);
        nodes = [.. Document.Descendants()];
    }

    public MarkdownDocument Document { get; }

    /// <summary>The content of every inline code span, in document order.</summary>
    public IReadOnlyList<string> CodeSpans => [.. nodes.OfType<CodeInline>().Select(code => code.Content)];

    /// <summary>The content of every fenced code block, lines joined by <c>\n</c>.</summary>
    public IReadOnlyList<string> Fences => [.. nodes.OfType<FencedCodeBlock>().Select(block => block.Lines.ToString())];

    /// <summary>The text of every heading, with code spans shown as their content.</summary>
    public IReadOnlyList<string> Headings => [.. nodes.OfType<HeadingBlock>().Select(heading => InlineText(heading.Inline))];

    /// <summary>All text that is outside code formatting: what a reader takes to be the document's own words.</summary>
    public string TextOutsideCode => string.Join('\n', nodes.OfType<LiteralInline>().Select(literal => literal.Content.ToString()));

    /// <summary>
    /// Everything that is active content or that a quoted text could have added: raw HTML,
    /// links, images, autolinks, link reference definitions, tables, indented code. A TRACE
    /// export has none of these, whatever text went into it.
    /// </summary>
    public IReadOnlyList<string> ActiveContent =>
    [
        .. nodes
            .Where(node => node is HtmlBlock or HtmlInline or HtmlEntityInline or LinkInline or AutolinkInline
                or LinkReferenceDefinition or LinkReferenceDefinitionGroup or Table
                || (node is CodeBlock and not FencedCodeBlock))
            .Select(node => node.GetType().Name),
    ];

    /// <summary>
    /// The lines of an export that are not inside a fenced block, found without the parser: a
    /// fence opens with a line of three or more backticks and nothing else, and closes with the
    /// same line.
    /// </summary>
    public static List<string> OutsideFences(string markdown)
    {
        var outside = new List<string>();
        string? fence = null;
        foreach (var line in markdown.Split('\n'))
        {
            if (fence is null && line.Length >= 3 && line.All(c => c == '`'))
            {
                fence = line;
            }
            else if (line == fence)
            {
                fence = null;
            }
            else if (fence is null)
            {
                outside.Add(line);
            }
        }

        return fence is null ? outside : throw new InvalidOperationException("A fenced block is not closed.");
    }

    /// <summary>
    /// The shape of the document with every quoted text blanked out: one line per node, with
    /// its kind, its nesting depth and, for text outside code formatting, the text. Two
    /// documents with the same skeleton differ only in what is inside code spans and fences.
    /// </summary>
    public string Skeleton()
    {
        var skeleton = new StringBuilder();
        Append(skeleton, Document, depth: 0);
        return skeleton.ToString();
    }

    private static void Append(StringBuilder skeleton, MarkdownObject node, int depth)
    {
        skeleton.Append(' ', depth * 2).Append(node.GetType().Name);
        switch (node)
        {
            case LiteralInline literal:
                skeleton.Append(": ").Append(literal.Content.ToString());
                break;

            case HeadingBlock heading:
                skeleton.Append(" level ").Append(heading.Level);
                break;

            case ListBlock list:
                skeleton.Append(list.IsOrdered ? " ordered" : " bullet");
                break;
        }

        skeleton.Append('\n');

        // A code span or a fence is a leaf here: its content is the quoted text.
        if (node is CodeInline or FencedCodeBlock)
        {
            return;
        }

        IEnumerable<MarkdownObject> children = node switch
        {
            ContainerBlock container => container,
            LeafBlock { Inline: { } inline } => inline,
            ContainerInline container => container,
            _ => [],
        };
        foreach (var child in children)
        {
            Append(skeleton, child, depth + 1);
        }
    }

    private static string InlineText(ContainerInline? inline) =>
        string.Concat((inline ?? []).Select(child => child switch
        {
            CodeInline code => code.Content,
            LiteralInline literal => literal.Content.ToString(),
            _ => $"<{child.GetType().Name}>",
        }));
}
