// Only native visibility/timer entry points are doubled. Catalog, paging, SSE/status and Home
// reconciliation execute the production source extracted by run.ps1 against synthetic HTTP.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;

namespace FluentCode.Pages;

public sealed partial class OpenCodePage
{
    private bool _testHomeRefreshEnabled;
    private void RefreshHomeChrome() { }
    private Task LoadHomeCatalogAsync() { _homeCatalogPending = false; return Task.CompletedTask; } // Settings/project-directory UI boundary.
    partial void OnHostUiVisibilityChanged(bool visible)
    {
        if (_testHomeRefreshEnabled) TestHomeVisibilityBoundary();
    }
    private void TestHomeVisibilityBoundary()
        => GetType().GetMethod("UpdateHomeRefreshVisibility", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(this, null);

    internal void TestEnableHomeRefresh()
    {
        _testHomeRefreshEnabled = true;
        _homeActive = true;
        _selectedSessionId = null;
        _sessionCts?.Cancel();
        _sessionCts = null;
        TestHomeVisibilityBoundary();
    }
    internal void TestLeaveHome()
    {
        _homeActive = false;
        TestHomeVisibilityBoundary();
    }
    internal void TestReturnHome()
    {
        _homeActive = true;
        TestHomeVisibilityBoundary();
    }
    internal Task TestHomeRefreshCompletion()
        => (Task?)GetType().GetField("_homeRefreshTask", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(this) ?? Task.CompletedTask;
    internal Task TestCatalogRefresh() => RefreshSessionsAsync();
    internal Task TestSessionPage() => LoadMoreSessionsAsync();
    internal void TestHomeTimer(bool makeDue = false, bool statusDue = false)
    {
        if (makeDue) GetType().GetField("_homeRefreshAt", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(this, DateTimeOffset.UtcNow);
        if (statusDue) GetType().GetField("_homeStatusAt", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(this, DateTimeOffset.UtcNow);
        GetType().GetMethod("HomeChromeTimer_Tick", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(this,
            new object[] { new Microsoft.UI.Dispatching.DispatcherQueueTimer(), new object() });
    }
    internal DateTimeOffset TestHomeRetryAt => _homeRefreshAt;
    internal int TestHomeReadFailures => _homeRefreshFailures;
    internal int TestCatalogCount => _sessionItems.Count;
    internal string? TestCatalogCursor => _sessionNextCursor;
    internal bool TestHomeRunning => _homeRefreshRunning;
    internal int TestHomePendingIds => _homeDirtySessions.Count;
    internal void TestSeedCatalog(IEnumerable<OpenCodeSessionInfo> rows, string? cursor = null)
    {
        _sessionItems.Clear();
        foreach (var row in rows) _sessionItems[row.Id!] = row;
        RestartSessionPagination(cursor);
    }
    internal OpenCodeHomeSessionRow? TestHomeRow(string id)
    {
        if (!_sessionItems.TryGetValue(id, out var info) || info.Time?.Archived is not null) return null;
        var states = new Dictionary<string, OpenCodeSessionUiState>();
        ApplyLiveSessionStatuses(states);
        states.TryGetValue(id, out var state);
        var project = new OpenCodeHomeProjectKey(_homeRuntimeSource, info.Location!.Directory!);
        var session = new OpenCodeHomeSession(project, info, CreateHomeStatus(state, info));
        return OpenCodeHomeProjection.Rows(new(1, [], [session]), null, null, DateTimeOffset.UtcNow, TimeZoneInfo.Utc).Single();
    }
}
