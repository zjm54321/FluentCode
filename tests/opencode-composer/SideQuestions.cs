using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;

internal static partial class Program
{
    private static async Task SideQuestions()
    {
        const string instruction = "The user is asking a quick side question about the conversation so far. Answer directly and concisely in markdown from what you already know. Do not call any tools and do not take any actions.";
        Check(OpenCodeClient.BuildSideQuestionPrompt("  为什么？\n ") == instruction + "\n\n为什么？", "btw uses exact official instruction, blank line and trimmed question");
        foreach (var text in new[] { "/btw", "  /btw  ", "/btw\t" })
            Check(OpenCodeSideQuestionPolicy.TryParse(text, out var empty) && empty.Length == 0, "empty btw recognized for argument hint");
        Check(OpenCodeSideQuestionPolicy.TryParse(" /btw  line one\nline two ", out var question) && question == "line one\nline two", "btw preserves multiline question");
        foreach (var text in new[] { "/btwx question", "/btw/x question", "/BTW question", "text /btw question" })
            Check(!OpenCodeSideQuestionPolicy.TryParse(text, out _), "only exact leading official slash token intercepted");
        Check(OpenCodeSideQuestionPolicy.QuestionLine("  a\n b\t c ") == "a b c", "question header is one muted line");
        using (var f = new Fixture())
        {
            f.Handler.Send = (_, _) => Task.FromResult(Json("{\"data\":{\"text\":\"**answer**\\nnext\"}}"));
            var answer = await f.Client.GenerateSessionTextAsync("ses_test", OpenCodeClient.BuildSideQuestionPrompt("为什么？"));
            using var body = JsonDocument.Parse(f.Handler.LastBody!);
            Check(f.Handler.LastPath == "/api/session/ses_test/generate" && f.Handler.Posts == 1 && f.Handler.Gets == 0,
                "only official session generate route, no prompt/history/queue/preflight");
            Check(body.RootElement.EnumerateObject().Count() == 1 && body.RootElement.GetProperty("prompt").GetString() == instruction + "\n\n为什么？",
                "generate body has ONLY required prompt field");
            Check(answer == "**answer**\nnext", "generate parses nested data.text without losing markdown");
        }
        foreach (var response in new[] { "{}", "{\"data\":{}}", "{\"data\":{\"text\":null}}", "{\"data\":{\"text\":7}}", "{\"text\":\"wrong envelope\"}" })
        {
            using var f = new Fixture();
            f.Handler.Send = (_, _) => Task.FromResult(Json(response));
            try { await f.Client.GenerateSessionTextAsync("ses_test", "prompt"); Check(false, "malformed generate response accepted"); }
            catch (OpenCodeProtocolException) { Check(true, "malformed generate response is protocol failure"); }
        }
        using (var f = new Fixture())
        {
            f.Handler.Send = (_, _) => Task.FromResult(new System.Net.Http.HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            try { await f.Client.GenerateSessionTextAsync("ses_test", "prompt"); Check(false, "503 accepted"); }
            catch (OpenCodeSessionControlException error)
            { Check(error.StatusCode == 503 && OpenCodeSideQuestionPolicy.FailureReason(error).Contains("暂时不可用"), "generate error maps to existing notice policy"); }
        }
    }
}
