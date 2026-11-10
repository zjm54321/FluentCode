using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AngleSharp.Html.Parser;
using FluentCode.Services.Rendering;

internal static class SanitizerRegressionTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        var renderer = new SafeContentRenderer();
        var allowedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "p", "div", "span", "br", "hr", "h1", "h2", "h3", "h4", "h5", "h6",
            "ul", "ol", "li", "dl", "dt", "dd", "blockquote", "pre", "code", "table",
            "caption", "thead", "tbody", "tfoot", "tr", "th", "td", "a", "em", "strong",
            "b", "i", "u", "s", "del", "sub", "sup",
        };
        // Captured against HtmlSanitizer 9.2.1039 / AngleSharp 1.7.2 before the upgrade.
        var vectors = new (string Name, string Input, string ExpectedHtml)[]
        {
            ("script", "<p>before<script>alert('script')</script>after</p>", "<p>beforealert('script')after</p>"),
            ("event-handler", "<p onclick=\"alert(1)\"><b onmouseover=\"alert(2)\">bold</b><img src=x onerror=\"alert(3)\"></p>", "<p><b>bold</b></p>"),
            ("javascript-url", "<a href=\"javascript:alert(1)\" data-token=\"secret\">link</a><a href=\"jav&#x61;script:alert(2)\">encoded</a>", "<a>link</a><a>encoded</a>"),
            ("svg", "<svg onload=\"alert(1)\"><a xlink:href=\"javascript:alert(2)\"><text>svg</text></a><foreignObject><p onclick=\"alert(3)\">html</p></foreignObject><script>alert(4)</script></svg>", "<a>svg</a><p>html</p>alert(4)"),
            ("mathml", "<math><mtext><b onclick=\"alert(1)\">math</b></mtext><annotation-xml encoding=\"text/html\"><script>alert(2)</script></annotation-xml></math>", "<b>math</b>alert(2)"),
            ("mutation-xss", "<math><mtext><table><mglyph><style><!--</style><img title=\"--><img src=x onerror=alert(1)>\">", "&lt;!--<table></table>"),
            ("resource-and-css", "<div style=\"background:url(javascript:alert(1))\"><iframe src=\"https://invalid.example/\">fallback</iframe><form><input autofocus onfocus=\"alert(2)\">text</form></div>", "<div>fallbacktext</div>"),
        };
        var checks = 0;
        foreach (var (name, input, expectedHtml) in vectors)
        {
            foreach (var markdown in new[] { false, true })
            {
                var output = markdown ? renderer.RenderMarkdown(input) : renderer.SanitizeHtml(input);
                var document = new HtmlParser().ParseDocument("<!doctype html><html><body>" + output + "</body></html>");
                foreach (var element in document.Body!.QuerySelectorAll("*"))
                {
                    if (!allowedTags.Contains(element.LocalName) || element.NamespaceUri != "http://www.w3.org/1999/xhtml"
                        || element.Attributes.Length != 0)
                        throw new Exception($"Sanitizer expanded its allowlist for {name} (markdown={markdown}).");
                    checks++;
                }
                if (renderer.SanitizeHtml(output) != output)
                    throw new Exception($"Sanitizer output is not stable after reparsing {name} (markdown={markdown}).");
                checks++;
                if (!markdown)
                {
                    if (output != expectedHtml) throw new Exception($"Sanitizer changed the pre-upgrade HTML result for {name}.");
                    checks++;
                    Console.WriteLine($"SANITIZER {name}: {JsonSerializer.Serialize(output)}");
                }
            }
        }
        Console.WriteLine($"PASS sanitizer regressions: {checks} checks (script, attributes, javascript URLs, SVG, MathML, mutation-XSS, resources/CSS; HTML and Markdown).");
    }
}
