using System;
using System.Collections.Generic;
using System.Text.Json;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// 输入区上方的待投递用户消息列表：保留 inbox 顺序，不显示系统生成的工作项。
/// </summary>
/// <remarks>
/// 数据来自页面已有的 <c>GET /api/session/{id}/inbox</c> 读取（条目仍在 inbox = 尚未投递）。
/// 本页发出的用户输入每行有原生“撤回”按钮（AutomationId <c>OpenCodeQueuedPromptWithdraw.{id}</c>）：
/// 视图只发出 <see cref="WithdrawRequested"/>；DELETE、新鲜复查与是否恢复到输入框由页面经
/// <see cref="OpenCodeQueuedPromptWithdrawal"/> 判定（204 不能证明撤回成功，绝不自动重发）。
/// 可切换的条目每行常驻一个只有图标的“改为插入当前回合”按钮（AutomationId <c>OpenCodePendingDelivery.{id}</c>），
/// 与待投递气泡左侧的“改为排队”按钮成对；右键菜单提供同一项。视图只发出 <see cref="DeliveryChangeRequested"/>。
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
    private readonly List<JsonElement?> _renderedPayloads = new();
    private readonly List<OpenCodeUserAttachmentsView> _attachments = new();
    private bool _withdrawBusy;
    // Segoe Fluent "Send"（纸飞机）: the glyph the composer's send button shows for sending into the current turn.
    // Same literal as OpenCodeComposerSendPresentation.Glyph (OpenCodeComposerCommandMenu.cs) and SendStopIcon in
    // OpenCodePage.xaml. Its opposite, the queue clock E823, is OpenCodeConversationView.QueueGlyph.
    private const string SteerGlyph = "\uE724";
    // One wording for the button's tooltip, the start of its Narrator name and the menu entry.
    private const string SteerSwitchLabel = "改为插入当前回合";

    /// <summary>用户点击某行“撤回”（参数为 inbox 条目 id）。</summary>
    internal event EventHandler<string>? WithdrawRequested;
    internal event EventHandler<OpenCodePendingDeliveryRequest>? DeliveryChangeRequested;
    internal event EventHandler? BusyChanged;
    internal void RequestWithdraw(string id) { if (!_withdrawBusy) WithdrawRequested?.Invoke(this, id); }
    internal void RequestDeliveryChange(OpenCodePendingDeliveryRequest request) => DeliveryChangeRequested?.Invoke(this, request);
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

    internal Button? WithdrawButton(string id) => _withdrawButtons.Find(button =>
        AutomationProperties.GetAutomationId(button) == "OpenCodeQueuedPromptWithdraw." + id);

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
    /// <paramref name="canChangeDelivery"/> 判定是否提供“改为插入当前回合”（按钮与右键菜单项；页面对服务端仍列出的任一条用户输入返回 true，不限发送方）。
    /// </summary>
    internal void Apply(IReadOnlyList<OpenCodeInboxItem>? items, Func<OpenCodeInboxItem, bool>? canWithdraw = null,
        Func<OpenCodeInboxItem, bool>? canChangeDelivery = null)
    {
        var rows = new List<(string Id, string Text, string Delivery, bool Withdrawable, bool Changeable, OpenCodeInboxItem Item)>();
        if (items is not null)
        {
            foreach (var item in items)
            {
                // Session.Inbox.Info also contains synthetic (including background completions),
                // compaction and move work. Only the explicit user variant belongs in this panel/count.
                if (item?.Type != OpenCodeInboxItemTypes.User || item.Id is not { Length: > 0 } id)
                {
                    continue;
                }
                if (IsSteerUser(item)) continue; // Admitted steer is projected as chat by ConversationView, not a lower pending row.

                var withdrawable = canWithdraw?.Invoke(item) == true;
                rows.Add((id, DescribeContent(item), item.Delivery ?? string.Empty, withdrawable,
                    canChangeDelivery?.Invoke(item) == true, item));
            }
        }

        var signature = string.Join("\u001f", rows.ConvertAll(static r => r.Id + "\u001e" + r.Text + "\u001e" + r.Delivery + "\u001e" + r.Withdrawable + "\u001e" + r.Changeable));
        var samePayloads = rows.Count == _renderedPayloads.Count;
        for (var i = 0; samePayloads && i < rows.Count; i++)
            samePayloads = rows[i].Item.Payload is { } current && _renderedPayloads[i] is { } previous
                ? JsonElement.DeepEquals(current, previous) : rows[i].Item.Payload is null && _renderedPayloads[i] is null;
        if (signature == _signature && samePayloads)
        {
            return;
        }

        var firstItems = !_hasItems;
        _signature = signature;
        foreach (var attachments in _attachments) attachments.Dispose();
        _attachments.Clear();
        _renderedPayloads.Clear();
        _list.Items.Clear();
        _withdrawButtons.Clear();
        _deliveryButtons.Clear();
        foreach (var (id, text, delivery, withdrawable, changeable, item) in rows)
        {
            _renderedPayloads.Add(item.Payload);
            _list.Items.Add(CreateRow(id, text, delivery, withdrawable, changeable, _list.Items.Count + 1, item));
        }
        _hasItems = rows.Count > 0;
        if (firstItems) _expander.IsExpanded = rows.Count == 1;
        _header.Text = rows.Count == 0 ? string.Empty : "排队 " + rows.Count + " 条 · 下一条：" + rows[0].Text;
        ToolTipService.SetToolTip(_expander, "当前回合结束后发送");
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
        var text = ReadText(item.Payload);
        return text is { Length: > 0 } ? text : "（仅附件）";
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

    private FrameworkElement CreateRow(string id, string text, string delivery, bool withdrawable, bool changeable, int number, OpenCodeInboxItem item)
    {
        // Transparent (not null) background: the whole row, gaps included, takes right-click / press-and-hold for the
        // row's menu and is the touch target that reveals 撤回.
        var grid = new Grid { ColumnSpacing = 12, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // Fixed slot for the delivery switch: the message keeps its width and wrapping whether or not the switch is shown.
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Button? withdraw = null;
        if (withdrawable)
        {
            // The page morphs this acting button only after its fresh inbox/message read confirms withdrawal.
            withdraw = new Button
            {
                Content = "撤回",
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = !_withdrawBusy,
            };
            AutomationProperties.SetAutomationId(withdraw, "OpenCodeQueuedPromptWithdraw." + id);
            AutomationProperties.SetName(withdraw, "撤回待投递消息：" + text);
            ToolTipService.SetToolTip(withdraw, "撤回");
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

        var files = OpenCodeUserAttachments.FromInbox(item);
        var rawText = ReadText(item.Payload);
        var presenter = new OpenCodeRichTextPresenter { AttachmentCounts = OpenCodeUserAttachments.Counts(files) };
        presenter.Render(files.Count == 0 ? text : rawText, OpenCodeRichTextStyle.Body);
        var attachments = new OpenCodeUserAttachmentsView();
        attachments.Update(files, !string.IsNullOrEmpty(rawText));
        _attachments.Add(attachments);
        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(presenter.Element);
        body.Children.Add(attachments.Element);
        ToolTipService.SetToolTip(body, text);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        var ordinal = OpenCodeInteractionUi.StyledText(number.ToString(), OpenCodeInteractionUi.CaptionSubtleStyleKey);
        ordinal.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(ordinal);

        if (changeable)
        {
            // Icon only, always visible: the counterpart of the pending bubble's 改为排队 clock, with the same 32px
            // size and borderless stock style, so the two directions read as a pair. It only dims while an inbox
            // action is running. When the entry cannot be switched the slot stays empty and nothing else moves.
            var change = new Button
            {
                Content = new FontIcon { Glyph = SteerGlyph, FontSize = 16 },
                Width = 32, Height = 32, Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Style = OpenCodeInteractionUi.TryStyle("SubtleButtonStyle"),
                IsEnabled = !_withdrawBusy,
            };
            change.Click += (_, _) => RequestDeliveryChange(new(id, OpenCodeDeliveryModes.Steer));
            AutomationProperties.SetAutomationId(change, "OpenCodePendingDelivery." + id);
            // Several entries can be queued at once, so the name says which one this button moves.
            AutomationProperties.SetName(change, SteerSwitchLabel + "：" + (rawText is { Length: > 0 } ? rawText : "附件消息"));
            ToolTipService.SetToolTip(change, SteerSwitchLabel);
            Grid.SetColumn(change, 2);
            grid.Children.Add(change);
            _deliveryButtons.Add(change);
        }

        // Same shared menu shape as transcript messages and the pending bubble: copy row on the text, then this
        // entry's own actions. Native context input covers right-click, Shift+F10 / Menu key and touch press-and-hold.
        var menu = OpenCodeMessageContextMenu.Create(() => rawText, actions: () => RowActions(grid, id, changeable));
        presenter.TextContextFlyout = menu;
        grid.ContextFlyout = menu;

        // A hidden 撤回 is not hit-testable, so a finger can never fire it while invisible; the first touch on the
        // row reveals (and keeps) it. Keyboard focus still reaches it and reveals it. The switch is never hidden.
        if (withdraw is { } hidden)
            OpenCodeTouchInput.WireReveal(grid, visible => { hidden.Opacity = visible ? 1 : 0; hidden.IsHitTestVisible = visible; });

        AutomationProperties.SetAutomationId(grid, "OpenCodeQueuedPrompt." + id);
        AutomationProperties.SetName(grid, text + "，" + DescribeDelivery(delivery));
        return grid;
    }

    // Read when the menu opens and re-checked when an entry runs. The entry follows the button: offered only for an
    // entry that can be switched, unavailable while an inbox action is running or once the row has been replaced.
    private IEnumerable<OpenCodeContextCommand> RowActions(Grid row, string id, bool changeable)
    {
        bool Current() => !_withdrawBusy && _list.Items.Contains(row);
        if (changeable)
            yield return new(SteerSwitchLabel, SteerGlyph, () =>
            {
                if (Current()) RequestDeliveryChange(new(id, OpenCodeDeliveryModes.Steer));
            }) { Enabled = Current(), AutomationId = "OpenCodeQueuedPromptMenuDelivery." + id };
    }
}

internal sealed record OpenCodePendingDeliveryRequest(string InboxId, string Delivery);
