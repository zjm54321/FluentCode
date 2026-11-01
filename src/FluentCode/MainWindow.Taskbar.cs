using System;
using System.Collections.Generic;
using System.Linq;
using FluentCode.Services.Taskbar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode;

/// <summary>
/// Shell 集成：任务栏任务跟踪与“任务状态”入口。
/// </summary>
/// <remarks>
/// <para>执行 ID 均为不透明标识（OpenCode 用 oc- 前缀 GUID，每次执行新建）。
/// 只映射真实观察到的状态：Working/Completed/Failed/Cancelled（OpenCode 回合）、
/// Pending（等待用户处理的真实 pending 请求）与 OutcomeUnknown（明确的提交结果未知）；
/// 绝不把 HTTP 接纳、页面切换、断线或历史记录当作完成/失败（不造业务状态）。</para>
/// <para>终态保持到显式确认：仅终态可在“任务状态”入口清除；等待处理/结果未知/进行中不可清除。
/// 本窗口不注册、不发送系统通知。</para>
/// </remarks>
public sealed partial class MainWindow
{
    private const int TrackedTaskCapacity = 128;
    private const int AcknowledgedTaskCapacity = 256;

    private enum TrackedTaskKind
    {
        OpenCode,
    }

    private sealed class TrackedTask
    {
        public TrackedTask(string id, TrackedTaskKind kind, string? sessionId, TaskbarTaskState state)
        {
            Id = id;
            Kind = kind;
            SessionId = sessionId;
            State = state;
        }

        public string Id { get; }

        public TrackedTaskKind Kind { get; }

        public string? SessionId { get; }

        public TaskbarTaskState State { get; set; }

        public bool IsTerminal => State is TaskbarTaskState.Completed or TaskbarTaskState.Failed or TaskbarTaskState.Cancelled;
    }

    private readonly List<TrackedTask> _trackedTaskList = new();
    private readonly Dictionary<string, TrackedTask> _trackedTaskIndex = new(StringComparer.Ordinal);
    private readonly HashSet<string> _acknowledgedTaskIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _acknowledgedTaskOrder = new();
    // ---- 转发与登记 ----

    /// <summary>登记并把一次真实状态观察转发给 App 的任务栏聚合（App 记录结果；此处不自动确认、不伪造完成）。</summary>
    private void ForwardTaskbarTask(string taskId, TrackedTaskKind kind, string? sessionId, TaskbarTaskState state)
    {
        if (!AppCallsAllowed)
        {
            return;
        }

        if (_acknowledgedTaskIds.Contains(taskId))
        {
            return; // 已确认的执行：不再转发迟到事件（聚合不保留无界 tombstone）。
        }

        UpsertTrackedTask(taskId, kind, sessionId, state);
        _app!.UpdateTaskbarTask(taskId, state);
    }

    private void UpsertTrackedTask(string taskId, TrackedTaskKind kind, string? sessionId, TaskbarTaskState state)
    {
        if (_trackedTaskIndex.TryGetValue(taskId, out TrackedTask? existing))
        {
            existing.State = state;
            return;
        }

        if (_trackedTaskList.Count >= TrackedTaskCapacity)
        {
            TrackedTask? victim = _trackedTaskList.FirstOrDefault(static t => t.IsTerminal) ?? _trackedTaskList[0];
            _trackedTaskList.Remove(victim);
            _trackedTaskIndex.Remove(victim.Id);
        }

        var record = new TrackedTask(taskId, kind, sessionId, state);
        _trackedTaskList.Add(record);
        _trackedTaskIndex[taskId] = record;
    }

    private bool IsAcknowledged(string taskId) => _acknowledgedTaskIds.Contains(taskId);

    // ---- OpenCode 监视语义 → 任务栏 ----

    /// <summary>会话读取失败/缺口：仅标记该会话非终态执行的 freshness（不改写状态）。</summary>
    private void MarkOpenCodeSessionObservationsStale(string sessionId)
    {
        if (!AppCallsAllowed || string.IsNullOrEmpty(sessionId))
        {
            return;
        }

        foreach (TrackedTask task in _trackedTaskList)
        {
            if (task.Kind == TrackedTaskKind.OpenCode && !task.IsTerminal &&
                string.Equals(task.SessionId, sessionId, StringComparison.Ordinal) && !IsAcknowledged(task.Id))
            {
                _app!.UpdateTaskbarObservation(task.Id, true);
            }
        }
    }

    /// <summary>来源换代/撤销：旧端点全部非终态执行失去观察，仅标记 freshness。</summary>
    private void MarkOpenCodeEndpointTasksStale()
    {
        if (!AppCallsAllowed)
        {
            return;
        }

        foreach (TrackedTask task in _trackedTaskList)
        {
            if (task.Kind == TrackedTaskKind.OpenCode && !task.IsTerminal && !IsAcknowledged(task.Id))
            {
                _app!.UpdateTaskbarObservation(task.Id, true);
            }
        }
    }

    // ---- “任务状态”入口（仅终态可显式确认清除） ----

    private void TaskFlyout_Opened(object? sender, object e) => RebuildTaskStatusList();

    private void TaskStatusClearCompleted_Click(object sender, RoutedEventArgs e)
    {
        // 仅确认终态；等待处理/结果未知/进行中一律保留（不能清除）。
        foreach (TrackedTask task in _trackedTaskList.Where(static t => t.IsTerminal).ToArray())
        {
            AcknowledgeTrackedTask(task.Id);
        }

        RebuildTaskStatusList();
    }

    private void AcknowledgeTrackedTask(string taskId)
    {
        if (!AppCallsAllowed)
        {
            return;
        }

        if (!_trackedTaskIndex.TryGetValue(taskId, out TrackedTask? task) || !task.IsTerminal)
        {
            return; // 仅终态可确认。
        }

        TaskbarOperationResult result = _app!.AcknowledgeTaskbarTask(taskId);
        if (!result.Succeeded)
        {
            return; // 未成功提交确认：保留记录，不虚报已清除。
        }

        _trackedTaskList.Remove(task);
        _trackedTaskIndex.Remove(taskId);
        RememberAcknowledged(taskId);
        RebuildTaskStatusList();
    }

    private void RememberAcknowledged(string taskId)
    {
        if (_acknowledgedTaskIds.Add(taskId))
        {
            _acknowledgedTaskOrder.Enqueue(taskId);
        }

        while (_acknowledgedTaskIds.Count > AcknowledgedTaskCapacity)
        {
            _acknowledgedTaskIds.Remove(_acknowledgedTaskOrder.Dequeue());
        }
    }

    private void RebuildTaskStatusList()
    {
        TaskStatusList.Children.Clear();
        TaskStatusEmptyHint.Visibility = _trackedTaskList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TaskStatusClearCompleted.IsEnabled = _trackedTaskList.Any(static t => t.IsTerminal);

        foreach (TrackedTask task in _trackedTaskList)
        {
            TaskStatusList.Children.Add(BuildTaskRow(task));
        }
    }

    private UIElement BuildTaskRow(TrackedTask task)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = "OpenCode 任务",
            FontSize = 13,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "会话：" + (string.IsNullOrEmpty(task.SessionId) ? "（未知）" : task.SessionId) +
                "　状态：" + DescribeTaskState(task.State),
            FontSize = 12,
            Opacity = 0.75,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        if (task.IsTerminal)
        {
            var acknowledge = new Button
            {
                Content = "确认已读（清除）",
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            AutomationProperties.SetAutomationId(acknowledge, "TaskAcknowledge");
            string id = task.Id;
            acknowledge.Click += (_, _) => AcknowledgeTrackedTask(id);
            panel.Children.Add(acknowledge);
        }

        return panel;
    }

    private static string DescribeTaskState(TaskbarTaskState state) => state switch
    {
        TaskbarTaskState.Working => "进行中",
        TaskbarTaskState.Pending => "等待处理",
        TaskbarTaskState.OutcomeUnknown => "结果未知（请重新查询）",
        TaskbarTaskState.Completed => "已完成",
        TaskbarTaskState.Failed => "失败",
        _ => "已取消",
    };
}
