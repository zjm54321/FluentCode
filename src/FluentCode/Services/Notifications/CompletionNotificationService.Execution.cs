using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using FluentCode.Services.OpenCode;

namespace FluentCode.Services.Notifications;

public sealed partial class CompletionNotificationService
{
    // Called only for a live execution terminal, with the server projector's msg_<event suffix> identity.
    internal IReadOnlyList<NotificationDispatchResult> ObserveTrustedOpenCodeTerminal(TrustedOpenCodeNotificationSource source,
        string session, OpenCodeMessage terminal, IReadOnlyList<OpenCodeMessage> context, string? title, string? error)
    {
        lock (_gate)
        {
            if (!IsCurrentNotificationSource(source)) return [];
            if (terminal.Outcome == "succeeded") return ObserveTrustedOpenCodeMessages(source, session, new[] { terminal }, false, title, context);
            if (terminal.Outcome != "failed") return [];
            var request = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { source.Identity, session, terminal.Id! })));
            return new[] { NotifyCompletedCore(NotificationSource.OpenCode, request,
                new(NotificationSource.OpenCode, source.Identity, session, terminal.Id!, NotificationNavigationKind.Completion),
                new(NotificationText.SingleLine(title, 80, "OpenCode 执行失败"), "执行失败 · " + NotificationText.SingleLine(error, 180, "请在应用查看错误详情"))) };
        }
    }
}
