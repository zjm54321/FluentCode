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

        // ---- question tool parts (real 2026-11-08 payload, three questions with bare CR everywhere) ----
        var state = JsonSerializer.SerializeToElement(new
        {
            status = "completed",
            input = new
            {
                questions = new object[]
                {
                    new
                    {
                        header = "你的手机",
                        question = "你平时用什么设备\r？我会按它的尺寸\r \r+\r 字体大小\r 4\r \r+\r 显示大小\r 2\r 去重新设计。",
                        options = new object[]
                        {
                            new { label = "常见\r 6\r.1\r~6\r.7\r 寸直屏", description = "我直接拿模拟器按这个尺寸复现" },
                            new { label = "平板为主", description = "" },
                        },
                    },
                    new
                    {
                        header = "设计稿形式",
                        question = "新设计稿要什么形式\r？",
                        options = new object[]
                        {
                            new { label = "可视化设计稿\r（推荐\r）\r \r(Recommended\r)", description = "沿用\r design\r/src\r 的生成方式\r，每屏出图" },
                        },
                    },
                    new
                    {
                        header = "重做范围", multiple = true, question = "这次重新设计覆盖哪些\r？（可多选\r）",
                        options = new object[] { new { label = "主\r agent\r 与\r subagent\r 的区分", description = "" } },
                    },
                },
            },
            metadata = new { answers = new[] { new[] { "常见\r 6\r.1\r~6\r.7\r 寸直屏" }, new[] { "可视化设计稿\r（推荐\r）\r \r(Recommended\r)" }, new[] { "主\r agent\r 与\r subagent\r 的区分" } } },
        });
        var questions = OpenCodeToolDetailBuiltinData.Questions(state);
        Check(questions.Count == 3 && questions[0].Text == "你平时用什么设备？我会按它的尺寸 + 字体大小 4 + 显示大小 2 去重新设计。",
            "question text drops bare CR");
        Check(questions[0].Options[0].DisplayLabel == "常见 6.1~6.7 寸直屏" && questions[0].IsSelected(questions[0].Options[0]),
            "option label is cleaned for display while the exact label still matches the recorded answer");
        Check(questions[0].Options[1].Description is null, "empty option description stays hidden");
        Check(questions[1].Options[0] is { Recommended: true, DisplayLabel: "可视化设计稿" }
            && questions[1].Options[0].Description == "沿用 design/src 的生成方式，每屏出图",
            "stacked （推荐）(Recommended) suffixes become one tag even when split by CR");
        Check(!questions.SelectMany(q => q.CustomAnswers()).Any(), "cleaning never turns a picked option into a typed answer");
        Check(questions[2] is { Multiple: true, Header: "重做范围" }, "multiple and header survive");
        var multiline = OpenCodeToolDetailBuiltinData.Questions(JsonSerializer.SerializeToElement(new
        {
            input = new { questions = new[] { new { header = " 取 Dashboard\r 账号 ", question = "第一段\r\n\r\n\r\n第二段：\n1. 一\n  - 缩进  \n", options = Array.Empty<object>() } } },
        }));
        Check(multiline.Single() is { Header: "取 Dashboard 账号", Text: "第一段\n\n第二段：\n1. 一\n  - 缩进" },
            "multi-line questions keep paragraphs and indentation, at most one blank line, no trailing spaces");

        var rowModel = OpenCodeToolRowPresentation.Build(Tool("question", "completed", new
        {
            questions = new[] { new { header = "设计稿\r形式", question = "新设计稿要什么形式\r？", options = Array.Empty<object>() } },
        }, null, new { answers = new[] { new[] { "可视化设计稿\r（推荐\r）\r \r(Recommended\r)" } } }));
        Check(rowModel.Target == "设计稿形式" && rowModel.Summary == "已回答：可视化设计稿", "timeline question row title and answer summary are single clean lines");

        // Question card (form) titles, labels and option content are covered by tests/notification-form OtherInputTests.
        var overlay = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/OpenCodeQuestionOverlay.cs"));
        Check(overlay.Contains("\"下一个问题（Ctrl+Enter）\"", StringComparison.Ordinal) && !overlay.Contains("\"下一题（Ctrl+Enter）\"", StringComparison.Ordinal),
            "next-question tooltip names the visible button label plus its shortcut");
    }
}
