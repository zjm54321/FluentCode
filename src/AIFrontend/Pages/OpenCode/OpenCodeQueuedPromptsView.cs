using System;
using System.Collections.Generic;
using System.Text.Json;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// 排队条（重设计 v3 §9.1）：列出本会话尚未投递的 inbox 条目及其投递方式，位于输入区上方。
/// </summary>
/// <remarks>
/// 数据来自页面已有的 <c>GET /api/session/{id}/inbox</c> 读取（条目仍在 inbox = 尚未投递）。
/// 本页发出的用户输入每行有原生“撤回”按钮（AutomationId <c>OpenCodeQueuedPromptWithdraw.{id}</c>）：
/// 视图只发出 <see cref="WithdrawRequested"/>；DELETE、新鲜复查与是否恢复到输入框由页面经
/// <see cref="OpenCodeQueuedPromptWithdrawal"/> 判定（204 不能证明撤回成功，绝不自动重发）。
/// </remarks>
internal sealed class OpenCodeQueuedPromptsView : Grid
{
    private const int PreviewLength = 120;

    // Expander is a WinUI library control, not a composable native base class.
    // Inheriting it creates a ContentControl native peer: its Expander template then
    // throws XAML HRESULT 0x800F1000 as soon as the first queued row becomes visible.
    private readonly Expander _expander = new();
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly TextBlock _header;
    private readonly List<Button> _withdrawButtons = new();
    private string _signature = string.Empty;
    private bool _withdrawBusy;

    /// <summary>用户点击某行“撤回”（参数为 inbox 条目 id）。</summary>
    internal event EventHandler<string>? WithdrawRequested;

    internal OpenCodeQueuedPromptsView()
    {
        AutomationProperties.SetAutomationId(_expander, "OpenCodeQueuedPrompts");
        HorizontalAlignment = HorizontalAlignment.Stretch;
        _expander.HorizontalAlignment = HorizontalAlignment.Stretch;
        _expander.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _expander.IsExpanded = true;
        Visibility = Visibility.Collapsed;

        var icon = new FontIcon { Glyph = "\uE823", FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        _header = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.BodyStyleKey);
        _header.VerticalAlignment = VerticalAlignment.Center;
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(icon);
        header.Children.Add(_header);
        _expander.Header = header;
        _expander.Content = _list;
        Children.Add(_expander);
    }

    /// <summary>是否有可显示的排队条目。</summary>
    internal bool HasItems => _list.Children.Count > 0;

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
        }
    }

    /// <summary>
    /// 用最新 inbox 快照重绘；null 或空表示无排队条目（视图折叠）。
    /// <paramref name="canWithdraw"/> 判定某条目是否显示“撤回”（页面只对本页发出的用户输入返回 true）。
    /// </summary>
    internal void Apply(IReadOnlyList<OpenCodeInboxItem>? items, Func<OpenCodeInboxItem, bool>? canWithdraw = null)
    {
        var rows = new List<(string Id, string Text, string Delivery, bool Withdrawable)>();
        if (items is not null)
        {
            foreach (var item in items)
            {
                if (item?.Id is not { Length: > 0 } id)
                {
                    continue;
                }

                var withdrawable = item.Type == OpenCodeInboxItemTypes.User && canWithdraw?.Invoke(item) == true;
                rows.Add((id, DescribeContent(item), DescribeDelivery(item.Delivery), withdrawable));
            }
        }

        var signature = string.Join("\u001f", rows.ConvertAll(static r => r.Id + "\u001e" + r.Text + "\u001e" + r.Delivery + "\u001e" + r.Withdrawable));
        if (signature == _signature)
        {
            return;
        }

        _signature = signature;
        _list.Children.Clear();
        _withdrawButtons.Clear();
        foreach (var (id, text, delivery, withdrawable) in rows)
        {
            _list.Children.Add(CreateRow(id, text, delivery, withdrawable));
        }

        _header.Text = "排队中 " + rows.Count + " 条";
        AutomationProperties.SetName(_expander, "排队中的消息（" + rows.Count + " 条，尚未投递）");
        Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>投递方式的用户可读说明（steer = 插入当前回合；queue = 当前回合结束后发送）。</summary>
    internal static string DescribeDelivery(string? delivery) => delivery switch
    {
        OpenCodeDeliveryModes.Steer => "将插入当前回合",
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
                    return Preview(text);
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

    private static string? ReadText(JsonElement? payload)
    {
        try
        {
            if (payload is { ValueKind: JsonValueKind.Object } p
                && p.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString()?.Trim();
            }
        }
        catch (InvalidOperationException)
        {
            // 负载形状异常：按无文本处理。
        }

        return null;
    }

    private static string Preview(string text)
    {
        var singleLine = text.ReplaceLineEndings(" ");
        return singleLine.Length <= PreviewLength ? singleLine : singleLine[..PreviewLength] + "…";
    }

    private FrameworkElement CreateRow(string id, string text, string delivery, bool withdrawable)
    {
        var grid = new Grid { ColumnSpacing = 12 };
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
            AutomationProperties.SetName(withdraw, "撤回排队消息：" + text);
            ToolTipService.SetToolTip(withdraw, "撤回这条尚未发送的消息；确认未发送后放回输入框");
            withdraw.Click += (_, _) =>
            {
                if (!_withdrawBusy)
                {
                    WithdrawRequested?.Invoke(this, id);
                }
            };
            Grid.SetColumn(withdraw, 2);
            grid.Children.Add(withdraw);
            _withdrawButtons.Add(withdraw);
        }

        var body = OpenCodeInteractionUi.StyledText(text, OpenCodeInteractionUi.BodyStyleKey, selectable: true);
        body.TextWrapping = TextWrapping.NoWrap;
        body.TextTrimming = TextTrimming.CharacterEllipsis;
        body.MaxLines = 1;
        body.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(body, text);
        grid.Children.Add(body);

        var mode = OpenCodeInteractionUi.StyledText(delivery, OpenCodeInteractionUi.CaptionSubtleStyleKey);
        mode.VerticalAlignment = VerticalAlignment.Center;
        mode.TextWrapping = TextWrapping.NoWrap;
        Grid.SetColumn(mode, 1);
        grid.Children.Add(mode);

        AutomationProperties.SetAutomationId(grid, "OpenCodeQueuedPrompt." + id);
        AutomationProperties.SetName(grid, text + "，" + delivery);
        return grid;
    }
}
