using System;
using System.Collections.Generic;

namespace AIFrontend.Services.OpenCode;

/// <summary>Process-only user dismissal of operational sync notices. Permission, question and uncertain
/// mutation/protection notices are deliberately outside this policy.</summary>
public static class OpenCodeNoticeDismissals
{
    private static readonly object Sync = new();
    private static readonly HashSet<(string Title, string Message)> Dismissed = new();

    public static bool IsDismissed(string title, string message)
    {
        lock (Sync) return Dismissed.Contains((title, DismissalKey(title, message)));
    }

    public static void RecordUserDismissal(string title, string message)
    {
        if (!IsOperationalSyncNotice(title)) return;
        lock (Sync) Dismissed.Add((title, DismissalKey(title, message)));
    }

    private static string DismissalKey(string title, string message)
        => title == "会话同步出现缺口" ? string.Empty : message;

    private static bool IsOperationalSyncNotice(string title) => title is
        "会话同步出现缺口" or "会话状态读取失败" or "消息刷新失败" or "消息刷新超时" or "会话列表超时";

    public const string AutomaticRestartText = "The server restarted while you were working. Continue from where you left off without repeating completed work.";

    /// <summary>Entire known machine notice only. Quoted/code/embedded text never matches.</summary>
    public static string DisplayNoticeText(string text)
        => string.Equals(text, AutomaticRestartText, StringComparison.Ordinal) ? "重启后继续" : text;
}
