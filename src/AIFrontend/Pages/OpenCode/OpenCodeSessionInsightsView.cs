using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIFrontend.Services.Billion;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// 会话概况的原生详情面板（放在页面右侧概况面板 <c>InsightsPaneHost</c> 内，重设计 v3 §10）。纯渲染：不发请求，
/// 刷新 / Billion 确认 / Billion 读取 / 打开子会话经事件交回页面，由页面捕获绑定后调用控制器。
/// </summary>
/// <remarks>
/// 顶部是原生 <see cref="SelectorBar"/>（<c>OpenCodeInsightsSections</c>）两个分区（F10c）：
/// 概况（上下文参考小圆环 <see cref="OpenCodeContextRing"/>，点击打开“当前上下文 / 用量计费 / ACP·Billion 状态”三组 Flyout；
/// 其下依次是 to do 与原 Agents 分区的主 agent 与子会话，见 <see cref="OpenCodeInsightsAgentsSection"/>）、
/// Billion（地址填写、关联确认与显式读取；常驻文本框/复选框需要固定宿主，所以保留为单独分区）。
/// MCP / 插件 / LSP 服务状态改由页头连接状态按钮的 Flyout 显示（<see cref="OpenCodeConnectionFlyout"/>）。
/// 两个分区页始终都在可视树内，切换分区只改 Visibility，不重建、不重新挂接任何控件（Billion 文本框焦点约束不受影响）。
/// 只显示白名单字段：数字、固定状态与原因文案；不显示原始错误体、metadata JSON，
/// 也不显示 Billion 的 handoff / 摘要 / 标题（连接器本就不解析这些字段）。
/// “读取中 / 部分 / 无法确认 / 不可用 / 尚未读取”各自有不同的徽标文案，绝不把失败画成空列表。
/// <para>
/// Billion 地址：用户在常驻文本框中输入根地址，每次真实编辑立即经 <see cref="BillionOriginEdited"/>
/// 携带“最近一次已确认的渲染上下文”交给页面（页面再交给控制器，使旧确认/在途读取立刻失效）。
/// TextChanging 回调内绝不改动可视树：快照只记录状态，界面更新排入 DispatcherQueue 合并执行；
/// 只有 Billion 部分变化时原地更新，不重新挂接文本框，保留焦点与光标。未生效的原始输入只留在
/// 本控件（按来源引用 + Revision 归属），不写入快照、自动化名称或日志；换源时丢弃。
/// 程序写入文本框/复选框不会产生“用户编辑/确认”事件。
/// </para>
/// </remarks>
internal sealed class OpenCodeSessionInsightsView : UserControl
{
    private readonly TextBlock _scopeText;
    private readonly Border _usageHost = Card();
    private readonly Button _refreshButton;
    private readonly ProgressRing _refreshRing;
    private readonly CheckBox _billionConfirm;
    private readonly Button _billionButton;
    private readonly StackPanel _billionOriginPanel;
    private readonly TextBox _billionOriginBox;
    private readonly TextBlock _billionOriginStatus;

    // 分区：SelectorBar + 四个常驻分区页（切换只改 Visibility）。
    private readonly SelectorBar _sections;
    private readonly StackPanel _overviewPage;
    private readonly StackPanel _billionPage;
    private readonly StackPanel[] _pages;
    private readonly OpenCodeInsightsAgentsSection _agents = new();
    // wave5 G5：上下文圆环移到页头（“概况”按钮左侧）；实例仍由本视图持有并随快照渲染，页面把 ContextRing.Root 挂进页头宿主。
    private readonly OpenCodeContextRing _contextRing = new();

    // 上次 BillionSection 把常驻控件放进的内容面板。面板尚未进入可视树时 Parent 不一定可靠，
    // 所以按记录的宿主显式移除，而不是只靠 Parent 推断。
    private Panel? _billionHost;
    private bool _rendering;

    // 原地更新 Billion 分区所需的本次全量渲染构件（全量渲染开始时清空，成功后才记录 _visual）。
    private StackPanel? _billionSection;
    private Grid? _billionHeading;
    private Border? _billionBadge;
    private StackPanel? _billionResult;

    // _latest：最近一次交给 Render 的快照（状态，立即更新）；_visual：当前界面实际显示的快照。
    private OpenCodeSessionInsightsSnapshot? _latest;
    private OpenCodeSessionInsightsSnapshot? _visual;
    private bool _visualPending;
    private bool _visualQueued;
    private long _visualTicket;
    private int _inputDepth;

    // 未生效的原始地址草稿归属（来源引用 + Revision）；不同来源绝不沿用。
    private bool _draftActive;
    private object? _draftSource;
    private long _draftRevision;
    private bool _editRejected;

    internal OpenCodeSessionInsightsView()
    {
        AutomationProperties.SetAutomationId(this, "OpenCodeSessionInsightsPanel");
        AutomationProperties.SetName(this, "会话概况");

        _refreshRing = new ProgressRing { Width = 16, Height = 16, IsActive = false, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(_refreshRing, "概况读取中");
        _refreshButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE72C", FontSize = 12 },
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAutomationId(_refreshButton, "OpenCodeInsightsRefreshButton");
        AutomationProperties.SetName(_refreshButton, "重新读取会话概况");
        ToolTipService.SetToolTip(_refreshButton, "重新读取用量、费用、上下文、to do、Agents 与服务状态（不含 Billion）");
        _refreshButton.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);

        // 工具行：采样说明（左）+ 读取中 / 刷新（右）。面板标题与关闭按钮由页面面板头提供。
        _scopeText = Caption(string.Empty);
        _scopeText.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(_scopeText, "OpenCodeInsightsScope");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Top };
        actions.Children.Add(_refreshRing);
        actions.Children.Add(_refreshButton);
        var toolbar = new Grid { ColumnSpacing = 12 };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Children.Add(_scopeText);
        Grid.SetColumn(actions, 1);
        toolbar.Children.Add(actions);

        _billionOriginBox = new TextBox
        {
            PlaceholderText = "http://127.0.0.1:端口",
            MinWidth = 0,
        };
        AutomationProperties.SetAutomationId(_billionOriginBox, "OpenCodeInsightsBillionOrigin");
        AutomationProperties.SetName(_billionOriginBox, "Billion 根地址");
        AutomationProperties.SetHelpText(_billionOriginBox, "只填协议、本机主机和端口，不含路径");
        _billionOriginBox.TextChanging += (_, args) => OnOriginTextChanging(args.IsContentChanging);
        _billionOriginBox.LostFocus += (_, _) => FlushPendingVisual();
        _billionOriginStatus = Caption(string.Empty, 0.85);
        AutomationProperties.SetAutomationId(_billionOriginStatus, "OpenCodeInsightsBillionOriginStatus");
        _billionOriginPanel = new StackPanel { Spacing = 4 };
        _billionOriginPanel.Children.Add(OpenCodeInteractionUi.StyledText("Billion 根地址", OpenCodeInteractionUi.BodyStrongStyleKey));
        _billionOriginPanel.Children.Add(_billionOriginBox);
        _billionOriginPanel.Children.Add(Caption(BillionOriginHint, 0.6));
        _billionOriginPanel.Children.Add(_billionOriginStatus);

        _billionConfirm = new CheckBox
        {
            Content = OpenCodeInteractionUi.StyledText(
                "我确认：此地址对应当前 OpenCode 使用的 Billion 实例（仅当前连接有效）", OpenCodeInteractionUi.CaptionStyleKey),
            MinWidth = 0,
        };
        AutomationProperties.SetAutomationId(_billionConfirm, "OpenCodeInsightsBillionConfirm");
        AutomationProperties.SetName(_billionConfirm, "确认此地址对应当前 OpenCode 的 Billion 实例");
        _billionConfirm.Checked += (_, _) => RaiseConfirm(true);
        _billionConfirm.Unchecked += (_, _) => RaiseConfirm(false);

        _billionButton = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "\uE8B7", FontSize = 14 },
                    new TextBlock { Text = "读取本机记录" },
                },
            },
        };
        AutomationProperties.SetAutomationId(_billionButton, "OpenCodeInsightsBillionReadButton");
        AutomationProperties.SetName(_billionButton, "读取本机 Billion 记录");
        ToolTipService.SetToolTip(_billionButton, "向所填地址读取一次 Billion 健康状态和这个会话的记录；不会自动刷新");
        _billionButton.Click += (_, _) => RaiseRead();

        _overviewPage = Page("OpenCodeInsightsSection.Overview", "概况");
        _billionPage = Page("OpenCodeInsightsSection.Billion", "Billion");
        _pages = [_overviewPage, _billionPage];
        var pagesHost = new Grid();
        foreach (var page in _pages)
        {
            pagesHost.Children.Add(page);
        }

        _sections = new SelectorBar();
        AutomationProperties.SetAutomationId(_sections, "OpenCodeInsightsSections");
        AutomationProperties.SetName(_sections, "概况分区");
        foreach (var (id, text) in SectionLabels)
        {
            var item = new SelectorBarItem { Text = text };
            AutomationProperties.SetAutomationId(item, "OpenCodeInsightsSectionItem." + id);
            AutomationProperties.SetName(item, text);
            _sections.Items.Add(item);
        }

        _sections.SelectionChanged += (_, _) => ShowSelectedSection();
        _sections.SelectedItem = _sections.Items[0];
        ShowSelectedSection();

        _agents.ChildSessionRequested += (_, childSessionId) => ChildSessionRequested?.Invoke(this, childSessionId);
        // 页头圆环：Flyout 打开（页面决定是否读取）、ACP 组“读取”（与 Billion 分区按钮同一路径、同一显示上下文）、“设置地址”。
        _contextRing.DetailsOpening += (_, _) => ContextDetailsOpening?.Invoke(this, EventArgs.Empty);
        _contextRing.BillionReadRequested += (_, _) => RaiseRead();
        _contextRing.BillionSetupRequested += (_, _) =>
        {
            _contextRing.Flyout.Hide();
            ShowBillionSection();
            BillionSetupRequested?.Invoke(this, EventArgs.Empty);
        };

        var root = new StackPanel { Spacing = 12 };
        root.Children.Add(_usageHost);
        root.Children.Add(pagesHost);
        root.Children.Add(toolbar);
        root.Children.Add(_sections);
        Content = root;

        // 卸载后排队的界面更新作废；重新加载时补上仍待显示的状态（不在任何输入回调内）。
        Unloaded += (_, _) => CancelQueuedVisual();
        Loaded += (_, _) => FlushPendingVisual();
        // 状态徽标的背景取自主题画刷：主题切换时整体重绘一次（不在输入回调内；全量渲染会恢复文本框焦点与光标）。
        ActualThemeChanged += (_, _) => RenderForThemeChange();
    }

    private static readonly (string Id, string Text)[] SectionLabels =
    [
        ("Overview", "概况"),
        ("Billion", "Billion"),
    ];

    internal event EventHandler? RefreshRequested;

    /// <summary>显式读取：携带当前界面显示的渲染上下文（页面不得替换为新上下文）。</summary>
    internal event EventHandler<OpenCodeBillionContext>? BillionReadRequested;

    /// <summary>用户勾选/取消关联确认：携带当前界面显示的渲染上下文。</summary>
    internal event EventHandler<OpenCodeBillionConfirmRequest>? BillionConfirmationChanged;

    /// <summary>
    /// 用户真实编辑地址（在 TextChanging 内同步触发）：处理方须同步设置 <see cref="OpenCodeBillionOriginEdit.Accepted"/>，
    /// 且不得在回调内改动可视树。
    /// </summary>
    internal event EventHandler<OpenCodeBillionOriginEdit>? BillionOriginEdited;

    /// <summary>
    /// Agents 分区详情页点击“打开子会话”：只携带当前已显示快照中的子会话 ID，属导航提示、不构成授权；
    /// 页面须自行复核归属后才切换。
    /// </summary>
    internal event EventHandler<string>? ChildSessionRequested;

    /// <summary>页头圆环的 Flyout 正在打开（页面在当前会话尚无概况结果时显式读取一次）。</summary>
    internal event EventHandler? ContextDetailsOpening;

    /// <summary>ACP 组“设置 Billion 地址”：视图已切到 Billion 分区，页面负责打开右侧概况面板。</summary>
    internal event EventHandler? BillionSetupRequested;

    /// <summary>切到 Billion 分区（只改分区可见性，不重建）。</summary>
    internal void ShowBillionSection() => _sections.SelectedItem = _sections.Items[1];

    /// <summary>页头上下文圆环（常驻实例，页面只挂接一次其 <see cref="OpenCodeContextRing.Root"/>）。</summary>
    internal OpenCodeContextRing ContextRing => _contextRing;

    /// <summary>当前主 agent（页面在每次渲染前写入；只读显示，下一次全量渲染生效）。</summary>
    internal string? PrimaryAgent { get; set; }

    /// <summary>真实换源（引用/Revision/来源代际变化）时由页面调用：丢弃原始地址草稿，下一次渲染按新来源显示。</summary>
    internal void ForgetBillionOriginDraft()
    {
        _draftActive = false;
        _draftSource = null;
        _draftRevision = 0;
        _editRejected = false;
    }

    /// <summary>入口按钮的简短状态（tooltip / 无障碍名称用）。</summary>
    internal static string DescribeEntry(OpenCodeSessionInsightsSnapshot snapshot)
    {
        if (snapshot.UnavailableReason is { } reason) return "会话概况：" + reason;
        if (snapshot.Loading) return "会话概况：读取中";
        if (snapshot.SampledAt is not { } sampled) return "会话概况：用量、费用、上下文、to do 与 Agents";
        var cost = snapshot.Cost.Value is { Summary.HasKnownCost: true } view
            ? (snapshot.Cost.State == OpenCodeInsightState.Partial ? "已知部分 " : "") + Money(view.Summary.TotalCost)
            : "费用无法确认";
        return "会话概况：" + cost + "（更新于 " + Time(sampled) + "）";
    }

    /// <summary>
    /// 记录最新快照并更新界面。在地址输入回调（TextChanging）内只记录状态并排队，绝不改动可视树；
    /// 其余时机同步更新（仅 Billion 变化时原地更新，常驻控件不重新挂接）。
    /// </summary>
    internal void Render(OpenCodeSessionInsightsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _latest = snapshot;
        if (_inputDepth > 0)
        {
            ScheduleVisual();
            return;
        }

        RenderVisual(snapshot);
    }

    private void RenderVisual(OpenCodeSessionInsightsSnapshot snapshot)
    {
        // 任何同步渲染都使已排队的旧回调失效。
        _visualTicket++;
        _visualQueued = false;
        _visualPending = false;
        if (CanUpdateBillionInPlace(snapshot))
        {
            _rendering = true;
            try
            {
                _visual = null;
                UpdateBillionInPlace(snapshot);
                _visual = snapshot;
            }
            finally
            {
                _rendering = false;
            }

            return;
        }

        RenderFull(snapshot);
    }

    private void RenderForThemeChange()
    {
        if (_inputDepth > 0 || _rendering || _latest is not { } latest)
        {
            return;
        }

        // 强制全量：徽标画刷在构造时取值，只有重建才会换成新主题的值。
        _visual = null;
        RenderVisual(latest);
    }

    private void RenderFull(OpenCodeSessionInsightsSnapshot snapshot)
    {
        _rendering = true;
        _visual = null;
        var refocus = _billionOriginBox.FocusState != FocusState.Unfocused;
        var selectionStart = _billionOriginBox.SelectionStart;
        var selectionLength = _billionOriginBox.SelectionLength;
        try
        {
            _refreshRing.IsActive = snapshot.Loading;
            _refreshRing.Visibility = snapshot.Loading ? Visibility.Visible : Visibility.Collapsed;
            _refreshButton.IsEnabled = snapshot.UnavailableReason is null && !snapshot.Loading;
            _scopeText.Text = snapshot.UnavailableReason
                ?? (snapshot.SampledAt is { } sampled
                    ? "更新于 " + Time(sampled) + "；随会话事件自动更新。"
                    : snapshot.Loading ? "正在读取…" : "等待加载会话概况…");

            ReleaseBillionControls();
            foreach (var page in _pages)
            {
                page.Children.Clear();
            }

            // 页头圆环与面板同源：每次全量渲染都更新（不可用时显示“—”与原因）。
            _contextRing.Render(snapshot);
            _usageHost.Child = ContextUsageSection(snapshot);
            if (snapshot.UnavailableReason is not null)
            {
                _visual = snapshot;
                return;
            }

            // 常驻上下文在上，A10 原生 to do 卡片在下；子会话只进入用量，不混入 to do。
            var todoCard = Card();
            todoCard.Child = TodoSection(snapshot);
            AutomationProperties.SetAutomationId(todoCard, "OpenCodeInsightsTodoCard");
            _overviewPage.Children.Add(todoCard);
            _overviewPage.Children.Add(_agents.Render(snapshot.Children, PrimaryAgent));
            _billionPage.Children.Add(BillionSection(snapshot));
            _visual = snapshot;

            // 全量重建（例如 OpenCode 分区结果在输入期间到达）会重新挂接文本框：恢复焦点与光标。
            if (refocus)
            {
                _billionOriginBox.Focus(FocusState.Programmatic);
                if (selectionStart + selectionLength <= _billionOriginBox.Text.Length)
                {
                    _billionOriginBox.SelectionStart = selectionStart;
                    _billionOriginBox.SelectionLength = selectionLength;
                }
            }
        }
        finally
        {
            _rendering = false;
        }
    }

    // ------------------------------------------------------------------
    // 分区切换（只改 Visibility；不重建、不重新挂接）
    // ------------------------------------------------------------------

    private static StackPanel Page(string automationId, string name)
    {
        // 侧栏间距刻度：卡片内 8/12，卡片之间 12，分区之间 16。
        var page = new StackPanel { Spacing = 16, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(page, automationId);
        AutomationProperties.SetName(page, name);
        return page;
    }

    private void ShowSelectedSection()
    {
        var index = _sections.SelectedItem is { } selected ? _sections.Items.IndexOf(selected) : -1;
        if (index < 0)
        {
            // 不允许“无分区”：回到概况（SelectedItem 相同时不会再次触发）。
            index = 0;
            _sections.SelectedItem = _sections.Items[0];
        }

        for (var i = 0; i < _pages.Length; i++)
        {
            _pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ------------------------------------------------------------------
    // 概况分区
    // ------------------------------------------------------------------

    // 上下文参考圆环（含“当前上下文 / 用量计费 / ACP·Billion 状态”三组 Flyout）见 OpenCodeContextRing；
    // MCP / 插件 / LSP 服务状态移到页头连接状态按钮的 Flyout（OpenCodeConnectionFlyout）。

    private static UIElement ContextUsageSection(OpenCodeSessionInsightsSnapshot snapshot)
    {
        var panel = new StackPanel { Spacing = 8 };
        AutomationProperties.SetAutomationId(panel, "OpenCodeInsightsLayeredUsage");
        var heading = new Grid { ColumnSpacing = 8 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(OpenCodeInteractionUi.StyledText("上下文", OpenCodeInteractionUi.BodyStrongStyleKey));
        var turns = Caption(snapshot.Turns is { } count ? count + " 轮" : "轮次不可用");
        turns.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(turns, "OpenCodeInsightsTurns");
        Grid.SetColumn(turns, 1);
        heading.Children.Add(turns);
        panel.Children.Add(heading);

        // 主数字：used / limit (pct%) 用 Subtitle 字阶单行显示，窄抽屉内省略号兜底（Tooltip 仍给出说明）。
        var current = OpenCodeInteractionUi.StyledText(CurrentContextLine(snapshot), OpenCodeInteractionUi.SubtitleStyleKey, selectable: true);
        current.TextWrapping = TextWrapping.NoWrap;
        current.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetAutomationId(current, "OpenCodeInsightsCurrentContext");
        ToolTipService.SetToolTip(current, "所选会话主 Agent 最近一次请求的上下文参考；不含子会话。撤回、压缩或模型切换后等待新的用量。");
        panel.Children.Add(current);

        var summary = snapshot.Cost.Value?.Summary;
        var knownTokens = summary?.HasKnownTokens == true;
        var knownCost = summary?.HasKnownCost == true;
        var tokens = knownTokens ? summary!.Tokens : null;
        var prefix = summary?.Incomplete == true ? "已知部分 " : "";
        string Token(double? value) => value is { } number ? prefix + Tokens(number) : "不可用";
        panel.Children.Add(Table(new List<(string, string)>
        {
            ("📥 输入", Token(tokens?.Input)),
            ("📤 输出", Token(tokens?.Output)),
            ("📖 缓存读取", Token(tokens?.CacheRead)),
            ("📝 缓存写入", Token(tokens?.CacheWrite)),
            ("🎯 缓存命中率", tokens?.CacheHitPercent is { } hit ? prefix + hit.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "不可用"),
            ("💭 思考 tokens", Token(tokens?.Reasoning)),
        }));
        panel.Children.Add(OpenCodeContextRing.Divider());
        var totals = Table(new List<(string, string)>
        {
            ("总 tokens", Token(tokens?.Total)),
            ("总费用", knownCost ? prefix + Money(summary!.TotalCost) : "不可用"),
        });
        // 合计行：标签升为主文字色、数值加粗，与上方明细行（次要标签 / 常规数值）拉开层级。Table 子项按“标签、数值”成对追加。
        for (var index = 0; index + 1 < totals.Children.Count; index += 2)
        {
            if (totals.Children[index] is TextBlock label) label.Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CaptionStyleKey);
            if (totals.Children[index + 1] is TextBlock value) value.Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.BodyStrongStyleKey);
        }
        panel.Children.Add(totals);
        var note = Caption(summary is null ? "主会话及子会话累计用量：" + snapshot.Cost.Note
            : "主会话及全部子会话，按 parentID 去重累计；输入不含缓存。" + (summary.Incomplete ? "部分数据未能读取。" : ""));
        ToolTipService.SetToolTip(note, "总 tokens = 输入 + 输出 + 思考 + 缓存读取 + 缓存写入；缓存命中率 = 缓存读取 /（输入 + 缓存读取 + 缓存写入）。费用直接累计 Session.Info.cost，不重新计价，不加消息费用或 Billion 账单。");
        panel.Children.Add(note);
        return panel;
    }

    internal static string CurrentContextLine(OpenCodeSessionInsightsSnapshot snapshot)
    {
        if (snapshot.UnavailableReason is not null || snapshot.Context.Value is not { } estimate
            || estimate.StaleReasons.Any(reason => reason is not OpenCodeContextStaleReason.HistoryIncomplete
                and not OpenCodeContextStaleReason.ModelLimitUnavailable)) return "不可用";
        var (_, tokens, limit) = OpenCodeContextRing.Measure(snapshot);
        if (tokens is not { } used) return "不可用";
        return Compact(used) + " / " + (limit is { } max ? Compact(max) + " ("
            + Math.Round(used / max * 100, 1).ToString("0.#", CultureInfo.InvariantCulture) + "%)" : "- (-)");
    }

    // ------------------------------------------------------------------
    // to do（概况分区内）— 对齐官方 v2.1.1 sidebar：标题 "to do"；超过 2 项时标题可折叠，
    // 折叠后显示“（N 项未完成）”，N = pending + in_progress；展开状态在本次应用运行期间记住。
    // ------------------------------------------------------------------

    internal const string TodoTitle = "to do";

    /// <summary>官方超过此数量才允许折叠（≤2 项始终展开）。</summary>
    internal const int TodoCollapsibleThreshold = 2;

    /// <summary>官方默认展开；本次应用运行期间记住用户的折叠选择（不落盘）。</summary>
    internal static bool TodoExpanded { get; set; } = true;

    /// <summary>
    /// 每行状态图标（Segoe Fluent Icons，只读外观，不是可勾选控件），与官方 [✓] [•] [ ] [-] 一一对应。
    /// 保持服务端原始顺序、不按状态分组。
    /// </summary>
    internal static string TodoGlyph(string status) => status switch
    {
        "completed" => "\uE73E",   // CheckMark          ≈ [✓]
        "in_progress" => "\uECCC", // RadioBtnOn（实心点）≈ [•]
        "cancelled" => "\uE738",   // Remove（短横）      ≈ [-]
        _ => "\uECCA",             // RadioBtnOff（空圈） ≈ [ ]（pending / 规范化后的未知状态）
    };

    internal static string TodoRemainingText(int remaining) => "（" + remaining + " 项未完成）";

    private static UIElement TodoSection(OpenCodeSessionInsightsSnapshot snapshot)
    {
        var section = snapshot.Todo;
        var todo = section.Value;
        var collapsible = todo is { State: OpenCodeTodoState.Listed } && todo.Items.Length > TodoCollapsibleThreshold;
        var expanded = !collapsible || TodoExpanded;

        var panel = new StackPanel { Spacing = 8 };
        var content = new StackPanel { Spacing = 8 };
        AutomationProperties.SetAutomationId(panel, "OpenCodeInsightsTodo");
        AutomationProperties.SetName(panel, TodoTitle + "：" + (todo is null ? DescribeState(section.State) : DescribeTodoState(todo)));
        var badge = todo is null ? Badge(section.State) : TodoBadge(todo);

        var heading = new Grid { ColumnSpacing = 6 };
        if (collapsible)
        {
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var column = 0;
        FontIcon? chevron = null;
        if (collapsible)
        {
            chevron = new FontIcon { Glyph = expanded ? "\uE70D" : "\uE76C", FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
            heading.Children.Add(chevron);
            column++;
        }
        var title = OpenCodeInteractionUi.StyledText(TodoTitle, OpenCodeInteractionUi.BodyStrongStyleKey);
        title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(title, column++);
        heading.Children.Add(title);
        var remaining = OpenCodeInteractionUi.StyledText(TodoRemainingText(todo?.Remaining ?? 0), OpenCodeInteractionUi.CaptionSubtleStyleKey);
        remaining.VerticalAlignment = VerticalAlignment.Center;
        remaining.TextWrapping = TextWrapping.NoWrap;
        remaining.Visibility = collapsible && !expanded ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetAutomationId(remaining, "OpenCodeInsightsTodoRemaining");
        Grid.SetColumn(remaining, column++);
        heading.Children.Add(remaining);
        if (badge is not null)
        {
            Grid.SetColumn(badge, column);
            heading.Children.Add(badge);
        }

        if (collapsible)
        {
            var toggle = new Button
            {
                Content = heading,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(4, 2, 4, 2),
                // 负外边距抵消按钮内边距：可折叠标题与不可折叠标题、列表图标保持同一左缘。
                Margin = new Thickness(-4, -2, -4, -2),
            };
            if (OpenCodeInteractionUi.TryStyle("OpenCodeTransparentToolButtonStyle") is { } style)
            {
                toggle.Style = style;
            }
            AutomationProperties.SetAutomationId(toggle, "OpenCodeInsightsTodoToggle");
            AutomationProperties.SetName(toggle, TodoToggleName(expanded, todo!.Remaining));
            toggle.Click += (_, _) =>
            {
                TodoExpanded = !TodoExpanded;
                var open = TodoExpanded;
                chevron!.Glyph = open ? "\uE70D" : "\uE76C";
                remaining.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
                content.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                AutomationProperties.SetName(toggle, TodoToggleName(open, todo.Remaining));
            };
            panel.Children.Add(toggle);
        }
        else
        {
            panel.Children.Add(heading);
        }
        panel.Children.Add(content);
        content.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;

        if (todo is null)
        {
            content.Children.Add(Caption(section.Note));
            return panel;
        }

        if (todo.State != OpenCodeTodoState.Listed)
        {
            // 未知 / 读取失败 / 未启用 / 空：只保留一行说明（空态最淡，接近官方“空则隐藏”）。
            content.Children.Add(Caption(todo.Reason, todo.State == OpenCodeTodoState.Empty ? 0.55 : 0.75));
            return panel;
        }

        var list = new StackPanel { Spacing = 6 };
        AutomationProperties.SetAutomationId(list, "OpenCodeInsightsTodoList");
        AutomationProperties.SetName(list, TodoTitle + " 列表（服务端顺序）");
        for (var index = 0; index < todo.Items.Length; index++)
        {
            list.Children.Add(TodoRow(todo.Items[index], index));
        }

        content.Children.Add(list);
        if (!todo.Complete)
        {
            // 只有“可能不完整”才需要说明。
            content.Children.Add(Caption(todo.Reason, 0.55));
        }

        return panel;
    }

    private static string TodoToggleName(bool expanded, int remaining)
        => TodoTitle + "，" + (expanded ? "已展开" : "已折叠") + "，" + remaining + " 项未完成";

    private static UIElement TodoRow(OpenCodeTodoItem item, int index)
    {
        var finished = item.Status is "completed" or "cancelled";
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // 图标与首行文字基线对齐；完成 / 取消弱化，进行中加粗强调（对应官方的高亮行）。
        var icon = new FontIcon
        {
            Glyph = TodoGlyph(item.Status), FontSize = 14, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, 0, 0), Opacity = finished ? 0.55 : 1,
        };
        if (finished) icon.Style = OpenCodeInsightsTheme.TodoSecondaryIcon();
        var text = OpenCodeInteractionUi.StyledText(item.Content,
            item.Status == "in_progress" ? OpenCodeInteractionUi.BodyStrongStyleKey
                : finished ? OpenCodeInteractionUi.BodySubtleStyleKey : OpenCodeInteractionUi.BodyStyleKey, selectable: true);
        Grid.SetColumn(text, 1);
        row.Children.Add(icon);
        row.Children.Add(text);
        AutomationProperties.SetAutomationId(row, "OpenCodeInsightsTodoItem." + index);
        AutomationProperties.SetName(row, DescribeTodoStatus(item.Status) + "：" + item.Content);
        return row;
    }

    private static Border? TodoBadge(OpenCodeTodoSnapshot todo) => todo.State switch
    {
        OpenCodeTodoState.Unknown => BadgeOf("未知", "OpenCodeNeutralBadgeStyle"),
        OpenCodeTodoState.ReadFailure => BadgeOf("读取失败", "OpenCodeCriticalBadgeStyle"),
        OpenCodeTodoState.Disabled => BadgeOf("未启用", "OpenCodeNeutralBadgeStyle"),
        _ when !todo.Complete => BadgeOf("部分", "OpenCodeCautionBadgeStyle"),
        _ => null,
    };

    private static string DescribeTodoState(OpenCodeTodoSnapshot todo) => todo.State switch
    {
        OpenCodeTodoState.Unknown => "状态未知",
        OpenCodeTodoState.ReadFailure => "读取失败",
        OpenCodeTodoState.Disabled => "后端未启用",
        OpenCodeTodoState.Empty => todo.Complete ? "空" : "空（部分数据）",
        _ => todo.Items.Length + " 项，" + todo.Remaining + " 项未完成" + (todo.Complete ? "" : "（部分数据）"),
    };

    // ------------------------------------------------------------------
    // Billion 分区
    // ------------------------------------------------------------------

    private UIElement BillionSection(OpenCodeSessionInsightsSnapshot snapshot)
    {
        var state = BillionState(snapshot.Billion);
        var (panel, content) = Section(BillionTitle, state, "OpenCodeInsightsBillion");
        _billionSection = panel;
        _billionHeading = (Grid)panel.Children[0];
        _billionBadge = _billionHeading.Children.Count > 1 ? _billionHeading.Children[1] as Border : null;
        content.Children.Add(Caption(BillionIntro, 0.75));

        // 先记录宿主再加入：即使中途失败，下一次 Render 也能从这个面板取回已加入的控件。
        _billionHost = content;
        UpdateBillionControls(snapshot);
        content.Children.Add(_billionOriginPanel);
        content.Children.Add(_billionConfirm);
        content.Children.Add(_billionButton);

        var result = new StackPanel { Spacing = 4 };
        FillBillionResult(result, snapshot.Billion);
        content.Children.Add(result);
        _billionResult = result;

        content.Children.Add(StateLine("压缩开关", new OpenCodeCapabilityState(
            OpenCodeCapabilityAvailability.Unavailable, BillionCapabilities.CompressionToggleReason)));
        return panel;
    }

    private const string BillionTitle = "Billion（本机插件记录）";

    internal const string BillionIntro = "只有此 Billion 实例对应当前 OpenCode 连接，它的记录才可用于本会话；"
        + "健康检查通过或会话 ID 相同都不能证明来自同一个服务。";

    internal const string BillionOriginHint = "只填根地址：http://、本机主机（localhost / 127.0.0.1 / [::1]）和端口，不含路径。"
        + "可取自 OpenCode 中 Billion 插件提供的 Web 地址的根部分；插件的常用端口不代表正在运行的就是这个实例。"
        + "地址只保存在内存中，切换 OpenCode 连接后需重新填写。";

    internal static OpenCodeInsightState BillionState(OpenCodeBillionView billion) => billion.Phase switch
    {
        OpenCodeBillionPhase.Loading => OpenCodeInsightState.Loading,
        OpenCodeBillionPhase.Loaded => OpenCodeInsightState.Loaded,
        OpenCodeBillionPhase.NoRecord => OpenCodeInsightState.Unavailable,
        OpenCodeBillionPhase.Failed => OpenCodeInsightState.Failed,
        _ => OpenCodeInsightState.NotLoaded,
    };

    /// <summary>常驻控件的属性（程序写入在 _rendering 下进行，不产生用户事件）。</summary>
    private void UpdateBillionControls(OpenCodeSessionInsightsSnapshot snapshot)
    {
        var context = snapshot.BillionContext;
        if (context is not null
            && (!ReferenceEquals(_draftSource, context.Binding.Source) || _draftRevision != context.Binding.Revision))
        {
            // 新来源（或草稿已被丢弃）：不沿用旧草稿，显示该来源已生效的地址（通常为空）。
            _draftSource = context.Binding.Source;
            _draftRevision = context.Binding.Revision;
            _draftActive = false;
            _editRejected = false;
        }

        if (context is null && _draftSource is null && _billionOriginBox.Text.Length > 0)
        {
            // 草稿已随换源丢弃、新来源尚未绑定：不在新来源下继续显示旧来源的文字。
            _billionOriginBox.Text = string.Empty;
        }
        else if (context is not null && !_draftActive)
        {
            var shown = snapshot.BillionOrigin ?? string.Empty;
            if (!string.Equals(_billionOriginBox.Text, shown, StringComparison.Ordinal))
            {
                _billionOriginBox.Text = shown;
            }
        }

        var originUsable = snapshot.BillionOriginState is OpenCodeBillionOriginState.Unconfirmed
            or OpenCodeBillionOriginState.Confirmed;
        _billionOriginBox.IsEnabled = context is not null;
        _billionOriginStatus.Text = DescribeOriginStatus(snapshot) + (_editRejected ? "\n刚才的修改未生效（面板状态已变化），请重新输入。" : string.Empty);
        _billionConfirm.IsChecked = snapshot.BillionConfirmed;
        _billionConfirm.IsEnabled = context is not null && originUsable;
        _billionButton.IsEnabled = context is not null && snapshot.BillionConfirmed
            && snapshot.Billion.Phase != OpenCodeBillionPhase.Loading;
    }

    internal static string DescribeOriginStatus(OpenCodeSessionInsightsSnapshot snapshot)
    {
        var state = snapshot.BillionOriginState switch
        {
            OpenCodeBillionOriginState.Invalid => "地址无效",
            OpenCodeBillionOriginState.Unconfirmed => "未确认关联",
            OpenCodeBillionOriginState.Confirmed => "关联由你确认",
            _ => "未填写",
        };
        return "当前生效地址：" + (snapshot.BillionOrigin ?? "无") + "（" + state + "）\n" + snapshot.BillionOriginNote;
    }

    private static void FillBillionResult(StackPanel content, OpenCodeBillionView billion)
    {
        if (billion.Phase != OpenCodeBillionPhase.Idle || billion.Stats is not null)
        {
            content.Children.Add(Caption(billion.Note, 0.85));
        }

        if (billion.Origin is { } origin)
        {
            var source = Caption("这次结果读取自 " + origin + "（读取时的地址；之后修改地址不会改写它）", 0.65);
            AutomationProperties.SetAutomationId(source, "OpenCodeInsightsBillionResultOrigin");
            content.Children.Add(source);
        }

        if (billion.Phase == OpenCodeBillionPhase.Loaded && billion.Stats is { } stats)
        {
            content.Children.Add(Table(new List<(string, string)>
            {
                ("最近上下文参考", Count(stats.ContextTokens) + DescribeContextSource(stats.ContextTokensSource)),
                ("上下文窗口", Count(stats.ContextWindow)),
                ("系统提示", Count(stats.SystemPromptTokens)),
                ("输入", Count(stats.InputTokens)),
                ("缓存命中", Count(stats.CachedTokens)),
                ("输出", Count(stats.OutputTokens)),
                ("缓存命中率", stats.CacheHitPct is { } pct && double.IsFinite(pct)
                    ? pct.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "未知"),
                ("请求数", Count(stats.Requests)),
                ("压缩块", Count(stats.Blocks)),
                ("节省 tokens", Count(stats.TokensSaved)),
                ("内存记录", DescribeMemoryRecord(stats.Live)),
                ("最近活动", LocalDateTime(stats.LastSeen)),
                ("Billion 版本", billion.Health?.Version ?? stats.BiliVersion ?? "未知"),
                ("读取时间", Time(stats.FetchedAt)),
            }));
            content.Children.Add(Caption(BillionStatsNote, 0.65));
        }
    }

    /// <summary>上次全量渲染仍在且只有 Billion 相关字段变化时，可原地更新（不重新挂接常驻控件）。</summary>
    private bool CanUpdateBillionInPlace(OpenCodeSessionInsightsSnapshot snapshot)
        => _visual is { UnavailableReason: null } visual && snapshot.UnavailableReason is null
        && _billionHost is not null && _billionResult is not null && _billionHeading is not null && _billionSection is not null
        && WithoutBillion(visual) == WithoutBillion(snapshot);

    private static OpenCodeSessionInsightsSnapshot WithoutBillion(OpenCodeSessionInsightsSnapshot snapshot) => snapshot with
    {
        Billion = OpenCodeBillionView.Idle,
        BillionConfirmed = false,
        BillionOrigin = null,
        BillionOriginState = OpenCodeBillionOriginState.Missing,
        BillionContext = null,
        BillionOriginNote = string.Empty,
        BillionDiscovery = null,
        BillionDiscoveryLoading = false,
    };

    private void UpdateBillionInPlace(OpenCodeSessionInsightsSnapshot snapshot)
    {
        var state = BillionState(snapshot.Billion);
        if (_billionBadge is { } oldBadge)
        {
            _billionHeading!.Children.Remove(oldBadge);
            _billionBadge = null;
        }

        if (Badge(state) is { } badge)
        {
            Grid.SetColumn(badge, 1);
            _billionHeading!.Children.Add(badge);
            _billionBadge = badge;
        }

        AutomationProperties.SetName(_billionSection!, BillionTitle + "：" + DescribeState(state));
        UpdateBillionControls(snapshot);
        _billionResult!.Children.Clear();
        FillBillionResult(_billionResult, snapshot.Billion);
        // 圆环 Flyout 的“ACP / Billion 状态”组随之更新（Flyout 内容不含常驻文本框）。
        _contextRing.RenderFlyoutOnly(snapshot);
    }

    // ------------------------------------------------------------------
    // 构件
    // ------------------------------------------------------------------

    /// <summary>界面显示的上下文；界面落后于状态（有待显示更新）时视为无效，先刷新界面、不转发。</summary>
    private OpenCodeBillionContext? DisplayedContext()
    {
        if (_visualPending || _visual?.BillionContext is not { } context)
        {
            if (_latest is { } latest)
            {
                RenderVisual(latest);
            }

            return null;
        }

        return context;
    }

    private void RaiseConfirm(bool value)
    {
        if (_rendering)
        {
            return;
        }

        if (DisplayedContext() is { } context)
        {
            BillionConfirmationChanged?.Invoke(this, new OpenCodeBillionConfirmRequest(context, value));
        }
    }

    private void RaiseRead()
    {
        if (DisplayedContext() is { } context)
        {
            BillionReadRequested?.Invoke(this, context);
        }
    }

    /// <summary>
    /// TextChanging（同步、文本已更新、尚未呈现）：立即转发编辑并记录结果；界面更新一律排队，不改动可视树。
    /// </summary>
    private void OnOriginTextChanging(bool contentChanging)
    {
        if (_rendering || !contentChanging)
        {
            return;
        }

        _inputDepth++;
        try
        {
            _draftActive = true;
            var context = _latest?.BillionContext;
            var accepted = false;
            if (context is not null && ReferenceEquals(_draftSource, context.Binding.Source)
                && _draftRevision == context.Binding.Revision)
            {
                var edit = new OpenCodeBillionOriginEdit(context, _billionOriginBox.Text);
                BillionOriginEdited?.Invoke(this, edit);
                accepted = edit.Accepted;
            }

            _editRejected = !accepted;
            _draftActive = !accepted || !string.IsNullOrWhiteSpace(_billionOriginBox.Text);
        }
        finally
        {
            _inputDepth--;
        }

        ScheduleVisual();
    }

    /// <summary>合并排队一次界面更新（每个 ticket 最多一个回调；卸载或更新的同步渲染使其作废）。</summary>
    private void ScheduleVisual()
    {
        _visualPending = true;
        if (_visualQueued)
        {
            return;
        }

        var ticket = _visualTicket;
        _visualQueued = true;
        bool queued;
        try
        {
            queued = DispatcherQueue is { } queue && queue.TryEnqueue(() => OnQueuedVisual(ticket));
        }
        catch
        {
            queued = false;
        }

        if (!queued)
        {
            // 无法排队：保持待显示，等下一次非输入时机（失焦 / 加载 / 下一次渲染 / 点击）补上。
            _visualQueued = false;
        }
    }

    private void OnQueuedVisual(long ticket)
    {
        if (ticket != _visualTicket)
        {
            return;
        }

        _visualQueued = false;
        FlushPendingVisual();
    }

    private void FlushPendingVisual()
    {
        if (_visualPending && _inputDepth == 0 && !_rendering && _latest is { } latest)
        {
            RenderVisual(latest);
        }
    }

    private void CancelQueuedVisual()
    {
        _visualTicket++;
        _visualQueued = false;
    }

    /// <summary>把常驻 Billion 控件从上次的内容面板中移除（在清空 _body 之前调用）。</summary>
    private void ReleaseBillionControls()
    {
        if (_billionHost is { } host)
        {
            host.Children.Remove(_billionOriginPanel);
            host.Children.Remove(_billionConfirm);
            host.Children.Remove(_billionButton);
            _billionHost = null;
        }

        _billionSection = null;
        _billionHeading = null;
        _billionBadge = null;
        _billionResult = null;

        // 兜底：万一控件被放进了其他面板。
        Detach(_billionOriginPanel);
        Detach(_billionConfirm);
        Detach(_billionButton);
    }

    private static void Detach(FrameworkElement element)
    {
        if (element.Parent is Panel parent)
        {
            parent.Children.Remove(element);
        }
    }

    // ------------------------------------------------------------------
    // 构件（Agents 分区共用）
    // ------------------------------------------------------------------

    internal static (StackPanel Panel, StackPanel Content) Section(string title, OpenCodeInsightState state, string automationId)
    {
        var heading = new Grid { ColumnSpacing = 8 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(OpenCodeInteractionUi.StyledText(title, OpenCodeInteractionUi.BodyStrongStyleKey));
        if (Badge(state) is { } badge)
        {
            Grid.SetColumn(badge, 1);
            heading.Children.Add(badge);
        }

        var content = new StackPanel { Spacing = 8 };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(heading);
        panel.Children.Add(content);
        AutomationProperties.SetAutomationId(panel, automationId);
        AutomationProperties.SetName(panel, title + "：" + DescribeState(state));
        return (panel, content);
    }

    private static Border? Badge(OpenCodeInsightState state)
    {
        string? text = state switch
        {
            OpenCodeInsightState.Loading => "读取中",
            OpenCodeInsightState.Partial => "部分",
            OpenCodeInsightState.Failed => "无法确认",
            OpenCodeInsightState.Unavailable => "不可用",
            OpenCodeInsightState.NotLoaded => "未读取",
            _ => null,
        };
        var style = state switch
        {
            OpenCodeInsightState.Partial => "OpenCodeCautionBadgeStyle",
            OpenCodeInsightState.Failed => "OpenCodeCriticalBadgeStyle",
            _ => "OpenCodeNeutralBadgeStyle",
        };
        return text is null ? null : BadgeOf(text, style);
    }

    private static Border BadgeOf(string text, string style)
    {
        // Brush references remain theme-aware for the entire badge lifetime.
        return new Border
        {
            Style = OpenCodeInteractionUi.TryStyle(style),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 0, 6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = OpenCodeInteractionUi.StyledText(text, OpenCodeInteractionUi.CaptionStyleKey),
        };
    }

    internal static string DescribeState(OpenCodeInsightState state) => state switch
    {
        OpenCodeInsightState.Loading => "读取中",
        OpenCodeInsightState.Loaded => "已读取",
        OpenCodeInsightState.Partial => "部分数据",
        OpenCodeInsightState.Failed => "无法确认",
        OpenCodeInsightState.Unavailable => "不可用",
        _ => "未读取",
    };

    /// <summary>大数字（内置 Subtitle 样式）+ 次要说明。</summary>
    internal static FrameworkElement Figure(string value, string caption)
    {
        var panel = new StackPanel { Spacing = 0 };
        var number = OpenCodeInteractionUi.StyledText(value, OpenCodeInteractionUi.SubtitleStyleKey, selectable: true);
        AutomationProperties.SetName(number, caption + "：" + value);
        panel.Children.Add(number);
        panel.Children.Add(Caption(caption, 0.75));
        return panel;
    }

    /// <param name="monospace">按行标签决定值是否用等宽样式（OpenCodeCodeTextBlockStyle：ThemeResource AppMonospaceFontFamily，
    /// 随用户字体设置实时更新；不硬编码字体名）。用于版本号、地址、路径等。</param>
    internal static Grid Table(IReadOnlyList<(string Label, string Value)> rows, Func<string, bool>? monospace = null)
    {
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var index = 0; index < rows.Count; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = Caption(rows[index].Label, 0.75);
            label.IsTextSelectionEnabled = false;
            var value = OpenCodeInteractionUi.StyledText(rows[index].Value,
                monospace?.Invoke(rows[index].Label) == true ? OpenCodeInteractionUi.CodeStyleKey : OpenCodeInteractionUi.CaptionStyleKey,
                selectable: true);
            value.TextAlignment = TextAlignment.Right;
            value.MaxWidth = 200;
            AutomationProperties.SetName(value, rows[index].Label + "：" + rows[index].Value);
            Grid.SetRow(label, index);
            Grid.SetRow(value, index);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
        }

        return grid;
    }

    internal static UIElement StateLine(string label, OpenCodeCapabilityState state)
    {
        var text = state.Availability switch
        {
            OpenCodeCapabilityAvailability.Available => "可用",
            OpenCodeCapabilityAvailability.Unavailable => "不可用",
            _ => "无法确认",
        };
        var row = Table(new List<(string, string)> { (label, text) });
        ToolTipService.SetToolTip(row, state.Reason);
        AutomationProperties.SetHelpText(row, state.Reason);
        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(row);
        var isChildRow = label.StartsWith("　", StringComparison.Ordinal);
        if (!isChildRow || state.Availability != OpenCodeCapabilityAvailability.Available)
        {
            panel.Children.Add(Caption(state.Reason, 0.55));
        }

        return panel;
    }

    internal static UIElement Bullets(string title, IEnumerable<string> items)
    {
        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(Caption(title, 0.85));
        foreach (var item in items)
        {
            panel.Children.Add(Caption("· " + item, 0.75));
        }

        return panel;
    }

    /// <summary>
    /// 说明文字：内置 Caption 字号；原 opacity 参数只区分“主要说明”（≥0.8，主文字色）与“次要说明”（次要文字色样式），
    /// 不再用 Opacity 降低对比度（样式内为 ThemeResource，随主题即时更新）。
    /// </summary>
    internal static TextBlock Caption(string text, double emphasis = 0.65)
        => OpenCodeInteractionUi.StyledText(text,
            emphasis >= 0.8 ? OpenCodeInteractionUi.CaptionStyleKey : OpenCodeInteractionUi.CaptionSubtleStyleKey, selectable: true);

    private static Style? _cardStyle;
    private static bool _cardStyleLoaded;

    /// <summary>右栏卡片：内置卡片画刷 / 描边（ThemeResource，随主题切换），统一 8 圆角、12 内边距。</summary>
    private static Border Card()
    {
        if (!_cardStyleLoaded)
        {
            _cardStyleLoaded = true;
            try
            {
                _cardStyle = XamlReader.Load(
                    "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"Border\">"
                    + "<Setter Property=\"Background\" Value=\"{ThemeResource CardBackgroundFillColorDefaultBrush}\" />"
                    + "<Setter Property=\"BorderBrush\" Value=\"{ThemeResource CardStrokeColorDefaultBrush}\" />"
                    + "<Setter Property=\"BorderThickness\" Value=\"1\" /></Style>") as Style;
            }
            catch
            {
                _cardStyle = null; // 回退：无底色，仅保留间距（与原来一致）。
            }
        }

        return new Border { Style = _cardStyle, Padding = new Thickness(12), CornerRadius = new CornerRadius(8) };
    }

    internal static TextBlock Body(string text)
        => OpenCodeInteractionUi.StyledText(text, OpenCodeInteractionUi.BodyStyleKey, selectable: true);

    /// <summary>
    /// 紧凑数字（wave5 H10）：&lt;1000 原样取整；其后保留一位小数并去掉多余的 0：82.3K / 1M / 10.2M / 1.5B。
    /// 进位跨档时升档（999,960 → 1M，不显示 1000K）。概况与上下文 Flyout 的 token / 计数统一使用。
    /// </summary>
    internal static string Compact(double value)
    {
        if (!double.IsFinite(value)) return "未知";
        var sign = value < 0 ? "-" : "";
        var abs = Math.Abs(value);
        if (Math.Round(abs) < 1_000) return sign + Math.Round(abs).ToString("0", CultureInfo.InvariantCulture);
        (double Scale, string Suffix)[] units = [(1_000, "K"), (1_000_000, "M"), (1_000_000_000, "B"), (1_000_000_000_000, "T")];
        for (var i = 0; i < units.Length; i++)
        {
            var scaled = Math.Round(abs / units[i].Scale, 1, MidpointRounding.AwayFromZero);
            if (scaled < 1_000 || i == units.Length - 1)
            {
                return sign + scaled.ToString("0.#", CultureInfo.InvariantCulture) + units[i].Suffix;
            }
        }

        return sign + abs.ToString("0", CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------
    // 文案与格式
    // ------------------------------------------------------------------

    /// <summary>token 数（紧凑格式，见 <see cref="Compact"/>）。</summary>
    internal static string Tokens(double value) => Compact(value);

    /// <summary>可缺省计数（紧凑格式；null = 未知，绝不当作 0）。</summary>
    internal static string Count(long? value) => value is { } number ? Compact(number) : "未知";

    internal static string Money(double value)
        => !double.IsFinite(value) ? "未知"
            : "$" + value.ToString(value >= 1 ? "N2" : "0.00##", CultureInfo.InvariantCulture);

    internal static string Price(double? value)
        => value is { } price && double.IsFinite(price) ? "$" + price.ToString("0.####", CultureInfo.InvariantCulture) : "未提供";

    internal static string Ratio(double? value)
        => value is { } ratio && double.IsFinite(ratio) ? "×" + ratio.ToString("0.###", CultureInfo.InvariantCulture) : "无法换算";

    internal static string Time(DateTimeOffset value) => value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    // Billion 的记录可能是重启后恢复的旧数据，只显示时刻会让旧记录看起来像刚刚发生，所以这里保留完整日期。
    internal static string LocalDateTime(DateTimeOffset? value)
        => value is { } time
            ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "（本机时间）"
            : "未知";

    // Live 只表示 Billion 内存里有这条记录（包括恢复出的旧记录），不能说明会话正在工作。
    internal static string DescribeMemoryRecord(bool? live) => live switch
    {
        true => "存在（不代表正在工作）",
        false => "不在内存中",
        null => "未知",
    };

    internal const string BillionStatsNote = "上下文为最近一次记录的用量参考或估算，不是实时占用；内存中有记录不代表会话正在运行。";

    internal static string DescribeTier(OpenCodeModelCostTier? tier)
        => tier is null ? "基础档"
            : tier.Type == "context" && tier.Size is { } size ? "上下文超过 " + size.ToString("N0", CultureInfo.InvariantCulture) + " tokens 的档位"
            : "未识别的档位";

    private static string DescribeContextSource(BillionContextTokensSource source) => source switch
    {
        BillionContextTokensSource.Usage => "（按用量）",
        BillionContextTokensSource.Estimate => "（估算）",
        _ => "",
    };

    internal static string DescribeStale(OpenCodeContextStaleReason reason) => reason switch
    {
        OpenCodeContextStaleReason.HistoryIncomplete => "本页尚未加载完整的消息历史",
        OpenCodeContextStaleReason.NoAssistantUsage => "已加载消息中没有可用的 assistant 用量",
        OpenCodeContextStaleReason.UnknownBoundary => "部分消息缺少时间，无法判断先后",
        OpenCodeContextStaleReason.ModelChanged => "最近的用量来自其他模型或变体",
        OpenCodeContextStaleReason.ProviderChanged => "最近的用量来自其他提供方，或当前模型未知",
        OpenCodeContextStaleReason.ModelSwitchAfterUsage => "之后切换过模型",
        OpenCodeContextStaleReason.CompactionAfterUsage => "之后发生过上下文压缩",
        OpenCodeContextStaleReason.ModelLimitUnavailable => "无法取得当前模型的上下文上限",
        _ => "原因未知",
    };

    internal static string DescribeUsage(OpenCodeUsageIncompleteReason reason) => reason switch
    {
        OpenCodeUsageIncompleteReason.RootUnavailable => "主会话读取失败",
        OpenCodeUsageIncompleteReason.RootHasParent => "主会话带有父会话",
        OpenCodeUsageIncompleteReason.ReadFailed => "部分子会话列表读取失败",
        OpenCodeUsageIncompleteReason.RepeatedSessionOrCycle => "出现重复或循环的会话",
        OpenCodeUsageIncompleteReason.RepeatedPage or OpenCodeUsageIncompleteReason.InvalidCursor
            or OpenCodeUsageIncompleteReason.MissingCursor => "分页异常，已停止继续读取",
        OpenCodeUsageIncompleteReason.ParentMismatch => "列表中有不属于该父会话的项（已排除）",
        OpenCodeUsageIncompleteReason.SessionBudgetExhausted or OpenCodeUsageIncompleteReason.PageBudgetExhausted
            => "会话太多，超过本次读取上限",
        OpenCodeUsageIncompleteReason.InvalidSession or OpenCodeUsageIncompleteReason.InvalidUsage => "部分会话缺少用量字段",
        OpenCodeUsageIncompleteReason.NumericOverflow => "数值过大无法相加",
        _ => "原因未知",
    };

    internal static string DescribeTodoStatus(string status) => status switch
    {
        "in_progress" => "进行中",
        "completed" => "已完成",
        "cancelled" => "已取消",
        _ => "未开始",
    };
}

/// <summary>地址编辑事件：Context 为最近一次已确认的渲染上下文；处理方同步写回 Accepted。</summary>
internal sealed class OpenCodeBillionOriginEdit(OpenCodeBillionContext context, string candidate)
{
    public OpenCodeBillionContext Context { get; } = context;

    /// <summary>原始输入（可能无效或含不应保留的内容）：只交给控制器校验，不得记录或回显到其他位置。</summary>
    public string Candidate { get; } = candidate;

    public bool Accepted { get; set; }
}

/// <summary>关联确认事件：Context 为界面显示时的渲染上下文。</summary>
internal sealed record OpenCodeBillionConfirmRequest(OpenCodeBillionContext Context, bool Confirmed);
