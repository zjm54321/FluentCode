using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using FluentCode.Services.Billion;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// 会话概况的主题样式工厂：用 XamlReader 构造只含 {ThemeResource …} Setter 的 Style（内置画刷键，不定义颜色），
/// 因此浅色 / 深色 / 高对比度切换时由框架自动重取，不是一次性取值的画刷。按键缓存；不可用时返回 null（保持默认外观）。
/// </summary>
internal static class OpenCodeInsightsTheme
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly Dictionary<string, Style?> Cache = new(StringComparer.Ordinal);

    internal const string Accent = "AccentFillColorDefaultBrush";
    internal const string Caution = "SystemFillColorCautionBrush";
    internal const string Critical = "SystemFillColorCriticalBrush";
    internal const string Success = "SystemFillColorSuccessBrush";
    internal const string Neutral = "TextFillColorTertiaryBrush";
    internal const string RingTrack = "ControlStrokeColorDefaultBrush";
    internal const string Divider = "DividerStrokeColorDefaultBrush";

    /// <summary>确定进度环：前景（进度弧）+ 背景（轨道）。模板仍是控件默认样式。</summary>
    internal static Style? Ring(string foregroundKey)
        => Get("ProgressRing", ("Foreground", foregroundKey), ("Background", RingTrack));

    /// <summary>8×8 状态圆点（Ellipse.Fill）。</summary>
    internal static Style? Dot(string fillKey)
        => Get("Ellipse", ("Width", null), ("Height", null), ("Fill", fillKey));

    /// <summary>1px 分隔线（Border.Background）。</summary>
    internal static Style? DividerLine() => Get("Border", ("Background", Divider));

    internal static Style? TodoSecondaryIcon() => Get("FontIcon", ("Foreground", "TextFillColorSecondaryBrush"));

    /// <summary>四张停靠卡片共用的实色主题样式；ClearValue 后仍保留样式提供的背景。</summary>
    internal static Style? DockCard()
        => OpenCodeInteractionUi.TryStyle("DockCard");

    /// <summary>卡片内只读信息框（目录行）：次级控件填充 + 卡片描边。</summary>
    internal static Style? InsetBox()
        => Get("Border", ("Background", "ControlFillColorSecondaryBrush"), ("BorderBrush", "CardStrokeColorDefaultBrush"));

    /// <summary>空闲（未运行过）的已配置智能体：三级文字色；运行中 / 已结束为主文字色。</summary>
    internal static Style? TreeLabel(bool dimmed)
        => Get("TextBlock", ("Foreground", dimmed ? "TextFillColorTertiaryBrush" : "TextFillColorPrimaryBrush"));

    private static Style? Get(string targetType, params (string Property, string? Key)[] setters)
    {
        var cacheKey = targetType + "|" + string.Join("|", setters.Select(s => s.Property + "=" + s.Key));
        if (Cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        Style? style = null;
        try
        {
            var xaml = new StringBuilder();
            xaml.Append("<Style xmlns=\"").Append(XamlNamespace).Append("\" TargetType=\"").Append(targetType).Append("\">");
            foreach (var (property, key) in setters)
            {
                // Key 为 null：固定尺寸（圆点 8px），不是颜色。
                xaml.Append("<Setter Property=\"").Append(property).Append("\" Value=\"")
                    .Append(key is null ? "8" : "{ThemeResource " + key + "}").Append("\"/>");
            }

            xaml.Append("</Style>");
            style = XamlReader.Load(xaml.ToString()) as Style;
        }
        catch
        {
            style = null;
        }

        Cache[cacheKey] = style;
        return style;
    }
}

/// <summary>
/// 窄屏标签栏右侧的原生上下文 ProgressRing + 百分比 Button；宽屏由右侧文字上下文区替代。
/// 点击打开原生 Flyout，显示与宽屏同一上下文网格，不再提供价格目录或用量明细。
/// 纯渲染：不发请求（打开 Flyout 时经 <see cref="DetailsOpening"/> 交给页面决定是否读取）；实例常驻（视图持有），
/// 每次渲染只改属性与 Flyout 内容。
/// </summary>
/// <remarks>
/// 颜色：常规 = 强调色；≥80% = 注意（Caution）；≥95% = 严重（Critical）；全部为 ThemeResource 样式。
/// 百分比只是“最近一次 assistant 请求用量 / 模型上下文上限”的参考值。
/// Billion 压缩块和节省量合入该网格；地址关联已确认且上下文可见时由页面自动读取。
/// 地址设置仍由原生上下文菜单进入，不推断端口或关联。
/// </remarks>
internal sealed class OpenCodeContextRing
{
    internal const double CautionPercent = 80;
    internal const double CriticalPercent = 95;
    internal const double ButtonSize = 32;

    private readonly Grid _root;
    private readonly Button _button;
    private readonly ProgressRing _ring;
    private readonly Flyout _flyout;
    private TextBlock? _percent;
    private StackPanel? _labelled;
    private bool _showPercent;
    private string _percentText = "—";

    internal OpenCodeContextRing()
    {
        _ring = new ProgressRing
        {
            IsActive = true,
            IsIndeterminate = false,
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Width = 20,
            Height = 20,
            IsHitTestVisible = false,
        };
        AutomationProperties.SetAutomationId(_ring, "OpenCodeInsightsContextRing");

        _flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            FlyoutPresenterStyle = OpenCodeInteractionUi.TryStyle("DockCardFlyoutPresenterStyle"),
        };
        _flyout.Opening += (_, _) => DetailsOpening?.Invoke(this, EventArgs.Empty);
        _button = new Button
        {
            Content = _ring,
            Flyout = _flyout,
            Width = ButtonSize,
            Height = ButtonSize,
            MinWidth = 0,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        OpenCodeToolStyle(_button);
        _button.ContextFlyout = OpenCodeContextMenu.Create(new OpenCodeContextCommand?[]
            { new("Billion 设置", "\uE713", () => BillionSetupRequested?.Invoke(this, EventArgs.Empty)) });
        AutomationProperties.SetAutomationId(_button, "OpenCodeInsightsContextRingButton");

        _root = new Grid { VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(_root, "OpenCodeInsightsContext");
        _root.Children.Add(_button);
        Render(OpenCodeSessionInsightsSnapshot.Empty(null, null));
    }

    /// <summary>常驻根元素（ID <c>OpenCodeInsightsContext</c>）；页面只挂接一次到页头宿主。</summary>
    internal Grid Root => _root;

    internal Button Button => _button;

    internal ProgressRing Ring => _ring;

    internal Flyout Flyout => _flyout;

    /// <summary>
    /// 布局 D 窄模式页头：圆环右侧附带百分比文字（“43%”，未知为“—”）。宽模式只保留 32×32 圆环（右侧卡片已有完整用量行）。
    /// 文字只在首次需要时创建；圆环实例与 Flyout 不变。
    /// </summary>
    internal bool ShowPercent
    {
        get => _showPercent;
        set
        {
            if (_showPercent == value) return;
            _showPercent = value;
            if (value)
            {
                _percent ??= new TextBlock { VerticalAlignment = VerticalAlignment.Center };
                if (_labelled is null)
                {
                    _labelled = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                }
                _percent.Text = _percentText;
                _button.Content = null;
                _labelled.Children.Clear();
                _labelled.Children.Add(_ring);
                _labelled.Children.Add(_percent);
                _button.Content = _labelled;
                _button.Width = double.NaN;
                _button.Padding = new Thickness(6, 0, 10, 0);
            }
            else
            {
                _button.Content = null;
                _labelled?.Children.Clear();
                _button.Content = _ring;
                _button.Width = ButtonSize;
                _button.Padding = new Thickness(0);
            }
        }
    }

    /// <summary>当前圆环对应的颜色档（测试与无障碍说明共用）。</summary>
    internal string ToneKey { get; private set; } = OpenCodeInsightsTheme.Accent;

    /// <summary>最近一次的简短说明（Tooltip / 无障碍帮助文本）。</summary>
    internal string Summary { get; private set; } = string.Empty;

    /// <summary>Flyout 正在打开：页面据此在当前会话尚无结果时显式读取一次（与打开概况面板相同的读取时机）。</summary>
    internal event EventHandler? DetailsOpening;

    /// <summary>ACP·Billion 组内“读取”按钮（仅当地址已确认）：由视图按界面显示的上下文转发。</summary>

    /// <summary>ACP·Billion 组内“去设置”按钮：打开概况面板的 Billion 分区（地址与关联确认只在那里进行）。</summary>
    internal event EventHandler? BillionSetupRequested;

    internal void Render(OpenCodeSessionInsightsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var (ratio, tokens, limit) = Measure(snapshot);
        double percent = ratio is { } r ? r * 100 : 0;
        _ring.Value = percent;
        ToneKey = ToneFor(ratio);
        if (OpenCodeInsightsTheme.Ring(ToneKey) is { } style)
        {
            _ring.Style = style;
        }

        var state = snapshot.Context.State;
        var usage = tokens is { } used
            ? OpenCodeSessionInsightsView.Compact(used) + (limit is { } max ? " / " + OpenCodeSessionInsightsView.Compact(max) : "") + " tokens"
            : null;
        var qualifier = snapshot.UnavailableReason ?? state switch
        {
            OpenCodeInsightState.NotLoaded => "点按读取",
            OpenCodeInsightState.Loading => "读取中…",
            OpenCodeInsightState.Failed => "无法确认",
            OpenCodeInsightState.Partial => "参考值，可能已过时",
            _ => tokens is null ? "暂无上下文参考" : limit is null ? "模型上下文上限未知" : "参考值",
        };
        _percentText = ratio is { } ? FormatPercent(percent) : "—";
        if (_percent is not null) _percent.Text = _percentText;
        Summary = "上下文 " + (ratio is { } ? FormatPercent(percent) : "—")
            + (usage is not null ? " · " + usage : "") + " · " + qualifier;
        ToolTipService.SetToolTip(_button, Summary);
        AutomationProperties.SetName(_button, "上下文参考 " + (ratio is { } ? "约 " + FormatPercent(percent) : "未知"));
        AutomationProperties.SetHelpText(_button, Summary);
        AutomationProperties.SetName(_ring, "上下文参考占比 " + (ratio is { } ? FormatPercent(percent) : "未知"));
        AutomationProperties.SetName(_root, "上下文参考：" + OpenCodeSessionInsightsView.DescribeState(state));
        _flyout.Content = BuildFlyout(snapshot, this);
    }

    /// <summary>Billion 原地更新时只重建 Flyout 内容（不触碰常驻 Billion 文本框）。</summary>
    internal void RenderFlyoutOnly(OpenCodeSessionInsightsSnapshot snapshot) => _flyout.Content = BuildFlyout(snapshot, this);

    internal static string ToneFor(double? ratio)
        => ratio is not { } r ? OpenCodeInsightsTheme.Accent
            : r * 100 >= CriticalPercent ? OpenCodeInsightsTheme.Critical
            : r * 100 >= CautionPercent ? OpenCodeInsightsTheme.Caution
            : OpenCodeInsightsTheme.Accent;

    internal static (double? Ratio, double? Tokens, double? Limit) Measure(OpenCodeSessionInsightsSnapshot snapshot)
    {
        if (snapshot.UnavailableReason is not null
            || snapshot.Context.Value is not { ReferenceTokens: { } tokens } estimate || !double.IsFinite(tokens)
            || estimate.SessionId != snapshot.SessionId
            || estimate.StaleReasons.Any(reason => reason is not FluentCode.Services.OpenCode.OpenCodeContextStaleReason.HistoryIncomplete
                and not FluentCode.Services.OpenCode.OpenCodeContextStaleReason.ModelLimitUnavailable))
        {
            return (null, null, null);
        }

        if (estimate.ContextLimit is { } limit && limit > 0)
        {
            return (Math.Clamp(tokens / limit, 0, 1), tokens, limit);
        }

        return (null, tokens, null);
    }

    private static string FormatPercent(double percent) => percent.ToString("0", CultureInfo.InvariantCulture) + "%";

    private static void OpenCodeToolStyle(Button button)
    {
        // 透明底的原生按钮（App.xaml 中 BasedOn DefaultButtonStyle，背景为 ThemeResource）。
        if (OpenCodeInteractionUi.TryStyle("OpenCodeTransparentToolButtonStyle") is { } style)
        {
            button.Style = style;
        }
    }

    // ------------------------------------------------------------------
    // Flyout：三组
    // ------------------------------------------------------------------

    internal static FrameworkElement BuildFlyout(OpenCodeSessionInsightsSnapshot snapshot, OpenCodeContextRing? owner = null)
    {
        var groups = new StackPanel { Spacing = 16, Width = 286 };
        AutomationProperties.SetAutomationId(groups, "OpenCodeInsightsContextFlyout");
        AutomationProperties.SetName(groups, "上下文");
        groups.Children.Add(OpenCodeSessionInsightsView.ContextUsageSection(snapshot));
        return Scroll(groups);
    }

    private static FrameworkElement Scroll(UIElement content) => new ScrollViewer
    {
        Content = content,
        MaxHeight = 560,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    internal static UIElement Divider()
    {
        var line = new Border { Height = 1 };
        if (OpenCodeInsightsTheme.DividerLine() is { } style)
        {
            line.Style = style;
        }

        return line;
    }

    internal static string DescribePhase(OpenCodeBillionPhase phase) => phase switch
    {
        OpenCodeBillionPhase.Loaded => "已读取本会话记录",
        OpenCodeBillionPhase.Loading => "读取中",
        OpenCodeBillionPhase.NoRecord => "可达，但没有本会话记录",
        OpenCodeBillionPhase.Failed => "不可达或无法确认",
        OpenCodeBillionPhase.NeedsConfirmation => "等待确认地址关联",
        OpenCodeBillionPhase.MissingOrigin => "未取得地址",
        OpenCodeBillionPhase.InvalidOrigin => "地址无效",
        _ => "未读取",
    };
}
