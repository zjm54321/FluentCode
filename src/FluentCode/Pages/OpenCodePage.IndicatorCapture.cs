using System;
using System.Collections.Generic;
using System.Linq;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace FluentCode.Pages;

public sealed partial class OpenCodePage
{
    // Test-only: no state override exists unless all three disposable-capture gates are present.
    internal static bool IndicatorCaptureEnabled => Environment.GetEnvironmentVariable("FC_INDICATOR_CAPTURE_TEST") == "1"
        && Environment.GetEnvironmentVariable("FC_SHELL_CAPTURE_TEST") == "1"
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FC_STARTUP_SMOKE_ROOT"));
    private IReadOnlyDictionary<string, OpenCodeSessionUiState>? _indicatorCaptureStates;

    internal int TestIndicatorTabCount => _sessionTabs.Count;
    internal double TestIndicatorHeaderHeight => SessionTabs.ActualHeight;

    internal void TestSeedIndicatorTabs(bool narrow)
    {
        if (!IndicatorCaptureEnabled) throw new InvalidOperationException("Indicator capture is not enabled.");
        if (_source is not null || _client is not null) throw new InvalidOperationException("Indicator capture must remain offline.");
        _pollTimer?.Stop();
        StopEventSync();
        UnsubscribeSessionStateUi();
        var fixture = new List<(string Id, string Title, OpenCodeSessionUiState State)>();
        OpenCodeSessionUiState State(string id) => new(id, false, false, false, null, null, OpenCodeStateUnknownReason.None);
        const string longTitle = "这是一个非常长的工作中会话标题，用于验证省略号与完整状态环不会相互裁剪";
        if (!narrow) fixture.Add(("ses_capture_working", "正在执行", State("ses_capture_working") with { Working = true }));
        else fixture.Add(("ses_capture_long", longTitle, State("ses_capture_long") with { Working = true }));
        fixture.Add(("ses_capture_background", "子智能体", State("ses_capture_background") with { BackgroundWorking = true, BackgroundChildCount = 2 }));
        fixture.Add(("ses_capture_retry", "正在重试", State("ses_capture_retry") with { Working = true, Retry = new(3, 1700000005000, "测试上游限流") }));
        fixture.Add(("ses_capture_failed", "执行失败", State("ses_capture_failed") with { Completion = new("failed", 100) }));
        fixture.Add(("ses_capture_interrupted", "用户中断", State("ses_capture_interrupted") with { Completion = new("interrupted", 100) }));
        fixture.Add(("ses_capture_waiting", "等待你", State("ses_capture_waiting") with { PendingAction = true }));
        fixture.Add(("ses_capture_unread", "新的回复", State("ses_capture_unread") with { Completion = new("succeeded", 100) }));
        fixture.Add(("ses_capture_idle", "空闲", State("ses_capture_idle")));
        if (!narrow) fixture.Add(("ses_capture_long", longTitle, State("ses_capture_long") with { Working = true }));

        _indicatorCaptureStates = fixture.ToDictionary(item => item.Id, item => item.State, StringComparer.Ordinal);
        _sessionTabStates = _indicatorCaptureStates;
        var previous = _suppressTabSelection;
        _suppressTabSelection = true;
        try
        {
            foreach (var entry in _sessionTabs.Values) entry.Status.Clear();
            _sessionTabs.Clear();
            _sessionTitles.Clear();
            SessionTabs.TabItems.Clear();
            SessionTabs.TabItems.Add(HomeTab); // Preserve the real 48 DIP Home tab footprint.
            foreach (var (id, title, _) in fixture)
            {
                _sessionTitles[id] = title;
                CreateSessionTab(id);
            }
            _selectedSessionId = _activeTabSessionId = fixture[0].Id;
            _homeActive = _newSessionDraftActive = false;
            SessionTabs.SelectedItem = _sessionTabs[fixture[0].Id].Item;
            VisualStateManager.GoToState(this, "SessionLayout", false);
            NotConfiguredPanel.Visibility = Visibility.Collapsed;
            WorkspaceGrid.Visibility = Visibility.Visible;
            SessionLayoutRoot.Visibility = Visibility.Visible;
            HomePageHost.Visibility = Visibility.Collapsed;
            SessionStateHint.Visibility = Visibility.Collapsed;
            EmptyStateText.Text = "离线标签头状态演示；没有连接服务端。";
            EmptyStateText.Visibility = Visibility.Visible;
            ComposerCell.Visibility = Visibility.Collapsed;
            foreach (var entry in _sessionTabs.Values) ApplySessionTabState(entry);
        }
        finally { _suppressTabSelection = previous; }
    }

    internal object[] TestIndicatorHeaderGeometry(UIElement root)
    {
        if (!IndicatorCaptureEnabled || _indicatorCaptureStates is null) throw new InvalidOperationException("Indicator capture fixture is absent.");
        return SessionTabs.TabItems.OfType<TabViewItem>().Where(item => item.Tag is string id && _sessionTabs.ContainsKey(id)).Select(item =>
        {
            var entry = _sessionTabs[(string)item.Tag];
            var bounds = item.TransformToVisual(root).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
            var slot = entry.Status.TransformToVisual(item).TransformBounds(new Rect(0, 0, entry.Status.ActualWidth, entry.Status.ActualHeight));
            var visible = entry.Status.Visibility == Visibility.Visible;
            if (visible && (slot.X < 0 || slot.Y < 0 || slot.Right > item.ActualWidth || slot.Bottom > item.ActualHeight))
                throw new InvalidOperationException("Native tab clips status slot: " + entry.SessionId);
            if (visible && (Math.Abs(entry.Status.ActualWidth - 16) > 0.1 || Math.Abs(entry.Status.ActualHeight - 16) > 0.1))
                throw new InvalidOperationException("Native status slot lost its 16 DIP geometry: " + entry.SessionId);
            return (object)new
            {
                entry.SessionId,
                entry.Title.Text,
                ItemStatus = AutomationProperties.GetItemStatus(item),
                X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height,
                SlotVisible = visible, SlotX = slot.X, SlotY = slot.Y,
                SlotWidth = entry.Status.ActualWidth, SlotHeight = entry.Status.ActualHeight,
                TitleWidth = entry.Title.ActualWidth, TitleTrimmed = entry.Title.IsTextTrimmed,
            };
        }).ToArray();
    }
}
