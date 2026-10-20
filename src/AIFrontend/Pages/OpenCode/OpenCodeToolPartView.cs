using System;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// 单个工具部件（assistant.content 中 type=tool 的元素）的紧凑工具行（重设计 §6.1）。
/// </summary>
/// <remarks>
/// <para>
/// 普通 sealed 业务类（不是控件子类）：构造时创建同一实例的根元素，经 <see cref="Element"/> 暴露，
/// 节点身份跨轮询保持。行本体是原生 <see cref="Button"/>（内置悬停/按下/焦点视觉与 Enter/Space 键盘行为），
/// 高 32：状态图标 16 | 工具名（BodyStrong）| 目标（等宽、省略号）| 右侧摘要（Caption 次要色）。
/// 完成 ✓ 成功色；出错 ✕ 危险色且行底为危险背景；运行中 / 输入生成中 = 16px ProgressRing；
/// 状态严格来自 <see cref="OpenCodeToolStates.Read"/> 的四个已核实取值，无法识别时如实显示“状态未识别”。
/// </para>
/// <para>
/// 完整内容（read 全文带行号、edit/patch/write 并排 diff、shell 命令与输出、其他工具 输入/输出/错误）
/// 在 <see cref="OpenCodeToolDetailPopup"/> 中显示：悬停片刻（延迟见 OpenCodeToolDetailPopup.HoverOpenDelayMs）预览（不移动焦点），点击 / Enter / Space 固定，Esc 关闭。
/// 上 / 下方向键在工具行之间移动焦点。
/// </para>
/// <para>
/// subagent 工具（<c>name == "subagent"</c>，F7）：行内不再显示摘要卡片与按钮；悬停详情显示子会话预览
/// （智能体 / 任务 / 模型 / 方式 / 记录状态 / 子会话 id + 工具输入与结果，均来自已有部件数据，不发起请求）。
/// 点击经 <see cref="SubagentSessionRequested"/> 向页面请求只读子会话小窗；小窗最大化才走校验导航路径。
/// 行本身不授权、不发起请求。
/// 增量移除 / 替换时由 <see cref="Dispose"/> 退订、关闭弹出层并停用回调，不产生 stale callbacks。
/// </para>
/// </remarks>
public sealed class OpenCodeToolPartView : IOpenCodeToolDetailSource
{
    private readonly Grid _root;
    private readonly Border _selectionBar;
    private readonly Border _rowChrome;
    private readonly Button _row;
    private readonly FontIcon _statusIcon;
    private readonly ProgressRing _progress;
    private readonly FontIcon _kindIcon;
    private readonly TextBlock _nameText;
    private readonly TextBlock _targetText;
    private readonly TextBlock _summaryText;
    private readonly TextBlock _additionsText;
    private readonly TextBlock _deletionsText;

    private OpenCodeToolRowModel _model = new();

    /// <summary>详情弹出层使用的模型（subagent 为子会话预览；其余同 <see cref="_model"/>）。</summary>
    private OpenCodeToolRowModel _detailModel = new();

    /// <summary>通过不透明校验的子会话 id（仅 subagent 工具；否则为 null）。</summary>
    private string? _childSessionId;

    private bool _pinned;

    /// <summary>subagent 工具的详情来源（子会话预览 + 「打开子会话」链接）；非 subagent 为 null。</summary>
    private SubagentPreviewSource? _subagentSource;

    /// <summary>最近一次 Update 传入的父消息 id（仅随导航事件透传；不做校验/授权）。</summary>
    private string? _parentMessageId;

    private bool _disposed;
    private FrameworkElement? _detailAnchorOverride;
    internal void SetDetailAnchor(FrameworkElement? anchor) => _detailAnchorOverride = anchor;

    /// <summary>承载 UI 的根元素（构造时创建，同一实例；本类不是控件子类）。</summary>
    public FrameworkElement Element => _root;

    /// <summary>
    /// 用户点击 subagent 工具行（或其悬停预览）时请求只读子会话小窗。
    /// 只含父消息 id 与子会话 id 的导航提示；不授权、不直接发起任何查询或 HTTP。
    /// <see cref="OpenCodeConversationView"/> 将其转发为自己的同名公开事件。
    /// </summary>
    public event EventHandler<OpenCodeSubagentSessionRequestedEventArgs>? SubagentSessionRequested;

    /// <summary>创建空的工具行；通过 <see cref="Update"/> 填充内容。</summary>
    public OpenCodeToolPartView()
    {
        _statusIcon = new FontIcon { FontSize = 14, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _progress = new ProgressRing
        {
            Width = 16,
            Height = 16,
            MinWidth = 16,
            MinHeight = 16,
            IsActive = false,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var statusCell = new Grid { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        statusCell.Children.Add(_statusIcon);
        statusCell.Children.Add(_progress);

        _kindIcon = new FontIcon { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        OpenCodeToolTheme.Apply(_kindIcon, OpenCodeToolTheme.Icon(OpenCodeToolTheme.TextSecondary));

        _nameText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.BodyStrongStyleKey);
        _nameText.VerticalAlignment = VerticalAlignment.Center;
        _nameText.TextWrapping = TextWrapping.NoWrap;

        _targetText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CodeStyleKey);
        _targetText.IsTextSelectionEnabled = false;
        _targetText.VerticalAlignment = VerticalAlignment.Center;
        _targetText.TextWrapping = TextWrapping.NoWrap;
        _targetText.TextTrimming = TextTrimming.CharacterEllipsis;

        _summaryText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionSubtleStyleKey);
        _summaryText.VerticalAlignment = VerticalAlignment.Center;
        _summaryText.TextWrapping = TextWrapping.NoWrap;
        _additionsText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionStyleKey);
        OpenCodeToolTheme.Apply(_additionsText, OpenCodeToolTheme.Text(OpenCodeToolTheme.Success, OpenCodeInteractionUi.CaptionStyleKey));
        _additionsText.VerticalAlignment = VerticalAlignment.Center;
        _deletionsText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionStyleKey);
        OpenCodeToolTheme.Apply(_deletionsText, OpenCodeToolTheme.Text(OpenCodeToolTheme.Critical, OpenCodeInteractionUi.CaptionStyleKey));
        _deletionsText.VerticalAlignment = VerticalAlignment.Center;
        var summaryPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        summaryPanel.Children.Add(_additionsText);
        summaryPanel.Children.Add(_deletionsText);
        summaryPanel.Children.Add(_summaryText);

        var content = new Grid { ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(statusCell, 0);
        Grid.SetColumn(_kindIcon, 1);
        Grid.SetColumn(_nameText, 2);
        Grid.SetColumn(_targetText, 3);
        Grid.SetColumn(summaryPanel, 4);
        content.Children.Add(statusCell);
        content.Children.Add(_kindIcon);
        content.Children.Add(_nameText);
        content.Children.Add(_targetText);
        content.Children.Add(summaryPanel);

        _row = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Height = 32,
            Padding = new Thickness(8, 0, 8, 0),
            BorderThickness = new Thickness(0),
            Style = OpenCodeInteractionUi.TryStyle("OpenCodeTransparentToolButtonStyle"),
        };
        _row.Click += (_, _) => TogglePinnedDetail();
        _row.PointerEntered += (_, _) => HoverEnterDetail();
        _row.PointerExited += (_, _) => HoverExitDetail();
        _row.KeyDown += OnRowKeyDown;
        _row.Unloaded += (_, _) => CloseDetailIfShowing();

        // 出错行底：危险背景（主题画刷样式）；正常时透明。
        _rowChrome = new Border { Child = _row, CornerRadius = new CornerRadius(4) };

        // 选中（详情已固定）时左侧 3px 强调色条。
        _selectionBar = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(1.5),
            Margin = new Thickness(0, 6, 0, 6),
            Visibility = Visibility.Collapsed,
        };
        OpenCodeToolTheme.Apply(_selectionBar, OpenCodeToolTheme.Border(OpenCodeToolTheme.Accent));
        _selectionBar.VerticalAlignment = VerticalAlignment.Stretch;

        _root = new Grid { ColumnSpacing = 2 };
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_selectionBar, 0);
        Grid.SetColumn(_rowChrome, 1);
        _root.Children.Add(_selectionBar);
        _root.Children.Add(_rowChrome);
    }

    /// <summary>当前行模型（供分组摘要与详情弹出层使用）。</summary>
    internal OpenCodeToolRowModel Model => _model;

    OpenCodeToolRowModel IOpenCodeToolDetailSource.DetailModel => _detailModel;

    FrameworkElement IOpenCodeToolDetailSource.DetailAnchor => _detailAnchorOverride ?? _row;

    void IOpenCodeToolDetailSource.OnDetailStateChanged(bool shown, bool pinned)
    {
        _pinned = shown && pinned;
        _selectionBar.Visibility = _pinned ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 就地更新展示内容（详情弹出层若正为本行显示，则同步刷新并保持滚动位置）。
    /// 传入的部件应已按 id 匹配到同一工具调用。
    /// </summary>
    /// <param name="part">assistant 消息中 type=tool 的内容部件。</param>
    /// <param name="parentMessageId">承载该部件的 assistant 消息 id（可为 null；仅随导航事件透传，不做校验/授权）。</param>
    /// <param name="asyncCompletion">与该调用关联的异步完成通知（后台终端 / 子任务）；无则 null（见 <see cref="OpenCodeToolRowPresentation.Build"/>）。</param>
    internal void Update(OpenCodeContentPart part, string? parentMessageId, OpenCodeAsyncCompletion? asyncCompletion)
    {
        ArgumentNullException.ThrowIfNull(part);

        _parentMessageId = parentMessageId;
        _model = OpenCodeToolRowPresentation.Build(part, asyncCompletion);
        ApplyModel(_model);

        // subagent 严格按已核准名称识别；尚未提供 child id 时，小窗如实提示而不显示属性卡冒充对话。
        OpenCodeSubagentToolInfo? subagent = null;
        try
        {
            if (OpenCodeSubagentPresentation.TryReadToolPart(part, out var info))
            {
                subagent = info;
            }
        }
        catch (OpenCodeProtocolException)
        {
            subagent = null;
        }

        _childSessionId = subagent?.ChildSessionId is { } childId && OpenCodeSubagentPresentation.IsOpaqueSessionId(childId)
            ? childId
            : null;
        _detailModel = subagent is null ? _model : BuildSubagentPreview(_model, subagent);
        var wasSubagent = _subagentSource is not null;
        if (subagent is not null)
        {
            _subagentSource ??= new SubagentPreviewSource(this);
        }
        else if (wasSubagent)
        {
            OpenCodeToolDetailPopup.Shared.CloseIf(_subagentSource!);
            _subagentSource = null;
        }

        if (!_disposed)
        {
            if (_subagentSource is { } preview)
            {
                OpenCodeToolDetailPopup.Shared.Refresh(preview);
            }
            else
            {
                OpenCodeToolDetailPopup.Shared.Refresh(this);
            }
        }
    }

    /// <summary>就地更新展示内容（无异步完成通知）。</summary>
    public void Update(OpenCodeContentPart part, string? parentMessageId = null) => Update(part, parentMessageId, null);

    /// <summary>
    /// 停用该视图：关闭本行的详情弹出层、退订子任务摘要回调并清空 <see cref="SubagentSessionRequested"/> 全部订阅者。
    /// 由 <see cref="OpenCodeConversationView"/> 在增量移除 / 替换该部件时调用（无 stale callbacks）。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseDetailIfShowing();
        _childSessionId = null;
        SubagentSessionRequested = null;
    }

    /// <summary>
    /// subagent 工具的悬停预览模型：在原工具模型之上，以“子会话”键值列表开头（智能体 / 任务 / 模型 / 方式 / 记录状态 / 子会话 id），
    /// 随后是原始输入中的其余字段；输出（子任务结果）保持不变。只使用部件中已有的数据，不发起任何请求。
    /// </summary>
    internal static OpenCodeToolRowModel BuildSubagentPreview(OpenCodeToolRowModel model, OpenCodeSubagentToolInfo info)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(info);

        var background = info.Background == true;
        var properties = new System.Collections.Generic.List<OpenCodeToolProperty>();
        void Add(string key, string? value, OpenCodeToolPropertyKind kind = OpenCodeToolPropertyKind.Text)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                properties.Add(new OpenCodeToolProperty(0, key, value!, kind));
            }
        }

        Add("智能体", info.Agent);
        Add("任务", info.Description);
        Add("模型", info.Model, OpenCodeToolPropertyKind.Code);
        if (info.Background.HasValue)
        {
            Add("方式", background ? "后台运行" : "前台运行");
        }

        Add("状态", OpenCodeSubagentPresentation.DescribeRecordedStatus(info.RecordedStatus, background));
        Add("子会话", info.ChildSessionId is { } child && OpenCodeSubagentPresentation.IsOpaqueSessionId(child) ? child : "（尚未创建或未提供）", OpenCodeToolPropertyKind.Code);

        // 原始输入中已在预览里呈现的顶层字段不重复显示（其子项一并跳过）。
        var skipDepth = -1;
        foreach (var property in model.InputProperties)
        {
            if (skipDepth >= 0)
            {
                if (property.Depth > skipDepth)
                {
                    continue;
                }

                skipDepth = -1;
            }

            if (property.Depth == 0 && property.Key is "agent" or "subagent_type" or "description" or "model" or "background")
            {
                skipDepth = 0;
                continue;
            }

            properties.Add(property);
        }

        if (model.InputProperties.Count == 0 && !string.IsNullOrWhiteSpace(model.InputText))
        {
            properties.Add(new OpenCodeToolProperty(0, "输入", model.InputText!, OpenCodeToolPropertyKind.Block));
        }

        return new OpenCodeToolRowModel
        {
            PartId = model.PartId,
            Name = string.IsNullOrWhiteSpace(info.Agent) ? model.Name : "子任务 · " + info.Agent,
            Kind = model.Kind,
            Status = model.Status,
            StatusText = model.StatusText,
            Target = model.Target,
            Summary = model.Summary,
            Additions = model.Additions,
            Deletions = model.Deletions,
            ExitCode = model.ExitCode,
            Truncated = model.Truncated,
            DurationText = model.DurationText,
            InputText = null,
            OutputText = model.OutputText,
            FileOutputs = model.FileOutputs,
            ErrorText = model.ErrorText,
            DiffFiles = model.DiffFiles,
            Command = model.Command,
            Url = model.Url,
            RawState = model.RawState,
            InputProperties = properties,
            OutputProperties = model.OutputProperties,
            RawStateProperties = model.RawStateProperties,
            AccessibleName = model.AccessibleName,
        };
    }

    /// <summary>工具类别的内置字形（Segoe Fluent Icons）。</summary>
    internal static string KindGlyph(OpenCodeToolKind kind) => kind switch
    {
        OpenCodeToolKind.Read => "\uE8A5",
        OpenCodeToolKind.Edit or OpenCodeToolKind.MultiEdit => "\uE70F",
        OpenCodeToolKind.Patch => "\uE8C8",
        OpenCodeToolKind.Write => "\uE8E5",
        OpenCodeToolKind.Shell => "\uE756",
        OpenCodeToolKind.Grep or OpenCodeToolKind.WebSearch => "\uE721",
        OpenCodeToolKind.Glob or OpenCodeToolKind.List => "\uE8B7",
        OpenCodeToolKind.WebFetch => "\uE774",
        OpenCodeToolKind.Subagent => OpenCodeStepRunPresentation.SubagentGlyph,
        OpenCodeToolKind.Question => "\uE897",
        OpenCodeToolKind.Todo => "\uE9D5",
        _ => "\uE90F",
    };

    private void ApplyModel(OpenCodeToolRowModel model)
    {
        _kindIcon.Glyph = KindGlyph(model.Kind);
        _nameText.Text = model.Name;
        _targetText.Text = model.Target;
        _targetText.Visibility = model.Target.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(_row, model.Target.Length == 0 ? model.Name : model.Name + "  " + model.Target);

        if (model.Additions is { } add && model.Deletions is { } del)
        {
            _additionsText.Text = "+" + add.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _deletionsText.Text = "−" + del.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _additionsText.Visibility = Visibility.Visible;
            _deletionsText.Visibility = Visibility.Visible;
            _summaryText.Text = model.DurationText;
        }
        else
        {
            _additionsText.Visibility = Visibility.Collapsed;
            _deletionsText.Visibility = Visibility.Collapsed;
            _summaryText.Text = model.Summary.Length > 0
                ? model.Summary + (model.DurationText.Length > 0 ? " · " + model.DurationText : string.Empty)
                : model.Status == OpenCodeToolRowStatus.Completed ? model.DurationText : model.StatusText;
        }

        // 非零退出码用危险色突出；其余摘要为次要色。
        var summaryStyle = model.Kind == OpenCodeToolKind.Shell && model.ExitCode is { } exit && exit != 0
            ? OpenCodeToolTheme.Text(OpenCodeToolTheme.Critical, OpenCodeInteractionUi.CaptionStyleKey)
            : OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CaptionSubtleStyleKey);
        OpenCodeToolTheme.Apply(_summaryText, summaryStyle);

        var active = model.IsActive;
        _progress.IsActive = active;
        _progress.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        _statusIcon.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        switch (model.Status)
        {
            case OpenCodeToolRowStatus.Completed:
                _statusIcon.Glyph = "\uE73E";
                OpenCodeToolTheme.Apply(_statusIcon, OpenCodeToolTheme.Icon(OpenCodeToolTheme.Success));
                break;
            case OpenCodeToolRowStatus.Error:
                _statusIcon.Glyph = "\uE711";
                OpenCodeToolTheme.Apply(_statusIcon, OpenCodeToolTheme.Icon(OpenCodeToolTheme.Critical));
                break;
            case OpenCodeToolRowStatus.Unknown:
                _statusIcon.Glyph = "\uE9CE";
                OpenCodeToolTheme.Apply(_statusIcon, OpenCodeToolTheme.Icon(OpenCodeToolTheme.TextSecondary));
                break;
        }

        if (model.Status == OpenCodeToolRowStatus.Error)
        {
            OpenCodeToolTheme.Apply(_rowChrome, OpenCodeToolTheme.Border(OpenCodeToolTheme.CriticalBackground));
        }
        else
        {
            _rowChrome.Style = null;
            _rowChrome.Background = null;
        }

        var id = string.IsNullOrEmpty(model.PartId) ? string.Empty : model.PartId;
        AutomationProperties.SetAutomationId(_row, "OpenCodeToolRow." + id);
        AutomationProperties.SetName(_row, model.AccessibleName);
        AutomationProperties.SetHelpText(_row, "按 Enter 查看完整内容，Esc 关闭");
    }

    private void OnRowKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                if (OpenCodeToolDetailPopup.Shared.IsShowing(this)
                    || (_subagentSource is { } preview && OpenCodeToolDetailPopup.Shared.IsShowing(preview)))
                {
                    OpenCodeToolDetailPopup.Shared.Close();
                    e.Handled = true;
                }

                break;
            case VirtualKey.Up:
            case VirtualKey.Down:
                e.Handled = TryMoveFocus(e.Key == VirtualKey.Up ? FocusNavigationDirection.Up : FocusNavigationDirection.Down);
                break;
        }
    }

    private bool TryMoveFocus(FocusNavigationDirection direction)
    {
        try
        {
            if (_row.XamlRoot?.Content is { } searchRoot)
            {
                return FocusManager.TryMoveFocus(direction, new FindNextElementOptions { SearchRoot = searchRoot });
            }
        }
        catch (Exception)
        {
        }

        return false;
    }

    private void RaiseSubagentSessionRequested(string childSessionId)
    {
        if (_disposed)
        {
            return;
        }

        SubagentSessionRequested?.Invoke(
            this,
            new OpenCodeSubagentSessionRequestedEventArgs(_parentMessageId, childSessionId));
    }

    internal void HoverEnterDetail()
    {
        if (_subagentSource is { } preview)
        {
            OpenCodeToolDetailPopup.Shared.HoverEnter(preview);
        }
        else
        {
            OpenCodeToolDetailPopup.Shared.HoverEnter(this);
        }
    }

    internal void HoverExitDetail()
    {
        if (_subagentSource is { } preview)
        {
            OpenCodeToolDetailPopup.Shared.HoverExit(preview);
        }
        else
        {
            OpenCodeToolDetailPopup.Shared.HoverExit(this);
        }
    }

    internal void TogglePinnedDetail()
    {
        if (_subagentSource is { } preview)
        {
            OpenCodeToolDetailPopup.Shared.TogglePinned(preview);
        }
        else
        {
            OpenCodeToolDetailPopup.Shared.TogglePinned(this);
        }
    }

    private void CloseDetailIfShowing()
    {
        OpenCodeToolDetailPopup.Shared.CloseIf(this);
        if (_subagentSource is { } preview)
        {
            OpenCodeToolDetailPopup.Shared.CloseIf(preview);
        }
    }

    /// <summary>
    /// subagent 悬停仍显示调用详情；点击独立请求真实子会话只读窗口，不复用大属性详情。
    /// </summary>
    private sealed class SubagentPreviewSource : IOpenCodeDetailContentSource, IOpenCodeChildChatSource
    {
        private readonly OpenCodeToolPartView _owner;
        private readonly OpenCodeToolDetailOptions _options = new();

        internal SubagentPreviewSource(OpenCodeToolPartView owner) => _owner = owner;

        public void OpenChildChat()
        {
            if (!_owner._disposed)
                _owner.RaiseSubagentSessionRequested(_owner._childSessionId ?? string.Empty);
        }

        public FrameworkElement DetailAnchor => _owner._detailAnchorOverride ?? _owner._row;

        public OpenCodeDetailHeader DetailHeader
        {
            get
            {
                var model = _owner._detailModel;
                var brush = model.Status switch
                {
                    OpenCodeToolRowStatus.Completed => OpenCodeToolTheme.Success,
                    OpenCodeToolRowStatus.Error => OpenCodeToolTheme.Critical,
                    _ => null,
                };
                return new OpenCodeDetailHeader(
                    KindGlyph(OpenCodeToolKind.Subagent),
                    model.Name,
                    model.Target,
                    model.StatusText,
                    brush,
                    model.DurationText,
                    model.AccessibleName);
            }
        }

        public OpenCodeToolDetailView BuildDetailContent()
        {
            var inner = OpenCodeToolDetailContent.Build(
                _owner._detailModel,
                _options,
                () => OpenCodeToolDetailPopup.Shared.Refresh(this));
            return inner;
        }

        public void OnDetailStateChanged(bool shown, bool pinned)
            => ((IOpenCodeToolDetailSource)_owner).OnDetailStateChanged(shown, pinned);
    }
}
