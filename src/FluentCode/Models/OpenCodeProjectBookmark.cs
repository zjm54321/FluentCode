using System;
using System.Collections.Generic;
using System.Linq;

namespace FluentCode.Models;

/// <summary>
/// 已添加的远程目录。SourceId 为调用者提供的稳定 Source/profile 键（不含凭据），
/// Directory 必须由当前 Source 的 GetLocation 解析；本记录不证明目录存在。
/// </summary>
public sealed record OpenCodeProjectBookmark(string SourceId, string Directory, string? ProjectId = null, string? DisplayName = null);

/// <summary>纯快照更新；调用者先验证服务器 location/fs，再保存至既有 SettingsService。</summary>
public static class OpenCodeProjectBookmarks
{
    public static IReadOnlyList<OpenCodeProjectBookmark> ForSource(AppSettings settings, string sourceId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        return (settings.OpenCodeProjectDirectories ?? Array.Empty<OpenCodeProjectBookmark>())
            .Where(item => string.Equals(item.SourceId, sourceId, StringComparison.Ordinal)).ToArray();
    }

    public static AppSettings Upsert(AppSettings settings, OpenCodeProjectBookmark bookmark)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(bookmark);
        ArgumentException.ThrowIfNullOrWhiteSpace(bookmark.SourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(bookmark.Directory);
        var items = (settings.OpenCodeProjectDirectories ?? Array.Empty<OpenCodeProjectBookmark>()).ToList();
        var index = items.FindIndex(item => item.SourceId == bookmark.SourceId && item.Directory == bookmark.Directory);
        if (index < 0) items.Add(bookmark);
        else items[index] = bookmark;
        return settings with { OpenCodeProjectDirectories = items.ToArray() };
    }

    public static AppSettings Remove(AppSettings settings, string sourceId, string directory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var items = (settings.OpenCodeProjectDirectories ?? Array.Empty<OpenCodeProjectBookmark>())
            .Where(item => item.SourceId != sourceId || item.Directory != directory).ToArray();
        return settings with { OpenCodeProjectDirectories = items.Length == 0 ? null : items };
    }
}
