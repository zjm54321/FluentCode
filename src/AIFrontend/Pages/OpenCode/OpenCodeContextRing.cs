using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AIFrontend.Services.Billion;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;

namespace AIFrontend.Pages.OpenCode;

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

    /// <summary>布局 D 浮动卡片（Border）：应用内亚克力 + 卡片描边（ThemeResource，随浅色 / 深色 / 高对比度切换）。</summary>
    internal static Style? DockCard()
        => Get("Border", ("Background", "AcrylicInAppFillColorDefaultBrush"), ("BorderBrush", "CardStrokeColorDefaultBrush"));

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
/// 页头的“上下文参考”小圆环（wave5 G5：从概况面板移到标签栏右侧，紧挨在“概况”按钮左边，二者同为 32×32 的原生按钮）：
/// 原生 <see cref="ProgressRing"/>（IsIndeterminate=False，Value=占比）放在透明原生 Button 内；百分比与 “41.2K / 200K”
/// 在 Tooltip 与无障碍名称中。点击打开原生 Flyout，固定三组：当前上下文 / 用量计费 / ACP·Billion 状态。
/// 纯渲染：不发请求（打开 Flyout 时经 <see cref="DetailsOpening"/> 交给页面决定是否读取）；实例常驻（视图持有），
/// 每次渲染只改属性与 Flyout 内容。
/// </summary>
/// <remarks>
/// 颜色：常规 = 强调色；≥80% = 注意（Caution）；≥95% = 严重（Critical）；全部为 ThemeResource 样式。
/// 百分比只是“最近一次 assistant 请求用量 / 模型上下文上限”的参考值。
/// ACP·Billion 组（wave5 H11）与 OpenCode TUI 中 billion-context 插件的 <c>/acp</c> 面板同源（Billion 会话记录：上下文、
/// 压缩块、节省量），压缩块按服务端顺序列成与 to do 列表相同的“图标 + 文本”行；读取仍须先在 Billion 分区确认地址关联，
/// 组内只提供“读取”/“去设置”两个显式动作，从不自动读取。
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

        _flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
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
    internal event EventHandler? BillionReadRequested;

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
        if (snapshot.Context.Value is not { ReferenceTokens: { } tokens } estimate || !double.IsFinite(tokens))
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
        var groups = new StackPanel { Spacing = 16, Width = 340 };
        AutomationProperties.SetAutomationId(groups, "OpenCodeInsightsContextFlyout");
        AutomationProperties.SetName(groups, "上下文与用量详情");
        if (snapshot.UnavailableReason is { } reason)
        {
            groups.Children.Add(OpenCodeSessionInsightsView.Caption(reason, 0.85));
            return Scroll(groups);
        }

        groups.Children.Add(ContextGroup(snapshot));
        groups.Children.Add(Divider());
        groups.Children.Add(BillingGroup(snapshot));
        groups.Children.Add(Divider());
        groups.Children.Add(AcpGroup(snapshot, owner));
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

    /// <summary>组一：当前上下文（ID OpenCodeInsightsContextDetail）。</summary>
    private static UIElement ContextGroup(OpenCodeSessionInsightsSnapshot snapshot)
    {
        var section = snapshot.Context;
        var (panel, content) = OpenCodeSessionInsightsView.Section("当前上下文", section.State, "OpenCodeInsightsContextDetail");
        if (section.Value is not { } estimate)
        {
            content.Children.Add(OpenCodeSessionInsightsView.Caption(section.Note));
            return panel;
        }

        var (ratio, tokens, limit) = Measure(snapshot);
        if (tokens is { } used)
        {
            if (limit is { } max && ratio is { } r)
            {
                var percent = FormatPercent(r * 100);
                content.Children.Add(OpenCodeSessionInsightsView.Figure(
                    OpenCodeSessionInsightsView.Compact(used) + " / " + OpenCodeSessionInsightsView.Compact(max),
                    "约占模型上下文上限的 " + percent));
            }
            else
            {
                content.Children.Add(OpenCodeSessionInsightsView.Figure(OpenCodeSessionInsightsView.Tokens(used) + " tokens", "模型上下文上限未知"));
            }
        }
        else
        {
            content.Children.Add(OpenCodeSessionInsightsView.Caption("暂无可参考的用量。"));
        }

        if (estimate.LatestAssistantUsage?.Tokens is { } breakdown)
        {
            content.Children.Add(OpenCodeSessionInsightsView.Table(new List<(string, string)>
            {
                ("输入（未缓存）", OpenCodeSessionInsightsView.Tokens(breakdown.Input)),
                ("输出", OpenCodeSessionInsightsView.Tokens(breakdown.Output)),
                ("推理", OpenCodeSessionInsightsView.Tokens(breakdown.Reasoning)),
                ("缓存读取", OpenCodeSessionInsightsView.Tokens(breakdown.CacheRead)),
                ("缓存写入", OpenCodeSessionInsightsView.Tokens(breakdown.CacheWrite)),
            }));
        }

        content.Children.Add(OpenCodeSessionInsightsView.Caption("最近一次 assistant 请求的五类用量合计，只是参考值，不是精确的当前上下文占用。", 0.55));
        if (estimate.Stale)
        {
            content.Children.Add(OpenCodeSessionInsightsView.Bullets("可能已过时：", estimate.StaleReasons.Select(OpenCodeSessionInsightsView.DescribeStale)));
        }

        if (!snapshot.HistoryComplete)
        {
            content.Children.Add(OpenCodeSessionInsightsView.Caption("提示：本页尚未加载到最早的消息。向上滚动加载完整历史后再刷新概况，可排除“历史未取全”。", 0.55));
        }

        return panel;
    }

    /// <summary>组二：用量计费（ID OpenCodeInsightsCost；价目子块 ID OpenCodeInsightsPricing）。</summary>
    private static UIElement BillingGroup(OpenCodeSessionInsightsSnapshot snapshot)
    {
        var section = snapshot.Cost;
        var (panel, content) = OpenCodeSessionInsightsView.Section("用量计费", section.State, "OpenCodeInsightsCost");
        if (section.Value is not { } view)
        {
            content.Children.Add(OpenCodeSessionInsightsView.Caption(section.Note));
        }
        else
        {
            FillUsage(content, view);
        }

        content.Children.Add(PricingBlock(snapshot));
        return panel;
    }

    private static void FillUsage(StackPanel content, OpenCodeCostView view)
    {
        var summary = view.Summary;
        var tokens = summary.Tokens;
        var prefix = summary.Incomplete ? "已知部分 " : "";
        string Token(double value) => summary.HasKnownTokens ? prefix + OpenCodeSessionInsightsView.Tokens(value) : "不可用";
        var figures = new Grid { ColumnSpacing = 16 };
        figures.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        figures.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var tokenFigure = OpenCodeSessionInsightsView.Figure(Token(tokens.Total),
            "已用 tokens");
        var costFigure = OpenCodeSessionInsightsView.Figure(summary.HasKnownCost ? prefix + OpenCodeSessionInsightsView.Money(summary.TotalCost) : "不可用", "费用（Core 累计估算）");
        Grid.SetColumn(costFigure, 1);
        figures.Children.Add(tokenFigure);
        figures.Children.Add(costFigure);
        content.Children.Add(figures);

        content.Children.Add(OpenCodeSessionInsightsView.Caption(view.Scope.SelectedIsRoot
            ? "主会话及全部后代，共 " + summary.SessionCount + " 个会话"
            : "当前是子会话：统计范围为已核实的主会话及全部后代，共 " + summary.SessionCount + " 个会话", 0.75));

        var rows = new List<(string, string)>
        {
            ("输入（未缓存）", Token(tokens.Input)),
            ("输出", Token(tokens.Output)),
            ("推理", Token(tokens.Reasoning)),
            ("缓存读取", Token(tokens.CacheRead)),
            ("缓存写入", Token(tokens.CacheWrite)),
            ("主会话自身费用", summary.RootCost is { } rootCost ? OpenCodeSessionInsightsView.Money(rootCost) : "无法确认"),
        };
        if (!view.Scope.SelectedIsRoot)
        {
            rows.Add(("当前子会话自身费用", view.SelectedOwn?.CoreCost is { } own ? OpenCodeSessionInsightsView.Money(own) : "不在已读取范围内"));
        }

        content.Children.Add(OpenCodeSessionInsightsView.Table(rows));
        if (summary.Incomplete)
        {
            content.Children.Add(OpenCodeSessionInsightsView.Bullets("部分数据：",
                summary.IncompleteReasons.Select(issue => OpenCodeSessionInsightsView.DescribeUsage(issue.Reason)).Distinct()));
        }

        content.Children.Add(OpenCodeSessionInsightsView.Caption("来自各会话 Session.Info 的累计值，每个会话只计一次。金额为 0 不代表免费；"
            + "不保证与外部账单一致，也不与 Billion 的数字相加。", 0.55));
    }

    private static UIElement PricingBlock(OpenCodeSessionInsightsSnapshot snapshot)
    {
        var section = snapshot.Pricing;
        var block = new StackPanel { Spacing = 8 };
        AutomationProperties.SetAutomationId(block, "OpenCodeInsightsPricing");
        AutomationProperties.SetName(block, "当前模型价目：" + OpenCodeSessionInsightsView.DescribeState(section.State));
        block.Children.Add(OpenCodeInteractionUi.StyledText("当前模型价目", OpenCodeInteractionUi.CaptionStyleKey));
        if (section.Value is not { } pricing)
        {
            block.Children.Add(OpenCodeSessionInsightsView.Caption(section.Note));
            return block;
        }

        block.Children.Add(OpenCodeSessionInsightsView.Body(pricing.ProviderId + " / " + pricing.ModelId
            + (pricing.Variant is { Length: > 0 } v ? "（" + v + "）" : "")));
        if (pricing.Tiers.IsEmpty)
        {
            block.Children.Add(OpenCodeSessionInsightsView.Caption(section.Note));
            return block;
        }

        foreach (var tier in pricing.Tiers)
        {
            var rows = new List<(string, string)>
            {
                ("输入", OpenCodeSessionInsightsView.Price(tier.Input)),
                ("输出", OpenCodeSessionInsightsView.Price(tier.Output)),
                ("缓存读取", OpenCodeSessionInsightsView.Price(tier.CacheRead)),
                ("缓存写入", OpenCodeSessionInsightsView.Price(tier.CacheWrite)),
            };
            if (tier.Multipliers is { } multipliers)
            {
                rows.Add(("缓存读取 / 输入", OpenCodeSessionInsightsView.Ratio(multipliers.Read)));
                rows.Add(("缓存写入 / 输入", OpenCodeSessionInsightsView.Ratio(multipliers.Write)));
            }

            block.Children.Add(OpenCodeSessionInsightsView.Caption(OpenCodeSessionInsightsView.DescribeTier(tier.Tier), 0.85));
            block.Children.Add(OpenCodeSessionInsightsView.Table(rows));
        }

        block.Children.Add(OpenCodeSessionInsightsView.Caption("单价为 USD / 百万 tokens，倍率只在同一档位内换算。这是模型目录的当前价，"
            + "历史费用不会按它重新计算，也不会自动判断适用哪一档。", 0.55));
        return block;
    }

    /// <summary>
    /// 组三：ACP / Billion 状态（ID OpenCodeInsightsAcpStatus；wave5 H11）。内容与 billion-context 的 <c>/acp</c> 面板同源：
    /// 上下文占比、压缩块（活跃 / 总数）、节省量与缓存命中；压缩块按服务端顺序列成“图标 + 文本”的行（ID OpenCodeInsightsAcpBlocks）。
    /// 只显示最近一次显式读取的结果；地址已确认时组内可“读取”，否则“去设置”跳到 Billion 分区，从不自动读取。
    /// </summary>
    private static UIElement AcpGroup(OpenCodeSessionInsightsSnapshot snapshot, OpenCodeContextRing? owner)
    {
        var billion = snapshot.Billion;
        var state = OpenCodeSessionInsightsView.BillionState(billion);
        var (panel, content) = OpenCodeSessionInsightsView.Section("ACP / Billion 状态", state, "OpenCodeInsightsAcpStatus");
        if (billion.Phase == OpenCodeBillionPhase.Loaded && billion.Stats is { } stats)
        {
            var rows = new List<(string, string)>();
            if (stats.ContextTokens is { } ctx)
            {
                var window = stats.ContextWindow is { } w && w > 0 ? w : (long?)null;
                rows.Add(("上下文", (window is { } total ? FormatPercent(Math.Clamp(ctx * 100.0 / total, 0, 100)) + " · " : "")
                    + OpenCodeSessionInsightsView.Count(ctx) + (window is { } limit ? " / " + OpenCodeSessionInsightsView.Count(limit) : "")));
            }
            else
            {
                rows.Add(("上下文", "未知"));
            }

            var active = stats.BlockDetails.Count(block => block.Active == true);
            rows.Add(("压缩块", stats.BlockDetails.IsEmpty
                ? OpenCodeSessionInsightsView.Count(stats.Blocks)
                : active + " 活跃 / " + OpenCodeSessionInsightsView.Count(stats.Blocks ?? stats.BlockDetails.Length)));
            rows.Add(("节省 tokens", OpenCodeSessionInsightsView.Count(stats.TokensSaved)));
            rows.Add(("缓存命中率", stats.CacheHitPct is { } pct && double.IsFinite(pct)
                ? pct.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "未知"));
            rows.Add(("请求数", OpenCodeSessionInsightsView.Count(stats.Requests)));
            rows.Add(("Billion 版本", billion.Health?.Version ?? stats.BiliVersion ?? "未知"));
            rows.Add(("读取时间", OpenCodeSessionInsightsView.Time(stats.FetchedAt)));
            content.Children.Add(OpenCodeSessionInsightsView.Table(rows));
            content.Children.Add(BlockList(stats));
        }
        else
        {
            var origin = snapshot.BillionOriginState switch
            {
                OpenCodeBillionOriginState.Confirmed => "已确认",
                OpenCodeBillionOriginState.Unconfirmed => "待确认",
                OpenCodeBillionOriginState.Invalid => "地址无效",
                _ => snapshot.BillionDiscoveryLoading ? "正在发现…" : "未取得",
            };
            content.Children.Add(OpenCodeSessionInsightsView.Table(new List<(string, string)>
            {
                ("本机 Billion", DescribePhase(billion.Phase)),
                ("地址", origin),
            }));
            if (billion.Phase is OpenCodeBillionPhase.Failed or OpenCodeBillionPhase.NoRecord)
            {
                content.Children.Add(OpenCodeSessionInsightsView.Caption(billion.Note, 0.75));
            }
        }

        if (owner is not null)
        {
            content.Children.Add(AcpAction(snapshot, owner));
        }

        return panel;
    }

    /// <summary>与 /acp 面板 “Blocks” 段同源的压缩块行：活跃 = 勾选框字形，已失效 = 空框 + 次要文字；按服务端顺序。</summary>
    private static UIElement BlockList(BillionSessionStats stats)
    {
        var list = new StackPanel { Spacing = 8 };
        AutomationProperties.SetAutomationId(list, "OpenCodeInsightsAcpBlocks");
        AutomationProperties.SetName(list, "压缩块列表");
        if (stats.BlockDetails.IsEmpty)
        {
            list.Children.Add(OpenCodeSessionInsightsView.Caption(stats.Blocks is > 0 ? "未提供压缩块明细。" : "还没有压缩块。", 0.75));
            return list;
        }

        for (var index = 0; index < stats.BlockDetails.Length; index++)
        {
            var block = stats.BlockDetails[index];
            var isActive = block.Active == true;
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new FontIcon { Glyph = isActive ? "\uE73A" : "\uE739", FontSize = 16, VerticalAlignment = VerticalAlignment.Top });
            var name = (block.Id is { Length: > 0 } id ? id : "块 " + (index + 1))
                + (block.Tier is { } tier ? " · T" + tier.ToString(CultureInfo.InvariantCulture) : "");
            var label = OpenCodeInteractionUi.StyledText(name,
                isActive ? OpenCodeInteractionUi.BodyStyleKey : OpenCodeInteractionUi.BodySubtleStyleKey, selectable: true);
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            var size = OpenCodeInteractionUi.StyledText(block.CompressedTokens is { } compressed ? OpenCodeSessionInsightsView.Count(compressed) : "—",
                OpenCodeInteractionUi.CaptionSubtleStyleKey);
            size.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(size, 2);
            row.Children.Add(size);
            AutomationProperties.SetAutomationId(row, "OpenCodeInsightsAcpBlock." + index);
            AutomationProperties.SetName(row, name + "，" + (isActive ? "活跃" : block.Active == false ? "已失效" : "状态未知")
                + "，压缩 " + (block.CompressedTokens is { } t ? OpenCodeSessionInsightsView.Count(t) : "未知") + " tokens");
            list.Children.Add(row);
        }

        if (stats.BlockDetailsTruncated)
        {
            list.Children.Add(OpenCodeSessionInsightsView.Caption("仅列出前 " + BillionSessionStats.MaximumBlockDetails + " 个压缩块。", 0.55));
        }

        return list;
    }

    private static UIElement AcpAction(OpenCodeSessionInsightsSnapshot snapshot, OpenCodeContextRing owner)
    {
        var canRead = snapshot.BillionContext is not null && snapshot.BillionConfirmed;
        var button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = canRead ? "\uE72C" : "\uE713", FontSize = 12 },
                    OpenCodeInteractionUi.StyledText(canRead
                        ? (snapshot.Billion.Phase == OpenCodeBillionPhase.Loaded ? "重新读取" : "读取")
                        : "设置 Billion 地址", OpenCodeInteractionUi.BodyStyleKey),
                },
            },
            IsEnabled = snapshot.Billion.Phase != OpenCodeBillionPhase.Loading && snapshot.UnavailableReason is null,
        };
        if (canRead)
        {
            AutomationProperties.SetAutomationId(button, "OpenCodeInsightsAcpRead");
            AutomationProperties.SetName(button, "读取本机 Billion 记录");
            button.Click += (_, _) => owner.BillionReadRequested?.Invoke(owner, EventArgs.Empty);
        }
        else
        {
            AutomationProperties.SetAutomationId(button, "OpenCodeInsightsAcpSetup");
            AutomationProperties.SetName(button, "打开 Billion 分区设置地址");
            ToolTipService.SetToolTip(button, "读取前需要在概况面板的 Billion 分区确认地址关联");
            button.Click += (_, _) => owner.BillionSetupRequested?.Invoke(owner, EventArgs.Empty);
        }

        return button;
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
