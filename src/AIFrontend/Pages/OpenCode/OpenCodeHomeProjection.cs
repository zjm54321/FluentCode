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
/// Revision is echoed by every request so a consumer can reject a stale source/snapshot action.</summary>
public sealed record OpenCodeHomeSnapshot(long Revision, IReadOnlyList<OpenCodeHomeProject> Projects,
    IReadOnlyList<OpenCodeHomeSession> Sessions, bool IsLoading = false, string? StatusMessage = null,
    bool HasMoreSessions = false);

public enum OpenCodeHomeTone { Unknown, Working, WorkingNeedsAction, NeedsAction, Succeeded, Failed, Interrupted, Unread, QuestionBlocked }
public enum OpenCodeHomeDateGroup { Today, Yesterday, Earlier, Future, Unknown }

public sealed record OpenCodeHomeSessionRow(OpenCodeHomeSession Session, string Title, string ProjectName,
    OpenCodeHomeTone Tone, OpenCodeHomeDateGroup DateGroup, DateTimeOffset? Updated)
{
    public bool IsUnread => Session.Status?.IsUnread == true;
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
        return date < today ? OpenCodeHomeDateGroup.Earlier : OpenCodeHomeDateGroup.Future;
    }

    public static IReadOnlyList<OpenCodeHomeSessionRow> Rows(OpenCodeHomeSnapshot snapshot,
        OpenCodeHomeProjectKey? selectedProject, string? search, DateTimeOffset now, TimeZoneInfo zone)
    {
        var names = snapshot.Projects.GroupBy(project => project.Key)
            .ToDictionary(group => group.Key, group => group.Last().DisplayName);
        var query = (search ?? string.Empty).Trim();
        return snapshot.Sessions.Where(session => !string.IsNullOrWhiteSpace(session.Info.Id))
            .Where(session => selectedProject is null || session.Project == selectedProject)
            .Where(session => query.Length == 0 || (session.Info.Title ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase))
            .GroupBy(session => (session.Project, session.Info.Id)).Select(group => group.Last())
            .Select(session =>
            {
                var updated = Updated(session.Info);
                var title = string.IsNullOrWhiteSpace(session.Info.Title) ? "未命名会话" : session.Info.Title;
                var name = names.TryGetValue(session.Project, out var supplied) && !string.IsNullOrWhiteSpace(supplied)
                    ? supplied : session.Project.Directory;
                return new OpenCodeHomeSessionRow(session, title, name, Classify(session), DateGroup(updated, now, zone), updated);
            })
            .OrderBy(row => row.DateGroup).ThenByDescending(row => row.Updated)
            .ThenBy(row => row.Session.Project.SourceId, StringComparer.Ordinal)
            .ThenBy(row => row.Session.Project.Directory, StringComparer.Ordinal)
            .ThenBy(row => row.Session.Info.Id, StringComparer.Ordinal).ToArray();
    }
}
