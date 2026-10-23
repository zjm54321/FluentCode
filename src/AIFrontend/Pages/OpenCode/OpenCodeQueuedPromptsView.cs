using System;
using System.Collections.Generic;
using System.Text.Json;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// 输入区上方的待投递消息平铺列表：保留 inbox 顺序，明确区分插入当前回合与回合结束后发送。
/// </summary>
/// <remarks>
/// 数据来自页面已有的 <c>GET /api/session/{id}/inbox</c> 读取（条目仍在 inbox = 尚未投递）。
/// 本页发出的用户输入每行有原生“撤回”按钮（AutomationId <c>OpenCodeQueuedPromptWithdraw.{id}</c>）：
/// 视图只发出 <see cref="WithdrawRequested"/>；DELETE、新鲜复查与是否恢复到输入框由页面经
/// <see cref="OpenCodeQueuedPromptWithdrawal"/> 判定（204 不能证明撤回成功，绝不自动重发）。
/// </remarks>
internal sealed class OpenCodeQueuedPromptsView : Grid
{
    private readonly ScrollViewer _scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalScrollMode = ScrollMode.Disabled,
        MaxHeight = 240,
    };
    private readonly ListView _list = new()
    {
        SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = false,
        CanReorderItems = false, CanDragItems = false, AllowDrop = false,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };
    private readonly Expander _expander = new()
    {
        ExpandDirection = ExpandDirection.Up, HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };
    private readonly TextBlock _header = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.BodyStyleKey);
    private bool _hasItems;
    private readonly List<Button> _withdrawButtons = new();
    private readonly List<Button> _deliveryButtons = new();
    private string _signature = string.Empty;
    private bool _withdrawBusy;

    /// <summary>用户点击某行“撤回”（参数为 inbox 条目 id）。</summary>
    internal event EventHandler<string>? WithdrawRequested;
    internal event EventHandler<OpenCodePendingDeliveryRequest>? DeliveryChangeRequested;
    internal event EventHandler? BusyChanged;
    internal void RequestWithdraw(string id) { if (!_withdrawBusy) WithdrawRequested?.Invoke(this, id); }
    internal void RequestDeliveryChange(OpenCodePendingDeliveryRequest request) { if (!_withdrawBusy) DeliveryChangeRequested?.Invoke(this, request); }
    internal static bool IsSteerUser(OpenCodeInboxItem item)
        => item.Type == OpenCodeInboxItemTypes.User && item.Delivery == OpenCodeDeliveryModes.Steer;

    internal OpenCodeQueuedPromptsView()
    {
        AutomationProperties.SetAutomationId(this, "OpenCodeQueuedPrompts");
        AutomationProperties.SetName(this, "待投递消息");
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Visibility = Visibility.Collapsed;
        _scroll.Content = _list;
        _expander.Content = _scroll;
        _expander.Header = _header;
        _header.TextWrapping = TextWrapping.NoWrap;
        _header.MaxLines = 1;
        _header.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetAutomationId(_expander, "OpenCodeQueueExpander");
        Children.Add(_expander);
    }

    /// <summary>是否有可显示的排队条目。</summary>
    internal bool HasItems => _hasItems;

    /// <summary>撤回进行中：禁用全部“撤回”按钮（单飞；结果出来前不能再点）。</summary>
    internal bool IsWithdrawBusy
    {
        get => _withdrawBusy;
        set
        {
            _withdrawBusy = value;
            foreach (var button in _withdrawButtons)
            {
                button.IsEnabled = !value;
            }
            foreach (var button in _deliveryButtons) button.IsEnabled = !value;
            BusyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 用最新 inbox 快照重绘；null 或空表示无排队条目（视图折叠）。
    /// <paramref name="canWithdraw"/> 判定某条目是否显示“撤回”（页面只对本页发出的用户输入返回 true）。
    /// </summary>
    internal void Apply(IReadOnlyList<OpenCodeInboxItem>? items, Func<OpenCodeInboxItem, bool>? canWithdraw = null,
        Func<OpenCodeInboxItem, bool>? canChangeDelivery = null)
    {
        var rows = new List<(string Id, string Text, string Delivery, bool Withdrawable, bool Changeable)>();
        if (items is not null)
        {
            foreach (var item in items)
            {
                if (item?.Id is not { Length: > 0 } id)
                {
                    continue;
                }
                if (IsSteerUser(item)) continue; // Admitted steer is projected as chat by ConversationView, not a lower pending row.

                var withdrawable = item.Type == OpenCodeInboxItemTypes.User && canWithdraw?.Invoke(item) == true;
                rows.Add((id, DescribeContent(item), item.Delivery ?? string.Empty, withdrawable,
                    item.Type == OpenCodeInboxItemTypes.User && canChangeDelivery?.Invoke(item) == true));
            }
        }

        var signature = string.Join("\u001f", rows.ConvertAll(static r => r.Id + "\u001e" + r.Text + "\u001e" + r.Delivery + "\u001e" + r.Withdrawable + "\u001e" + r.Changeable));
        if (signature == _signature)
        {
            return;
        }

        var firstItems = !_hasItems;
        _signature = signature;
        _list.Items.Clear();
        _withdrawButtons.Clear();
        _deliveryButtons.Clear();
        foreach (var (id, text, delivery, withdrawable, changeable) in rows)
        {
            _list.Items.Add(CreateRow(id, text, delivery, withdrawable, changeable, _list.Items.Count + 1));
        }
        _hasItems = rows.Count > 0;
        if (firstItems) _expander.IsExpanded = rows.Count == 1;
        _header.Text = rows.Count == 0 ? string.Empty : "排队 " + rows.Count + " 条 · 下一条：" + rows[0].Text;
        ToolTipService.SetToolTip(_expander, "当前回合结束后发送；顺序由服务器管理");
        Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>投递方式的用户可读说明（steer = 插入当前回合；queue = 当前回合结束后发送）。</summary>
    internal static string DescribeDelivery(string? delivery) => delivery switch
    {
        OpenCodeDeliveryModes.Steer => "插入当前回合",
        OpenCodeDeliveryModes.Queue => "当前回合结束后发送",
        null or "" => "等待投递",
        _ => "等待投递（" + delivery + "）",
    };

    private static string DescribeContent(OpenCodeInboxItem item)
    {
        switch (item.Type)
        {
            case OpenCodeInboxItemTypes.User:
            case OpenCodeInboxItemTypes.Synthetic:
                var text = ReadText(item.Payload);
                if (text is { Length: > 0 })
                {
                    return text;
                }

                return item.Type == OpenCodeInboxItemTypes.User ? "（仅附件）" : "（系统输入）";
            case OpenCodeInboxItemTypes.Compaction:
                return "压缩会话上下文";
            case OpenCodeInboxItemTypes.Move:
                return "移动会话";
            default:
                return item.Type is { Length: > 0 } type ? "（" + type + "）" : "（未知条目）";
        }
    }

    internal static string? ReadText(JsonElement? payload)
    {
        try
        {
            if (payload is { ValueKind: JsonValueKind.Object } p
                && p.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString();
            }
        }
        catch (InvalidOperationException)
        {
            // 负载形状异常：按无文本处理。
        }

        return null;
    }

    private FrameworkElement CreateRow(string id, string text, string delivery, bool withdrawable, bool changeable, int number)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (withdrawable)
        {
            // 原生默认按钮样式（不自绘）；结果由页面新鲜复查后以 InfoBar 告知。
            var withdraw = new Button
            {
                Content = "撤回",
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = !_withdrawBusy,
            };
            AutomationProperties.SetAutomationId(withdraw, "OpenCodeQueuedPromptWithdraw." + id);
            AutomationProperties.SetName(withdraw, "撤回待投递消息：" + text);
            ToolTipService.SetToolTip(withdraw, "撤回这条尚未发送的消息；确认未发送后放回输入框");
            withdraw.Click += (_, _) =>
            {
                if (!_withdrawBusy)
                {
                    WithdrawRequested?.Invoke(this, id);
                }
            };
            Grid.SetColumn(withdraw, 3);
            grid.Children.Add(withdraw);
            _withdrawButtons.Add(withdraw);
        }

        var body = OpenCodeInteractionUi.StyledText(text, OpenCodeInteractionUi.BodyStyleKey, selectable: true);
        body.TextWrapping = TextWrapping.NoWrap;
        body.MaxLines = 1;
        body.TextTrimming = TextTrimming.CharacterEllipsis;
        body.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(body, text);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        var ordinal = OpenCodeInteractionUi.StyledText(number.ToString(), OpenCodeInteractionUi.CaptionSubtleStyleKey);
        ordinal.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(ordinal);

        var mode = OpenCodeInteractionUi.StyledText(DescribeDelivery(delivery), OpenCodeInteractionUi.CaptionSubtleStyleKey);
        mode.VerticalAlignment = VerticalAlignment.Center;
        mode.TextWrapping = TextWrapping.Wrap;
        FrameworkElement deliveryControl = mode;
        if (changeable)
        {
            var change = new Button { Content = "改为插入当前回合", IsEnabled = !_withdrawBusy };
            change.Click += (_, _) => { if (!_withdrawBusy) DeliveryChangeRequested?.Invoke(this, new(id, OpenCodeDeliveryModes.Steer)); };
            AutomationProperties.SetAutomationId(change, "OpenCodePendingDelivery." + id);
            AutomationProperties.SetName(change, "修改投递方式：" + DescribeDelivery(delivery));
            _deliveryButtons.Add(change);
            deliveryControl = change;
        }
        Grid.SetColumn(deliveryControl, 2);
        grid.Children.Add(deliveryControl);

        void ShowActions(double opacity)
        {
            foreach (var child in grid.Children) if (child is Button action) action.Opacity = opacity;
        }
        ShowActions(0);
        grid.PointerEntered += (_, _) => ShowActions(1);
        grid.PointerExited += (_, _) => ShowActions(0);
        grid.GotFocus += (_, _) => ShowActions(1);
        grid.LostFocus += (_, _) => ShowActions(0);

        AutomationProperties.SetAutomationId(grid, "OpenCodeQueuedPrompt." + id);
        AutomationProperties.SetName(grid, text + "，" + DescribeDelivery(delivery));
        return grid;
    }
}

internal sealed record OpenCodePendingDeliveryRequest(string InboxId, string Delivery);
