namespace FluentCode.Pages;

internal enum OpenCodeNoticeKind { SshAlert, Permission, Message, StateHint }

/// <summary>
/// 浮动提示与终端的关系（纯逻辑，不依赖界面类型，可离线测试）。
/// 只有“需要用户处理”的提示才可以打断终端；被动提示绝不冻结终端：它与终端重叠时先不显示，等不再重叠再出现。
/// </summary>
internal static class OpenCodeNoticePolicy
{
    /// <summary>需要用户处理：SSH 安全提示、权限条，以及错误 / 警告级或带操作按钮的消息条。</summary>
    internal static bool NeedsAction(OpenCodeNoticeKind kind, bool errorOrWarning, bool hasAction) => kind switch
    {
        OpenCodeNoticeKind.SshAlert or OpenCodeNoticeKind.Permission => true,
        OpenCodeNoticeKind.Message => errorOrWarning || hasAction,
        _ => false,
    };

    /// <summary>被动提示与终端重叠时先不显示（不占终端、不计时）。</summary>
    internal static bool IsHeldBack(OpenCodeNoticeKind kind, bool errorOrWarning, bool hasAction, bool overlapsTerminal)
        => overlapsTerminal && !NeedsAction(kind, errorOrWarning, hasAction);

    /// <summary>需要处理的提示盖住终端时，终端让出画面（保留最后一帧）直到提示关闭。</summary>
    internal static bool TakesTerminal(OpenCodeNoticeKind kind, bool errorOrWarning, bool hasAction, bool overlapsTerminal)
        => overlapsTerminal && NeedsAction(kind, errorOrWarning, hasAction);

    /// <summary>两个矩形（左、上、右、下）是否有面积重叠；只是贴边不算。</summary>
    internal static bool Overlaps(double aLeft, double aTop, double aRight, double aBottom,
        double bLeft, double bTop, double bRight, double bBottom)
        => aLeft < bRight && bLeft < aRight && aTop < bBottom && bTop < aBottom;
}
