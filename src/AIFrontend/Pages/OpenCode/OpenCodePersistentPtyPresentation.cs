using System;

namespace AIFrontend.Pages.OpenCode;

// Local view contracts, deliberately independent of PTY server/socket DTOs.
internal sealed record OpenCodePersistentPtyViewContext(Guid ViewId, long Generation, string SessionId, string PtyId);
internal enum OpenCodePersistentPtyPhase { Unavailable, Connecting, Replaying, Live, Disconnected, Exited }
internal enum OpenCodePersistentPtyRole { Observer, Controller }
internal sealed record OpenCodePersistentPtyPresentation(OpenCodePersistentPtyViewContext Context, string Title,
    string? Directory, OpenCodePersistentPtyPhase Phase, OpenCodePersistentPtyRole Role, string? ControllerLabel,
    bool CanTakeControl, bool InputAuthorized, string? Notice = null, string? OutputStatus = null)
{
    internal bool Writable => Phase == OpenCodePersistentPtyPhase.Live && Role == OpenCodePersistentPtyRole.Controller && InputAuthorized;
    internal bool NeedsInputConsent => Phase == OpenCodePersistentPtyPhase.Live && CanTakeControl
        && (Role == OpenCodePersistentPtyRole.Observer || !InputAuthorized);
    internal string ConsentAction => Role == OpenCodePersistentPtyRole.Controller ? "启用输入" : "接管终端";
    internal string ConsentExplanation => Role == OpenCodePersistentPtyRole.Controller
        ? "当前连接已获分配控制权，但尚未启用输入。确认后，键盘输入将发送到此终端。"
        : "接管后，键盘输入将发送到此终端，当前控制者将转为只读观察。仅查看不会接管终端。";
    internal string Status => OutputStatus ?? (Phase switch
    {
        OpenCodePersistentPtyPhase.Connecting => "正在连接",
        OpenCodePersistentPtyPhase.Replaying => "正在恢复终端画面",
        OpenCodePersistentPtyPhase.Live => Role == OpenCodePersistentPtyRole.Controller
            ? InputAuthorized ? "你正在控制" : "控制权已分配，输入未启用" : "只读观察",
        OpenCodePersistentPtyPhase.Disconnected => "连接已断开",
        OpenCodePersistentPtyPhase.Exited => "终端已退出",
        _ => "终端不可用",
    });
}
internal sealed record OpenCodePersistentPtyInput(OpenCodePersistentPtyViewContext Context, ReadOnlyMemory<byte> Bytes);
internal sealed record OpenCodePersistentPtyResize(OpenCodePersistentPtyViewContext Context, int Columns, int Rows);
internal sealed record OpenCodePersistentPtySelection(OpenCodePersistentPtyViewContext Context, string Text);

internal static class OpenCodePersistentPtyRendererPolicy
{
    internal static bool ValidGeometry(int columns, int rows) => columns is >= 2 and <= 1000 && rows is >= 1 and <= 1000;
    internal static bool AcceptsInputGeneration(bool writable, string expected, string? received)
        => writable && expected.Length > 0 && string.Equals(expected, received, StringComparison.Ordinal);
}
