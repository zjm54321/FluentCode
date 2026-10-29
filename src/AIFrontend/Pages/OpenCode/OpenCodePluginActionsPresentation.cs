using System;

namespace AIFrontend.Pages.OpenCode;

// Local presentation/event contracts, not plugin RPC schemas or mutation authority.
internal sealed record OpenCodePluginActionContext(Guid ViewId, long Generation, string SessionId, string Directory);
internal enum OpenCodePluginActionKind { ApplyPreset, ClearEncrypted }
internal sealed record OpenCodePluginUnknownProtection(Guid OperationId, OpenCodePluginActionKind Kind)
{
    internal string ActionLabel => Kind == OpenCodePluginActionKind.ApplyPreset ? "应用预设" : "清除加密内容";
}
internal sealed record OpenCodePluginProtectionAcknowledgement(OpenCodePluginActionContext Context, Guid OperationId);
internal sealed record OpenCodeClearEncryptedCounts(long ChangedMessages, long ChangedEvents, long RemovedFields)
{
    internal string Summary => ChangedMessages < 0 || ChangedEvents < 0 || RemovedFields < 0 ? "清除结果无法确认"
        : $"已清除加密内容 · {ChangedMessages} 条消息 · {ChangedEvents} 条事件 · {RemovedFields} 个字段";
}
internal sealed record OpenCodePluginActionsPresentation(OpenCodePluginActionContext Context, string PresetSummary,
    bool CanApplyPreset, bool CanClearEncrypted, bool Pending, string? Notice = null, OpenCodeClearEncryptedCounts? ClearResult = null,
    OpenCodePluginUnknownProtection? UnknownProtection = null);
internal sealed record OpenCodePresetApplyRequest(OpenCodePluginActionContext Context, string ExactName);
internal static class OpenCodePluginActionsPolicy
{
    // Names are opaque own-keys in resolved server config. No trim, catalog, fuzzy match or unbind exists.
    internal static bool CanSubmitExactName(string? value) => value is { Length: > 0 };
    internal static bool CanAcknowledge(OpenCodePluginActionsPresentation? current, OpenCodePluginProtectionAcknowledgement request)
        => request.OperationId != Guid.Empty && current is { Pending: false, UnknownProtection: { } protection }
            && current.Context == request.Context && protection.OperationId == request.OperationId;
}
