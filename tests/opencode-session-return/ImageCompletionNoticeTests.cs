using System;
using System.Linq;
using System.Text.Json;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;

internal static partial class Program
{
    private const string ImageNoticeJob = "image_1d49e1037001IWfZJ0kDXyKmr2";
    private const string ImageNoticeStem = "/tmp/opencode/kaiti/preview/gen/structure_b";

    // image/notice.ts envelope through the real ConversationView: notice row, launch-row association, impostors.
    private static void ImageCompletionNotices()
    {
        var before = _assertions;
        var user = new OpenCodeMessage { Id = "msg_image_user", Type = "user", Text = "Draw the structure", Time = new() { Created = 1 } };
        var answer = new OpenCodeMessage { Id = "msg_image_answer", Type = "assistant", Time = new() { Created = 6, Completed = 7 },
            Content = new() { new() { Id = "part_image_answer", Type = "text", Text = "The picture is ready." } } };

        // The notice on its own (its launch row is outside the loaded history): one compact row carrying the picture path.
        ConversationViewTestProbe.Reset();
        var view = new OpenCodeConversationView();
        var notice = ImageJobNotice();
        view.ApplyMessages(new[] { user, notice, answer });
        var chip = Constructed<OpenCodeTimelineChipView>().Single();
        Check(!chip.CompletionNotice && chip.Content.Name == "图片生成（后台）" && chip.Content.Target == "structure_b.png"
            && chip.Content.Summary == "已完成 · gpt-image-2.5-flare" && chip.Content.Status == OpenCodeToolRowStatus.Completed
            && chip.Content.ImagePath == ImageNoticeStem + ".png" && chip.LinkInvoked is null && chip.DetailText is null,
            "image.real-renderer-uses-the-compact-completion-row-with-the-picture");
        Check(!ConversationViewTestProbe.Records.Any(record => record.Operation == "Render" && record.Input is string text
            && (text.Contains("<image", StringComparison.Ordinal) || text.Contains("Use the read tool", StringComparison.Ordinal))),
            "image.notice-markup-never-reaches-the-prose-presenter");
        Check(ConversationViewTestProbe.Records.Any(record => record.Operation == "Render" && record.Input is string text && text.Contains("The picture is ready.")),
            "image.adjacent-answer-unchanged");
        view.ApplyMessages(new[] { user, notice, answer });
        Check(ReferenceEquals(chip, Constructed<OpenCodeTimelineChipView>().Single()), "image.unchanged-notice-keeps-row-identity");
        view.ApplyMessages(new[] { user, ImageJobNotice("error", "Image request failed for " + ImageNoticeStem + ": quota exceeded"), answer });
        Check(chip.Content.Status == OpenCodeToolRowStatus.Error && chip.Content.Summary == "失败" && chip.Content.ImagePath is null
            && chip.DetailText == "Image request failed for " + ImageNoticeStem + ": quota exceeded", "image.retained-row-reconciles-a-failed-state");
        view.ApplyMessages(new[] { user, ImageJobNotice("cancelled", "Image request for " + ImageNoticeStem + " was cancelled."), answer });
        Check(chip.Content.Status == OpenCodeToolRowStatus.Error && chip.Content.Summary == "已取消" && chip.Content.ImagePath is null, "image.retained-row-reconciles-a-cancelled-state");
        view.ClearMessages();
        Check(chip.DisposeCount == 1, "image.session-clear-disposes-notice");

        // Launch row + notice: the call row receives the completion (state, saved file) through metadata.jobID.
        ConversationViewTestProbe.Reset();
        var linked = new OpenCodeConversationView();
        var call = ImageJobCall();
        linked.ApplyMessages(new[] { user, call });
        Constructed<OpenCodeToolGroupView>().First().OpenLeaf?.Invoke();
        linked.ApplyMessages(new[] { user, call, notice });
        var tool = Constructed<OpenCodeToolPartView>().Single();
        Check(tool.AsyncCompletion is { Kind: OpenCodeAsyncCompletionKind.Image, State: "completed" } matched
            && matched.Key == ImageNoticeJob && matched.ImagePath == ImageNoticeStem + ".png", "image.launch-row-receives-the-finished-job");
        // Same chain (nothing between the call and its notice): one row per job, the launch row; the notice row is folded away.
        var folded = Constructed<OpenCodeTimelineChipView>().Single();
        Check(folded.Content.ImagePath == ImageNoticeStem + ".png" && folded.Element.Visibility == Visibility.Collapsed,
            "image.notice-row-folds-into-its-launch-row-in-the-same-chain");
        // An answer between them ends the chain: the later notice keeps its own row.
        linked.ApplyMessages(new[] { user, call, answer with { Time = new() { Created = 3, Completed = 4 } }, notice });
        Check(Constructed<OpenCodeTimelineChipView>().Any(row => row.Content.ImagePath is not null && row.Element.Visibility == Visibility.Visible),
            "image.notice-after-an-answer-keeps-its-own-row");
        linked.ClearMessages();

        // The same text without the producer's metadata is ordinary content: no completion row.
        ConversationViewTestProbe.Reset();
        var quoted = new OpenCodeConversationView();
        quoted.ApplyMessages(new[]
        {
            notice with { Id = "msg_image_quoted_user", Type = "user", Metadata = null },
            notice with { Id = "msg_image_quoted_assistant", Type = "assistant", Text = null, Metadata = null, Time = new() { Created = 4, Completed = 5 },
                Content = new() { new() { Id = "part_image_quoted", Type = "text", Text = notice.Text } } },
        });
        Check(!Constructed<OpenCodeTimelineChipView>().Any()
            && ConversationViewTestProbe.Records.Count(record => record.Operation == "Render" && record.Input as string == notice.Text) >= 2,
            "image.user-and-assistant-quoted-notice-text-stays-ordinary-prose");
        quoted.ClearMessages();
        foreach (var metadata in new object[]
        {
            new { source = "other", jobID = ImageNoticeJob, state = "completed", path = ImageNoticeStem },
            new { jobID = ImageNoticeJob, state = "completed", path = ImageNoticeStem },
            new { source = "experimental_image_generate", state = "completed", path = ImageNoticeStem },
        })
        {
            ConversationViewTestProbe.Reset();
            var impostor = new OpenCodeConversationView();
            impostor.ApplyMessages(new[] { user, notice with { Metadata = JsonSerializer.SerializeToElement(metadata) }, answer });
            Check(!Constructed<OpenCodeTimelineChipView>().Any(row => row.Content.Name.StartsWith("图片生成", StringComparison.Ordinal) || row.Content.ImagePath is not null),
                "image.synthetic-text-with-wrong-or-missing-metadata-gets-no-completion-row");
            impostor.ClearMessages();
        }
        Console.WriteLine($"PASS image completion notices: {_assertions - before} assertions; real parser/ConversationView, inert visuals, no file read.");
    }

    private static OpenCodeMessage ImageJobNotice(string state = "completed", string? body = null) => new()
    {
        Id = "msg_image_notice", Type = "synthetic", Description = "Image generation", Time = new() { Created = 5 },
        Text = "<image id=\"" + ImageNoticeJob + "\" state=\"" + state + "\">\n"
            + (body ?? "Image request finished. Saved image: " + ImageNoticeStem + ".png (openai/gpt-image-2.5-flare).\nUse the read tool on this path to view the image.")
            + "\n</image>",
        Metadata = JsonSerializer.SerializeToElement(new { source = "experimental_image_generate", jobID = ImageNoticeJob, state, path = ImageNoticeStem }),
    };

    private static OpenCodeMessage ImageJobCall() => new()
    {
        Id = "msg_image_call", Type = "assistant", Time = new() { Created = 2, Completed = 3 },
        Content = new() { new() { Id = "part_image_call", Type = "tool", Name = "experimental_image_generate",
            State = JsonSerializer.SerializeToElement(new
            {
                status = "completed",
                input = new { prompt = "Flat vector structure diagram", path = ImageNoticeStem + ".png" },
                content = new[] { new { type = "text", text = "Image job " + ImageNoticeJob + " started in the background with openai/gpt-image-2.5-flare." } },
                metadata = new { path = ImageNoticeStem, model = "openai/gpt-image-2.5-flare", jobID = ImageNoticeJob, truncated = false },
            }) } },
    };
}
