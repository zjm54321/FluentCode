using System;
using System.Collections.Generic;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private sealed record SessionStateRow(ProgressRing Working, Ellipse Attention, TextBlock Hint);
    private readonly Dictionary<ListViewItem, SessionStateRow> _sessionStateRows = new();
    private App? _sessionStateApp;
    private OpenCodePageSource? _sessionStateSource;
    private long _sessionStateRevision;
    private long _sessionStateVersion = -1;
    private bool _sessionStateRefreshQueued;

    /// <summary>
    /// 当前会话是否“工作中”（F6：顶部横向进度条已移除，工作状态由标签栏着色工作环表示）。
    /// 与原进度条同源同时机更新，保留为页面内可读状态。
    /// </summary>
    private bool _selectedSessionWorkingIndicated;

    internal bool IsSelectedSessionWorkingIndicated => _selectedSessionWorkingIndicated;

    /// <summary>
    /// 工作环/状态点的语义（F6）：Working = 强调色旋转；WorkingNeedsAction = 注意色（黄）旋转（工作中且等待提问/权限）；
    /// NeedsAction = 注意色静止环（未在工作但等待提问/权限）；Failed = 严重色（红）静止环（未读的失败/中断完成）；
    /// Unread = 普通未读圆点；None = 不显示。
    /// </summary>
    internal enum SessionIndicatorTone
    {
        None,
        Working,
        WorkingNeedsAction,
        NeedsAction,
        Failed,
        Unread,
    }

    /// <summary>纯函数：由会话 UI 状态得出指示器语义（未知字段不臆测为待处理/失败）。</summary>
    internal static SessionIndicatorTone ClassifySessionIndicator(OpenCodeSessionUiState? state)
    {
        if (state is null) return SessionIndicatorTone.None;
        if (state.Working == true)
            return state.PendingAction == true ? SessionIndicatorTone.WorkingNeedsAction : SessionIndicatorTone.Working;
        if (state.PendingAction == true) return SessionIndicatorTone.NeedsAction;
        if (state.IsUnread)
            return state.Completion?.Outcome is "failed" or "interrupted" ? SessionIndicatorTone.Failed : SessionIndicatorTone.Unread;
        return SessionIndicatorTone.None;
    }

    /// <summary>
    /// 会话行工作环着色扩展点（实现位于 OpenCodePage.Tabs.cs，套用 ThemeResource 前景样式）：仅外观，
    /// 不改变行的显示/隐藏判定。未实现时（单独编译本文件的测试）调用被移除。
    /// </summary>
    partial void OnSessionStateRowToneApplied(ProgressRing working, SessionIndicatorTone tone);

    private static readonly IReadOnlyDictionary<string, OpenCodeSessionUiState> NoSessionUiStates =
        new Dictionary<string, OpenCodeSessionUiState>(StringComparer.Ordinal);

    /// <summary>
    /// 重设计 v3 §2.3 扩展点（实现位于 OpenCodePage.Tabs.cs）：每次指示器刷新/清空后，把同一份按会话 ID 的
    /// 状态交给标签栏，使标签的工作中/需要处理标记与会话行同源。未实现时（如单独编译本文件的测试）调用被移除。
    /// </summary>
    partial void OnSessionUiStatesApplied(IReadOnlyDictionary<string, OpenCodeSessionUiState> states);

    /// <summary>
    /// 会话行指示器创建后的外观扩展点（实现位于 OpenCodePage.Tabs.cs）：套用内置文字样式与 ThemeResource 圆点样式，
    /// 主题切换时由框架自动更新。仅外观，不参与状态判定。
    /// </summary>
    partial void OnSessionStateRowCreated(string sessionId, ProgressRing working, Ellipse attention, TextBlock hint);

    private void InitializeSessionStateUi()
    {
        Loaded += SessionStateUi_Loaded;
        Unloaded += SessionStateUi_Unloaded;
    }

    private void SessionStateUi_Loaded(object sender, RoutedEventArgs e)
    {
        SubscribeSessionStateUi();
        QueueSessionStateUiRefresh();
    }

    private void SessionStateUi_Unloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeSessionStateUi();
        ClearSessionStateIndicators();
    }

    private void SubscribeSessionStateUi()
    {
        if (_sessionStateApp is not null || Application.Current is not App app) return;
        _sessionStateApp = app;
        app.OpenCodeSessionUiStateChanged += SessionStateUi_Changed;
        app.OpenCodeSourceChanged += SessionStateUi_SourceChanged;
    }

    private void UnsubscribeSessionStateUi()
    {
        if (_sessionStateApp is not { } app) return;
        app.OpenCodeSessionUiStateChanged -= SessionStateUi_Changed;
        app.OpenCodeSourceChanged -= SessionStateUi_SourceChanged;
        _sessionStateApp = null;
    }

    private void SessionStateUi_SourceChanged(object? sender, EventArgs e)
    {
        if (!DispatcherQueue.HasThreadAccess) return;
        UnsubscribeSessionStateUi();
        ClearSessionStateIndicators();
        if (_pageActive) SubscribeSessionStateUi();
        QueueSessionStateUiRefresh();
    }

    private void SessionStateUi_Changed(object? sender, OpenCodeSessionUiStateChangedEventArgs e)
    {
        if (!DispatcherQueue.HasThreadAccess || !_pageActive || !ReferenceEquals(sender, _sessionStateApp)
            || _source is not { IsRevoked: false } source || source.Revision != _sourceRevision
            || !ReferenceEquals(source, _sessionStateApp?.CurrentOpenCodePageSource)
            || !string.Equals(source.Identity, e.EndpointIdentity, StringComparison.Ordinal)
            || e.Revision != _sourceRevision) return;
        if (ReferenceEquals(source, _sessionStateSource) && _sessionStateRevision == e.Revision
            && e.Version <= _sessionStateVersion) return;
        // Do not apply the event payload: the getter rechecks source-object ownership on the UI thread.
        QueueSessionStateUiRefresh();
    }

    private void QueueSessionStateUiRefresh()
    {
        if (!DispatcherQueue.HasThreadAccess || _sessionStateRefreshQueued || !_pageActive) return;
        _sessionStateRefreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _sessionStateRefreshQueued = false;
            RefreshSessionStateUi();
        })) _sessionStateRefreshQueued = false;
    }

    private void ClearSessionStateIndicators()
    {
        _sessionStateSource = null;
        _sessionStateRevision = 0;
        _sessionStateVersion = -1;
        _selectedSessionWorkingIndicated = false;
        if (SessionStateHint is not null) SetSessionStateHint(SessionStateHint, string.Empty);
        foreach (var row in _sessionStateRows.Values)
        {
            row.Working.IsActive = false;
            row.Working.Visibility = Visibility.Collapsed;
            row.Attention.Visibility = Visibility.Collapsed;
            SetSessionStateHint(row.Hint, string.Empty, compact: true);
        }
        OnSessionUiStatesApplied(NoSessionUiStates);
    }

    private void RefreshSessionStateUi()
    {
        if (!DispatcherQueue.HasThreadAccess) return;
        OpenCodeSessionUiSnapshot? snapshot = null;
        var source = _source;
        if (!_pageActive || source is not { IsRevoked: false } || source.Revision != _sourceRevision
            || _sessionStateApp is not { } app || !ReferenceEquals(source, app.CurrentOpenCodePageSource))
        {
            ClearSessionStateIndicators();
            return;
        }
        else
            snapshot = app.GetOpenCodeSessionUiSnapshot(source, _sourceRevision);
        if (snapshot is null)
        {
            ClearSessionStateIndicators();
        }
        if (snapshot is not null && ReferenceEquals(source, _sessionStateSource) && _sessionStateRevision == snapshot.Revision
            && snapshot.Version < _sessionStateVersion) return;
        _sessionStateSource = source;
        _sessionStateRevision = source.Revision;
        _sessionStateVersion = snapshot?.Version ?? -1;

        var states = new Dictionary<string, OpenCodeSessionUiState>(StringComparer.Ordinal);
        if (snapshot is not null)
            foreach (var state in snapshot.Sessions) states[state.SessionId] = state;
        ApplyLiveSessionStatuses(states);
        OpenCodeSessionUiState? current = null;
        if (_selectedSessionId is { } selected) states.TryGetValue(selected, out current);
        _selectedSessionWorkingIndicated = current?.Working == true;
        SetSessionStateHint(SessionStateHint, _selectedSessionId is null ? string.Empty : DescribeSessionStateUnknown(current));

        var retained = new HashSet<ListViewItem>(_sessionItems.Values);
        var removed = new List<ListViewItem>();
        foreach (var pair in _sessionStateRows)
        {
            if (retained.Contains(pair.Key)) continue;
            pair.Value.Working.IsActive = false;
            removed.Add(pair.Key);
        }
        foreach (var item in removed) _sessionStateRows.Remove(item);
        foreach (var pair in _sessionItems)
        {
            if (!_sessionStateRows.TryGetValue(pair.Value, out var row))
            {
                if (pair.Value.Content is not FrameworkElement content) continue;
                var ring = new ProgressRing { Width = 12, Height = 12, IsActive = false, Visibility = Visibility.Collapsed };
                // 尺寸与填充色（SystemFillColorAttentionBrush）由 OnSessionStateRowCreated 套用的 ThemeResource 样式提供。
                var dot = new Ellipse { VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
                AutomationProperties.SetAutomationId(ring, "OpenCodeSessionWorking." + pair.Key);
                AutomationProperties.SetName(ring, "会话工作中");
                AutomationProperties.SetAutomationId(dot, "OpenCodeSessionAttention." + pair.Key);
                AutomationProperties.SetName(dot, "会话需要注意");
                // 字号与次要文字色由 OnSessionStateRowCreated 套用内置 Caption 样式提供（不再写死 FontSize）。
                var hint = new TextBlock
                {
                    MaxWidth = 72, TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                    Visibility = Visibility.Collapsed,
                };
                AutomationProperties.SetAutomationId(hint, "OpenCodeSessionStateHint." + pair.Key);
                var indicators = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center,
                    Children = { ring, dot, hint },
                };
                var wrapper = new Grid();
                wrapper.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                wrapper.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                pair.Value.Content = null;
                wrapper.Children.Add(content);
                Grid.SetColumn(indicators, 1);
                wrapper.Children.Add(indicators);
                pair.Value.Content = wrapper;
                row = new(ring, dot, hint);
                _sessionStateRows.Add(pair.Value, row);
                OnSessionStateRowCreated(pair.Key, ring, dot, hint);
            }
            states.TryGetValue(pair.Key, out var state);
            var rowWorking = state?.Working == true;
            row.Working.IsActive = rowWorking;
            row.Working.Visibility = rowWorking ? Visibility.Visible : Visibility.Collapsed;
            row.Attention.Visibility = state?.NeedsAttention == true ? Visibility.Visible : Visibility.Collapsed;
            OnSessionStateRowToneApplied(row.Working, ClassifySessionIndicator(state));
            var rowHint = DescribeSessionStateUnknown(state);
            ApplyLiveSessionRowHint(pair.Key, ref rowHint);
            SetSessionStateHint(row.Hint, rowHint, compact: true);
        }
        OnSessionUiStatesApplied(states);
    }

    partial void ApplyLiveSessionStatuses(Dictionary<string, OpenCodeSessionUiState> states);
    partial void ApplyLiveSessionRowHint(string sessionId, ref string hint);

    private static string DescribeSessionStateUnknown(OpenCodeSessionUiState? state)
    {
        if (state is null) return "状态未知（尚未观测）";
        var reasons = new List<string>();
        if ((state.UnknownReason & OpenCodeStateUnknownReason.NotObserved) != 0) reasons.Add("尚未观测");
        if ((state.UnknownReason & OpenCodeStateUnknownReason.Expired) != 0) reasons.Add("信息已过期");
        if ((state.UnknownReason & OpenCodeStateUnknownReason.Incomplete) != 0) reasons.Add("信息不完整");
        if (reasons.Count == 0 && (state.Working is null || state.PendingAction is null)) reasons.Add("信息不完整");
        return reasons.Count == 0 ? string.Empty : "状态未知（" + string.Join("、", reasons) + "）";
    }

    private static void SetSessionStateHint(TextBlock hint, string text, bool compact = false)
    {
        if (compact)
        {
            hint.Text = text.Length == 0 ? string.Empty
                : text.Contains("、", StringComparison.Ordinal) ? "多项未知"
                : text.Contains("已过期", StringComparison.Ordinal) ? "已过期"
                : text.Contains("不完整", StringComparison.Ordinal) ? "不完整" : "未观测";
            ToolTipService.SetToolTip(hint, text.Length == 0 ? null : text);
        }
        else hint.Text = text;
        hint.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(hint, text);
    }
}
