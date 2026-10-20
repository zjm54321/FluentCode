using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// 权限遮罩（重设计 v3 §5）：放在输入区同一单元格内、盖住输入卡片，一次显示一个待处理权限请求。
/// </summary>
/// <remarks>
/// <para>
/// 遮罩只托管 <see cref="OpenCodePermissionCard.Presenter"/>（可见呈现）；请求的状态机、指纹与回调仍归
/// <see cref="OpenCodePermissionCard"/>，由 <see cref="OpenCodeInteractionsView"/> 按服务端顺序增量维护。
/// 页面在每次交互区刷新后调用 <see cref="Sync"/>，遮罩据此决定显示哪一个（被导航聚焦的优先，否则第一个）。
/// </para>
/// <para>
/// 外观：底层不透明 <c>SolidBackgroundFillColorBaseBrush</c> 完全遮住输入框（输入框留在可视树中，仅禁用），
/// 上层卡片 <c>CardBackgroundFillColorDefaultBrush</c> + 卡片描边，顶部 2px 警示色细线。全部经
/// <c>{ThemeResource}</c> 引用，随浅色/深色/高对比度即时切换。Tab 焦点在遮罩内循环，不会落到被遮住的输入框。
/// </para>
/// </remarks>
internal sealed class OpenCodePermissionOverlay : Grid
{
    private readonly ContentControl _host;
    private readonly List<OpenCodePermissionCard> _cards = new();
    private OpenCodePermissionCard? _current;
    private string? _pinnedId;

    internal OpenCodePermissionOverlay()
    {
        AutomationProperties.SetAutomationId(this, "OpenCodePermissionOverlay");
        AutomationProperties.SetName(this, "需要你的许可");
        AutomationProperties.SetLandmarkType(this, AutomationLandmarkType.Custom);
        AutomationProperties.SetLocalizedLandmarkType(this, "权限请求");
        TabFocusNavigation = KeyboardNavigationMode.Cycle;
        XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;

        // 底层：不透明底色，完全盖住输入卡片（与输入卡片同圆角）。
        var baseLayer = OpenCodeThemedXaml.Load<Border>("Border",
            "Background=\"{ThemeResource SolidBackgroundFillColorBaseBrush}\" CornerRadius=\"{ThemeResource OverlayCornerRadius}\"")
            ?? new Border { CornerRadius = new CornerRadius(8) };
        AutomationProperties.SetAccessibilityView(baseLayer, AccessibilityView.Raw);
        Children.Add(baseLayer);

        // 卡片：卡片底色 + 描边。
        _host = new ContentControl
        {
            IsTabStop = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        var card = OpenCodeThemedXaml.Load<Border>("Border",
            "Background=\"{ThemeResource CardBackgroundFillColorDefaultBrush}\" BorderBrush=\"{ThemeResource CardStrokeColorDefaultBrush}\"" +
            " BorderThickness=\"1\" CornerRadius=\"{ThemeResource OverlayCornerRadius}\" Padding=\"16,14,16,12\"")
            ?? new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 14, 16, 12) };
        card.Child = _host;
        Children.Add(card);

        // 顶部 2px 警示色细线（不参与布局与命中测试）。
        var accent = OpenCodeThemedXaml.Load<Border>("Border",
            "Height=\"2\" VerticalAlignment=\"Top\" Margin=\"12,0,12,0\" CornerRadius=\"0,0,1,1\" IsHitTestVisible=\"False\"" +
            " Background=\"{ThemeResource SystemFillColorCautionBrush}\"");
        if (accent is not null)
        {
            AutomationProperties.SetAccessibilityView(accent, AccessibilityView.Raw);
            Children.Add(accent);
        }
    }

    /// <summary>当前待处理（可在遮罩中显示）的权限请求数。</summary>
    internal int Count => _cards.Count;

    /// <summary>当前显示的请求（无请求时为 null）。</summary>
    internal OpenCodePermissionCard? Current => _current;

    /// <summary>
    /// 按服务端顺序同步待处理请求；保持当前显示项（若仍存在），否则切到被导航请求或第一个。
    /// 返回遮罩中的请求数。
    /// </summary>
    internal int Sync(IEnumerable<OpenCodePermissionCard> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        var fresh = cards.Where(static c => c.Id.Length > 0).ToList();

        foreach (var removed in _cards.Except(fresh).ToList())
        {
            removed.FocusRequested -= Card_FocusRequested;
        }

        foreach (var added in fresh.Except(_cards).ToList())
        {
            added.FocusRequested += Card_FocusRequested;
        }

        _cards.Clear();
        _cards.AddRange(fresh);

        if (_pinnedId is not null && _cards.All(c => c.Id != _pinnedId))
        {
            _pinnedId = null;
        }

        var next = (_pinnedId is not null ? _cards.FirstOrDefault(c => c.Id == _pinnedId) : null)
            ?? (_current is not null && _cards.Contains(_current) ? _current : null)
            ?? _cards.FirstOrDefault();
        Show(next);

        for (var i = 0; i < _cards.Count; i++)
        {
            _cards[i].SetPosition(i + 1, _cards.Count);
        }

        return _cards.Count;
    }

    /// <summary>键盘焦点当前是否位于遮罩内。</summary>
    internal bool ContainsFocus()
    {
        if (XamlRoot is null)
        {
            return false;
        }

        try
        {
            var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            while (focused is not null)
            {
                if (ReferenceEquals(focused, this))
                {
                    return true;
                }

                focused = VisualTreeHelper.GetParent(focused);
            }
        }
        catch
        {
            // 焦点查询失败按“不在遮罩内”处理。
        }

        return false;
    }

    /// <summary>把焦点放到当前请求的呈现根（不聚焦任何决定按钮，避免误按 Enter）。</summary>
    internal bool FocusCurrent() => _current?.FocusPresenter() ?? false;

    private void Card_FocusRequested(object? sender, EventArgs e)
    {
        if (sender is OpenCodePermissionCard card && _cards.Contains(card))
        {
            _pinnedId = card.Id;
            Show(card);
        }
    }

    private void Show(OpenCodePermissionCard? card)
    {
        if (ReferenceEquals(_current, card) && ReferenceEquals(_host.Content, card?.Presenter))
        {
            return;
        }

        var hadFocus = ContainsFocus();
        _host.Content = null;
        _current = card;
        if (card is null)
        {
            return;
        }

        // 呈现根同一时刻只能有一个父元素：若仍挂在别处（理论上不会），先摘下。
        if (card.Presenter.Parent is ContentControl oldHost && !ReferenceEquals(oldHost, _host))
        {
            oldHost.Content = null;
        }

        _host.Content = card.Presenter;
        if (hadFocus)
        {
            // 处理完一个后焦点跟随到下一个请求的呈现根，而不是掉到被遮住的输入框。
            DispatcherQueue?.TryEnqueue(() => card.FocusPresenter());
        }
    }
}
