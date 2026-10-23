using System;
using System.Collections.Generic;
using System.Linq;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages.OpenCode;

public enum OpenCodeHomeSessionAction { Delete, Archive, Rename, CopyId }

internal static class OpenCodeHomeClick
{
    // Own-container WinUI ItemClick can supply the container's Content, not the ListViewItem.
    // Tags are actual row metadata, never inferred from visual text or the hidden session selector.
    internal static OpenCodeHomeSessionRow? Resolve(object? clickedItem, object? containerTag, object? contentTag)
        => containerTag as OpenCodeHomeSessionRow ?? clickedItem as OpenCodeHomeSessionRow ?? contentTag as OpenCodeHomeSessionRow;
}
public sealed class OpenCodeHomeSessionActionEventArgs(long revision, OpenCodeHomeSession session, OpenCodeHomeSessionAction action) : EventArgs
{
    public long Revision { get; } = revision;
    public OpenCodeHomeSession Session { get; } = session;
    public OpenCodeHomeSessionAction Action { get; } = action;
}

// Local unbound intent, never settings or a session/global switch. Immutable copy is used by the single create POST.
internal sealed record OpenCodeBlankDraftPreferences(string RuntimeSourceId, Guid Draft, string Directory,
    string? Agent = null, OpenCodeModelRef? Model = null, long Version = 0)
{
    internal Guid BlankSlotId { get; init; }
    internal bool Matches(string runtimeSourceId, Guid draft) => RuntimeSourceId == runtimeSourceId && Draft == draft;
}

/// <summary>Caller-owned source binding identity plus exact remote directory. Neither is a display label.
/// SourceId must change when the adapter's source binding is replaced. No path normalization/case folding.</summary>
public sealed record OpenCodeHomeProjectKey(string SourceId, string Directory);

public sealed record OpenCodeHomeProject(OpenCodeHomeProjectKey Key, string DisplayName, string? ProjectId = null);

/// <summary>An accepted status snapshot overrides DTO outcome entirely. Missing Working never means success.</summary>
public sealed record OpenCodeHomeStatus(bool? Working = null, bool? PendingAction = null,
    bool IsUnread = false, string? Outcome = null)
{
    public bool? QuestionPending { get; init; }
}

public sealed record OpenCodeHomeSession(OpenCodeHomeProjectKey Project, OpenCodeSessionInfo Info,
    OpenCodeHomeStatus? Status = null);

/// <summary>Already accepted data only. Root/child policy, pagination and transport belong to the adapter.
/// Revision is echoed by every request so a consumer can reject a stale source/snapshot action.
/// IsLoadingMore is the shared next-page request in flight (also folded into IsLoading); LoadMoreFailed means the
/// last next-page request for the current cursor failed, so infinite scroll waits for an explicit retry.</summary>
public sealed record OpenCodeHomeSnapshot(long Revision, IReadOnlyList<OpenCodeHomeProject> Projects,
    IReadOnlyList<OpenCodeHomeSession> Sessions, bool IsLoading = false, string? StatusMessage = null,
    bool HasMoreSessions = false, bool IsLoadingMore = false, bool LoadMoreFailed = false);

public enum OpenCodeHomeTone { Unknown, Working, WorkingNeedsAction, NeedsAction, Succeeded, Failed, Interrupted, Unread, QuestionBlocked }
public enum OpenCodeHomeDateGroup { Today, Yesterday, ThisWeek, Earlier, Future, Unknown }

public sealed record OpenCodeHomeSessionRow(OpenCodeHomeSession Session, string Title, string ProjectName,
    OpenCodeHomeTone Tone, OpenCodeHomeDateGroup DateGroup, DateTimeOffset? Updated)
{
    public bool IsUnread => Session.Status?.IsUnread == true;

    /// <summary>需要你处理：被提问阻塞，或已结束但尚未查看。仍在运行的旧完成未读不算“已结束”。</summary>
    public bool NeedsAttention => Tone == OpenCodeHomeTone.QuestionBlocked
        || IsUnread && Tone is not (OpenCodeHomeTone.Working or OpenCodeHomeTone.WorkingNeedsAction);
}

/// <summary>Pure display projection; never creates catalog entries, changes session records, or infers activity.</summary>
public static class OpenCodeHomeProjection
{
    public static OpenCodeHomeTone Classify(OpenCodeHomeSession session)
    {
        var status = session.Status;
        if (status?.QuestionPending == true) return OpenCodeHomeTone.QuestionBlocked;
        if (status?.Working == true)
            return status.PendingAction == true ? OpenCodeHomeTone.WorkingNeedsAction : OpenCodeHomeTone.Working;
        if (status?.PendingAction == true) return OpenCodeHomeTone.NeedsAction;
        var outcome = status is null ? session.Info.Outcome : status.Outcome;
        return outcome switch
        {
            "succeeded" => OpenCodeHomeTone.Succeeded,
            "failed" => OpenCodeHomeTone.Failed,
            "interrupted" => OpenCodeHomeTone.Interrupted,
            _ => OpenCodeHomeTone.Unknown,
        };
    }

    public static DateTimeOffset? Updated(OpenCodeSessionInfo session)
    {
        if (session.Time is not { } time || !double.IsFinite(time.Updated)
            || time.Updated < -62135596800000d || time.Updated > 253402300799999d) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)time.Updated);
    }

    public static OpenCodeHomeDateGroup DateGroup(DateTimeOffset? updated, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (updated is null) return OpenCodeHomeDateGroup.Unknown;
        var date = TimeZoneInfo.ConvertTime(updated.Value, zone).Date;
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        if (date == today) return OpenCodeHomeDateGroup.Today;
        if (date == today.AddDays(-1)) return OpenCodeHomeDateGroup.Yesterday;
        if (date > today) return OpenCodeHomeDateGroup.Future;
        // 本周 = the local calendar week starting Monday; never a rolling seven-day window.
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        return date >= weekStart ? OpenCodeHomeDateGroup.ThisWeek : OpenCodeHomeDateGroup.Earlier;
    }

    public static IReadOnlyList<OpenCodeHomeSessionRow> Rows(OpenCodeHomeSnapshot snapshot,
        OpenCodeHomeProjectKey? selectedProject, string? search, DateTimeOffset now, TimeZoneInfo zone)
    {
        var names = snapshot.Projects.GroupBy(project => project.Key)
            .ToDictionary(group => group.Key, group => group.Last().DisplayName);
        var query = (search ?? string.Empty).Trim();
        return snapshot.Sessions.Where(session => !string.IsNullOrWhiteSpace(session.Info.Id))
            .Where(session => selectedProject is null || session.Project == selectedProject)
            .Where(session => query.Length == 0 || Matches(session, names, query))
            .GroupBy(session => (session.Project, session.Info.Id)).Select(group => group.Last())
            .Select(session =>
            {
                var updated = Updated(session.Info);
                var title = string.IsNullOrWhiteSpace(session.Info.Title) ? "未命名会话" : SingleLine(session.Info.Title);
                var name = names.TryGetValue(session.Project, out var supplied) && !string.IsNullOrWhiteSpace(supplied)
                    && supplied != session.Project.Directory ? supplied : ShortName(session.Project.Directory);
                return new OpenCodeHomeSessionRow(session, title, name, Classify(session), DateGroup(updated, now, zone), updated);
            })
            .OrderBy(row => row.NeedsAttention ? 0 : 1).ThenBy(row => row.DateGroup).ThenByDescending(row => row.Updated)
            .ThenBy(row => row.Session.Project.SourceId, StringComparer.Ordinal)
            .ThenBy(row => row.Session.Project.Directory, StringComparer.Ordinal)
            .ThenBy(row => row.Session.Info.Id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Compact project label when the server supplied no name: the directory's last segment
    /// (the full directory stays in tooltips/automation). Never a guessed name.</summary>
    public static string ShortName(string directory)
    {
        var trimmed = directory.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        var last = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        return last.Length == 0 ? directory : last;
    }

    /// <summary>One display line: embedded line breaks/tabs in a title become single spaces.</summary>
    public static string SingleLine(string text)
        => string.Join(' ', text.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(part => part.Trim()).Where(part => part.Length > 0));

    // Search covers what each row actually shows or copies: title, project name/directory and session ID.
    private static bool Matches(OpenCodeHomeSession session, IReadOnlyDictionary<OpenCodeHomeProjectKey, string> names, string query)
        => (session.Info.Title ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase)
        || (session.Info.Id ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase)
        || session.Project.Directory.Contains(query, StringComparison.OrdinalIgnoreCase)
        || names.TryGetValue(session.Project, out var name) && (name ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase);

    /// <summary>Right-aligned row time: clock for today/yesterday, weekday for this week, date otherwise.</summary>
    public static string TimeLabel(OpenCodeHomeSessionRow row, TimeZoneInfo zone)
    {
        if (row.Tone is OpenCodeHomeTone.Working or OpenCodeHomeTone.WorkingNeedsAction) return "进行中";
        if (row.Tone == OpenCodeHomeTone.QuestionBlocked) return "等你回答";
        if (row.Updated is not { } updated) return string.Empty;
        var local = TimeZoneInfo.ConvertTime(updated, zone);
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        return row.DateGroup switch
        {
            OpenCodeHomeDateGroup.Today or OpenCodeHomeDateGroup.Yesterday => local.ToString("HH:mm", invariant),
            OpenCodeHomeDateGroup.ThisWeek => "周" + "日一二三四五六"[(int)local.DayOfWeek],
            _ => local.Year == TimeZoneInfo.ConvertTime(DateTimeOffset.Now, zone).Year
                ? local.Month + "月" + local.Day + "日" : local.ToString("yyyy/M/d", invariant),
        };
    }
}
