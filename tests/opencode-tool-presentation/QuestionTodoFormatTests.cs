using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;

// Question / to-do display format regressions. Fixtures are real payloads from the local OpenCode DB
// (session_message tool parts, 2026-11-08/09) whose model output carried bare CR at token boundaries.
internal static partial class Program
{
    private static void QuestionTodoFormat(string repo)
    {
        // ---- the to-do item from the user's screenshot (rendered as a column: "1" / ".2611" / ".14" / …) ----
        const string screenshot = "1\r.2611\r.14\r 已发布安装\r（PID\r 38448\r）\r，安装包在桌面\r，FluentCode\r dbac0e6\r 与\r qol\r 5101724\r 已推送";
        Check(OpenCodeDisplayText.Line(screenshot) == "1.2611.14 已发布安装（PID 38448），安装包在桌面，FluentCode dbac0e6 与 qol 5101724 已推送",
            "bare CR in a to-do item is dropped, not turned into a line break");
        Check(OpenCodeDisplayText.Line("R6b\r 主\r/子代理审计\r \r+\r R6c\r 用户截图记录") == "R6b 主/子代理审计 + R6c 用户截图记录",
            "CR-separated spaces collapse to one space");
        Check(OpenCodeDisplayText.Line("新设计稿\r design\r/v2\r：01–04\r 已出\r，补\r 05–10\r \r+\r SPEC") == "新设计稿 design/v2：01–04 已出，补 05–10 + SPEC",
            "CJK mixed with Latin keeps its own spacing");
        Check(OpenCodeDisplayText.Line("  first line\r\nsecond\nthird\u2028fourth\tend  ") == "first line second third fourth end",
            "real line breaks and tabs inside one to-do item become single spaces; ends trimmed");
        Check(OpenCodeDisplayText.Line("a\u200Bb\uFEFFc\u202Ed\u0007e\u00ADf") == "abcdef", "invisible format characters and C0 controls are removed");
        Check(OpenCodeDisplayText.Line("👩\u200D💻 ok") == "👩\u200D💻 ok", "ZWJ emoji sequences survive");
        Check(OpenCodeDisplayText.Line("\r\n \t") == string.Empty && OpenCodeDisplayText.Line(null) == string.Empty, "whitespace-only is empty");

        // Stored data is untouched: replay keeps the exact content; only the status token is repaired ("in\r_progress" was real).
        using (var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            input = new
            {
                todos = new object[]
                {
                    new { content = screenshot, status = "completed" },
                    new { content = "等远程桌面重新连上\r：自动截图对照", status = "in\r_progress" },
                    new { content = "x", status = " pending\r" },
                    new { content = "y", status = "cancelled" },
                    new { content = "z", status = "done" },
                },
            },
        })))
        {
            var items = OpenCodeTodoReplay.Normalize(document.RootElement);
            Check(items.Length == 5 && items[0].Content == screenshot, "replay keeps stored content byte-for-byte");
            Check(items.Select(item => item.Status).SequenceEqual(new[] { "completed", "in_progress", "pending", "cancelled", "pending" }),
                "status token tolerates stray CR/whitespace; unknown still falls back to pending");
        }

        // The right-panel card and the todowrite detail share one row builder that cleans for display only.
        var insights = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/OpenCodeSessionInsightsView.cs"));
        var row = insights[insights.IndexOf("internal static UIElement TodoRow(", StringComparison.Ordinal)..];
        row = row[..row.IndexOf("internal static bool? TodoChecked", StringComparison.Ordinal)];
        Check(row.Contains("OpenCodeDisplayText.Line(item.Content)", StringComparison.Ordinal)
            && !row.Contains("StyledText(item.Content", StringComparison.Ordinal)
            && row.Contains("\"：\" + content", StringComparison.Ordinal), "to-do row text and accessible name use the cleaned line");
        var builtins = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/OpenCodeToolDetailBuiltins.cs"));
        var generic = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/OpenCodeToolDetailGeneric.cs"));
        Check(builtins.Contains("OpenCodeSessionInsightsView.TodoRow(items[index], index, \"OpenCodeToolDetailTodoItem.\")", StringComparison.Ordinal)
            && !generic.Contains("TodoDetailRow", StringComparison.Ordinal),
            "todowrite detail reuses the panel row (one checkbox/strikethrough/status vocabulary)");
    }
}
