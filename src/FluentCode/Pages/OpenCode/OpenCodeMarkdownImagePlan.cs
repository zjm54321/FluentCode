using System.Collections.Generic;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace FluentCode.Pages.OpenCode;

internal sealed record OpenCodeMarkdownSegment(string Text, bool IsImage = false, string Alt = "", string? Title = null);

/// <summary>Only Markdown AST image nodes become image requests. HTML, code and escaped syntax stay text.</summary>
internal static class OpenCodeMarkdownImagePlan
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePreciseSourceLocation().Build();
    internal static IReadOnlyList<OpenCodeMarkdownSegment> Build(string source)
    {
        var document = Markdown.Parse(source, Pipeline);
        var result = new List<OpenCodeMarkdownSegment>();
        var offset = 0;
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (!link.IsImage || link.Span.Start < offset || link.Span.End < link.Span.Start) continue;
            if (link.Span.Start > offset) result.Add(new(source[offset..link.Span.Start]));
            var alt = new StringBuilder();
            foreach (var node in link.Descendants())
            {
                if (node is LiteralInline literal) alt.Append(literal.Content.ToString());
                else if (node is CodeInline code) alt.Append(code.Content);
            }
            result.Add(new(link.Url ?? string.Empty, true, alt.ToString(), link.Title));
            offset = link.Span.End + 1;
        }
        if (offset < source.Length) result.Add(new(source[offset..]));
        return result;
    }
}
