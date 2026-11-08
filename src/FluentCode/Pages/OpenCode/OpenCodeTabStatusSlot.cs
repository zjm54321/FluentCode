using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace FluentCode.Pages.OpenCode;

internal enum OpenCodeTabStatusGlyph { None, Working, Background, Retrying, NeedsYou, Failed, Interrupted, Unread }

/// <summary>会话标签标题前的 16×16 状态位；空闲时整格收起。状态文字由标签的 ItemStatus 承担，本格对 UIA 不可见。</summary>
internal sealed class OpenCodeTabStatusSlot : Grid
{
    private const string AccentKey = "AccentFillColorDefaultBrush";
    private const string NeutralKey = "TextFillColorSecondaryBrush";
    private const string CautionKey = "SystemFillColorCautionBrush";
    private const string CriticalKey = "SystemFillColorCriticalBrush";

    private ProgressRing? _ring;
    private Ellipse? _ringDot, _dot;
    private FontIcon? _retry;
    private InfoBadge? _badge;
    private Grid? _failed, _interrupted;
    private OpenCodeTabStatusGlyph _glyph;
    private bool _overlay;

    internal OpenCodeTabStatusSlot()
    {
        Width = Height = OpenCodeWorkingMotion.StatusCellSize;
        Margin = new Thickness(0, 0, 8, 0);
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
    }

    internal void Clear() => Show(OpenCodeTabStatusGlyph.None);

    internal void Show(OpenCodeTabStatusGlyph glyph, bool unreadOverlay = false)
    {
        unreadOverlay &= glyph == OpenCodeTabStatusGlyph.Background;
        if (glyph == _glyph && unreadOverlay == _overlay) return;
        _glyph = glyph;
        _overlay = unreadOverlay;
        if (_ring is not null) _ring.IsActive = false;
        Children.Clear();
        switch (glyph)
        {
            case OpenCodeTabStatusGlyph.Working or OpenCodeTabStatusGlyph.Background:
                _ring ??= Raw(OpenCodeProgressRingSync.Create(
                    OpenCodeWorkingMotion.RingSize, OpenCodeWorkingMotion.RingAccessibleName));
                OpenCodeToolTheme.Apply(_ring, OpenCodeToolTheme.Ring(
                    glyph == OpenCodeTabStatusGlyph.Working ? AccentKey : NeutralKey));
                _ring.IsActive = true;
                Children.Add(_ring);
                if (unreadOverlay) Children.Add(_ringDot ??= Dot(6));
                break;
            case OpenCodeTabStatusGlyph.Retrying:
                Children.Add(_retry ??= Icon("\uE72C", CautionKey)); // Refresh：重试等待期间不旋转。
                break;
            case OpenCodeTabStatusGlyph.NeedsYou:
                Children.Add(_badge ??= Raw(new InfoBadge
                {
                    Style = OpenCodeInteractionUi.TryStyle("CautionIconInfoBadgeStyle"),
                    Width = OpenCodeWorkingMotion.StatusCellSize,
                    Height = OpenCodeWorkingMotion.StatusCellSize,
                }));
                break;
            case OpenCodeTabStatusGlyph.Failed:
                Children.Add(_failed ??= Outcome("\uF13D", CriticalKey)); // 环 + 叉：执行失败。
                break;
            case OpenCodeTabStatusGlyph.Interrupted:
                Children.Add(_interrupted ??= Outcome("\uE738", NeutralKey)); // 环 + Remove 横线：用户中断，不是错误。
                break;
            case OpenCodeTabStatusGlyph.Unread:
                Children.Add(_dot ??= Dot(8));
                break;
        }
        Visibility = glyph == OpenCodeTabStatusGlyph.None ? Visibility.Collapsed : Visibility.Visible;
    }

    private static Grid Outcome(string mark, string brushKey) => Raw(new Grid
    {
        Width = OpenCodeWorkingMotion.RingSize,
        Height = OpenCodeWorkingMotion.RingSize,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Children = { Icon("\uF138", brushKey), Icon(mark, brushKey) },
    });

    private static FontIcon Icon(string glyph, string brushKey)
    {
        var icon = Raw(new FontIcon
        {
            Glyph = glyph,
            FontSize = OpenCodeWorkingMotion.RingSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        OpenCodeToolTheme.Apply(icon, OpenCodeToolTheme.Icon(brushKey));
        return icon;
    }

    private static Ellipse Dot(double size)
    {
        var dot = Raw(new Ellipse { Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.AttentionDotStyleKey) });
        dot.Width = dot.Height = size;
        dot.HorizontalAlignment = HorizontalAlignment.Center;
        dot.VerticalAlignment = VerticalAlignment.Center;
        return dot;
    }

    private static T Raw<T>(T element) where T : FrameworkElement
    {
        AutomationProperties.SetAccessibilityView(element, AccessibilityView.Raw);
        return element;
    }
}
