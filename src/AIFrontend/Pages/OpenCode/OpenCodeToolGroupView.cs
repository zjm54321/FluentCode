using System;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AIFrontend.Pages.OpenCode;

// Retained chain state must not allocate a header, timer or items panel offscreen.
internal sealed class OpenCodeToolGroupView
{
    private readonly Action<string, bool> _toggled;
    private OpenCodeToolGroupVisual? _visual;
    private OpenCodeStepRunSummary? _summary;
    private bool _expanded, _headerVisible = true, _inline, _visible = true;
    private Action? _open, _enter, _exit, _background;
    private string? _name, _glyph, _model;
    private OpenCodeToolRowModel? _runningTool;
    private OpenCodeToolRowStatus? _status;
    private string _step = "处理中…";
    private bool _reasoning, _toolRunning, _disposed;

    internal OpenCodeToolGroupView(string groupId, Action<string, bool> onUserToggled)
    { GroupId = groupId; _toggled = onUserToggled; }
    internal string GroupId { get; }
    internal bool IsExpanded => _expanded;
    internal bool ShowsItems => _inline || _open is not null || IsExpanded;
    internal FrameworkElement DetailAnchor => Visual.DetailAnchor;
    internal FrameworkElement Element => Visual.Element;
    internal Panel ItemsHost => Visual.ItemsHost;

    private OpenCodeToolGroupVisual Visual
    {
        get
        {
            if (_disposed) throw new ObjectDisposedException(nameof(OpenCodeToolGroupView));
            if (_visual is not null) return _visual;
            _visual = new OpenCodeToolGroupVisual(GroupId, (id, expanded) => { _expanded = expanded; _toggled(id, expanded); });
            _visual.SetCurrentStep(_step);
            _visual.SetWorkingContext(_reasoning, _toolRunning, _model, _runningTool);
            _visual.BindSingleLeaf(_open, _enter, _exit, _name, _glyph, _status);
            _visual.SetBackgroundAction(_background);
            _visual.SetInlineLeaf(_inline);
            if (_summary is not null) _visual.Update(_summary, _expanded, _headerVisible);
            _visual.Element.Visibility = _visible ? Visibility.Visible : Visibility.Collapsed;
            return _visual;
        }
    }

    internal void SetVisibility(bool visible)
    { _visible = visible; if (_visual is not null) _visual.Element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed; }
    internal void SetCurrentStep(string step) { _step = step; _visual?.SetCurrentStep(step); }
    internal void SetWorkingContext(bool reasoning, bool toolRunning, string? model, OpenCodeToolRowModel? runningTool = null)
    { _reasoning = reasoning; _toolRunning = toolRunning; _model = model; _runningTool = runningTool; _visual?.SetWorkingContext(reasoning, toolRunning, model, runningTool); }
    internal void BindSingleLeaf(Action? open, Action? enter, Action? exit, string? name = null, string? glyph = null, OpenCodeToolRowStatus? status = null)
    { _open = open; _enter = enter; _exit = exit; _name = name; _glyph = glyph; _status = status; _visual?.BindSingleLeaf(open, enter, exit, name, glyph, status); }
    internal void SetBackgroundAction(Action? action) { _background = action; _visual?.SetBackgroundAction(action); }
    internal void SetInlineLeaf(bool inline) { _inline = inline; _visual?.SetInlineLeaf(inline); }
    internal void Update(OpenCodeStepRunSummary summary, bool expanded, bool headerVisible)
    { _summary = summary; _expanded = expanded; _headerVisible = headerVisible; _visual?.Update(summary, _expanded, headerVisible); }
    internal void Dispose()
    { _disposed = true; _visual?.Dispose(); _visual = null; _open = _enter = _exit = _background = null; }
}

/// <summary>
/// W1/W2: a native Expander owns the complete processing group. B18/WG1 manual choices override
/// the timeline's running/terminal defaults. A single operation is its real row,
/// not a synthetic "已处理" header. Followers retain their items without duplicating the chain header.
/// </summary>
internal sealed class OpenCodeToolGroupVisual
{
    private readonly string _groupId;
    private readonly Action<string, bool> _toggled;
    private readonly StackPanel _root = new();
    private readonly Expander _expander;
    private readonly Border _outline;
    private readonly Border _headerSurface;
    private readonly Border _itemsBorder;
    private readonly StackPanel _items = new();
    private readonly FontIcon _icon;
    private readonly FontIcon _chevron;
    private readonly TextBlock _headerText;
    private readonly TextBlock _currentStepText;
    private readonly StackPanel _successTally, _errorTally;
    private readonly TextBlock _successText, _errorText;
    private readonly OpenCodeWorkingIndicator _working;
    private readonly OpenCodeWorkingPhrasePicker _phrases = new();
    private OpenCodeStepRunSummary _summary = new(string.Empty, string.Empty, 0, 0, false, null, false, string.Empty);
    private OpenCodeWorkingActivity _activity;
    private OpenCodeWorkingFamily _family;
    private DispatcherQueueTimer? _timer;
    private bool _single, _inline, _headerVisible = true, _expanded, _updating, _loaded, _disposed;
    private bool? _groupContainer;
    private bool _pointerOver, _headerPointerOver, _focusVisible, _headerFocusVisible;

    // Expanded children: a 1px guide under the header icon's centre (x = 24.5 ≈ 5 + 8 + 12), and the
    // children's icon column starts exactly where the header text starts (x = 45 = 32 + 13).
    private static readonly Thickness ChildGuideMargin = new(OpenCodeTimelineRowMetrics.LeadInset + OpenCodeTimelineRowMetrics.Padding + OpenCodeTimelineRowMetrics.IconColumn / 2 - 1, 0, 0, 0);
    private static readonly Thickness ChildGuidePadding = new(OpenCodeTimelineRowMetrics.ChildIndent - ChildGuideMargin.Left - 1, 0, 0, 0);

    internal OpenCodeToolGroupVisual(string groupId, Action<string, bool> onUserToggled)
    {
        _groupId = groupId;
        _toggled = onUserToggled;
        _icon = new FontIcon { Glyph = "\uE9F5", FontSize = 14, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _headerText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionStyleKey);
        _headerText.FontSize = OpenCodeTimelineRowMetrics.FontSize(null);
        _headerText.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
        _headerText.VerticalAlignment = VerticalAlignment.Center;
        _headerText.TextTrimming = TextTrimming.CharacterEllipsis;
        _working = new OpenCodeWorkingIndicator(groupId);
        _working.VisibilityChanged += UpdateTimer;
        OpenCodeWorkingClock.Shared.HostVisibilityChanged += HostVisibilityChanged;
        _chevron = new FontIcon
        {
            Glyph = "\uE76C", FontSize = 10, Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Opacity = 0, IsHitTestVisible = false,
        };
        OpenCodeToolTheme.Apply(_chevron, OpenCodeToolTheme.Icon(OpenCodeToolTheme.TextSecondary));
        AutomationProperties.SetAccessibilityView(_chevron, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        // One transcript grid (OpenCodeTimelineRowMetrics): same 24px icon column, 8px gap, 13px text
        // and 30px height as tool / reasoning / completion rows, so every secondary row shares one left edge.
        var iconCell = new Grid { Width = OpenCodeTimelineRowMetrics.IconColumn, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        iconCell.Children.Add(_icon);
        iconCell.Children.Add(_working.Gear);
        var title = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(_headerText);
        title.Children.Add(_working.Label);
        Grid.SetColumn(_chevron, 1);
        title.Children.Add(_chevron);
        _currentStepText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionStyleKey);
        _currentStepText.FontSize = 12;
        _currentStepText.TextTrimming = TextTrimming.CharacterEllipsis;
        _currentStepText.Visibility = Visibility.Collapsed;
        OpenCodeToolTheme.Apply(_currentStepText, OpenCodeToolTheme.Text(OpenCodeToolTheme.TextSecondary, OpenCodeInteractionUi.CaptionStyleKey));
        AutomationProperties.SetAutomationId(_currentStepText, "OpenCodeToolGroup.CurrentStep." + groupId);
        var titleStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(title);
        titleStack.Children.Add(_currentStepText);
        (_successTally, _successText) = CreateTally("\uE73E", OpenCodeToolTheme.Success, "OpenCodeToolGroup.Success." + groupId);
        (_errorTally, _errorText) = CreateTally("\uE711", OpenCodeToolTheme.Critical, "OpenCodeToolGroup.Errors." + groupId);
        var tally = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        tally.Children.Add(_successTally);
        tally.Children.Add(_errorTally);
        var header = new Grid { ColumnSpacing = OpenCodeTimelineRowMetrics.ColumnGap, MinHeight = OpenCodeTimelineRowMetrics.Height(null), HorizontalAlignment = HorizontalAlignment.Stretch };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(OpenCodeTimelineRowMetrics.IconColumn) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(iconCell);
        Grid.SetColumn(titleStack, 1);
        header.Children.Add(titleStack);
        Grid.SetColumn(tally, 2);
        header.Children.Add(tally);
        // PG7: a settled group highlights only this header, never the Expander/content container.
        _headerSurface = new Border
        {
            // Same 4px radius, 5px lead (rows' 3px selection bar + 2px gap) and 8px padding as a row button:
            // the hover surface and the icon/text columns coincide with the rows below, no extra card inset.
            Child = header, CornerRadius = new CornerRadius(4),
            Margin = new Thickness(OpenCodeTimelineRowMetrics.LeadInset, 0, 0, 0),
            Padding = new Thickness(OpenCodeTimelineRowMetrics.Padding, 0, OpenCodeTimelineRowMetrics.Padding, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        OpenCodeToolTheme.Apply(_headerSurface, OpenCodeToolTheme.Border("SubtleFillColorTransparentBrush"));
        _itemsBorder = new Border
        {
            Child = _items,
            Margin = ChildGuideMargin,
            Padding = ChildGuidePadding,
            BorderThickness = new Thickness(1, 0, 0, 0),
        };
        OpenCodeToolTheme.Apply(_itemsBorder, OpenCodeToolTheme.Border("SubtleFillColorTransparentBrush", OpenCodeToolTheme.Divider));
        _expander = new Expander
        {
            Header = _headerSurface,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
            MinHeight = OpenCodeTimelineRowMetrics.Height(null),
            MinWidth = 0,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            // Native template filters this into top-only header / bottom-only content corners.
            CornerRadius = new CornerRadius(8),
        };
        ApplyGroupChrome();
        _outline = new Border { Child = _expander, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8) };
        OpenCodeToolTheme.Apply(_outline, OpenCodeToolTheme.Border("SubtleFillColorTransparentBrush"));
        AutomationProperties.SetAutomationId(_expander, "OpenCodeToolGroup." + groupId);
        _expander.Loaded += (_, _) => NormalizeTemplateInsets();
        // Fluent hover: fills cross-fade in 83ms; the chevron fades in 83ms and turns 0→90° in 167ms (Point-to-point
        // feel via the system scalar transition). Composition-driven; the native Expander keeps its own expand animation.
        AIFrontend.Motion.FluentMotion.Bind(_root, enabled =>
        {
            _outline.BackgroundTransition = enabled ? AIFrontend.Motion.FluentMotion.HoverFill() : null;
            _headerSurface.BackgroundTransition = enabled ? AIFrontend.Motion.FluentMotion.HoverFill() : null;
            _chevron.OpacityTransition = enabled ? new ScalarTransition { Duration = AIFrontend.Motion.FluentMotion.Duration(AIFrontend.Motion.FluentMotion.Faster) } : null;
            _chevron.RotationTransition = enabled ? new ScalarTransition { Duration = AIFrontend.Motion.FluentMotion.Duration(AIFrontend.Motion.FluentMotion.Fast) } : null;
        });
        _chevron.SizeChanged += (_, args) => _chevron.CenterPoint = new System.Numerics.Vector3((float)args.NewSize.Width / 2, (float)args.NewSize.Height / 2, 0);
        _expander.Expanding += (_, _) => ExpansionChanged(true);
        _expander.Collapsed += (_, _) => ExpansionChanged(false);
        _root.PointerEntered += (_, _) => { _pointerOver = true; UpdateChevronAffordance(); };
        _root.PointerExited += (_, _) => { _pointerOver = false; UpdateChevronAffordance(); };
        _root.PointerCanceled += (_, _) => { _pointerOver = false; UpdateChevronAffordance(); };
        _headerSurface.PointerEntered += (_, _) => { _headerPointerOver = true; UpdateChevronAffordance(); };
        _headerSurface.PointerExited += (_, _) => { _headerPointerOver = false; UpdateChevronAffordance(); };
        _headerSurface.PointerCanceled += (_, _) => { _headerPointerOver = false; UpdateChevronAffordance(); };
        _expander.GotFocus += (_, e) =>
        {
            _focusVisible = e.OriginalSource is Control { FocusState: FocusState.Keyboard };
            _headerFocusVisible = _focusVisible && !IsItemsSource(e.OriginalSource as DependencyObject);
            UpdateChevronAffordance();
        };
        _expander.LostFocus += (_, _) => { _focusVisible = _headerFocusVisible = false; UpdateChevronAffordance(); };
        _root.Loaded += (_, _) => { _loaded = true; RefreshWorking(); };
        // Reparenting can deliver an old Unloaded after the next Loaded; trust the current tree state.
        _root.Unloaded += (_, _) => { if (_root.IsLoaded) return; _loaded = false; StopTimer(); };
        ConfigureContainer();
    }

    private void ApplyGroupChrome()
    {
        // PG1: keep the native Expander's input, focus, automation and transitions, not its card chrome.
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (var key in new[]
        {
            "ExpanderHeaderBackground", "ExpanderHeaderBackgroundPointerOver", "ExpanderHeaderBackgroundPressed",
            "ExpanderContentBackground", "ExpanderContentBorderBrush", "ExpanderHeaderBorderBrush",
            "ExpanderHeaderBorderPointerOverBrush", "ExpanderHeaderBorderPressedBrush", "ExpanderHeaderDisabledBorderBrush",
            "ExpanderChevronBackground", "ExpanderChevronPointerOverBackground", "ExpanderChevronPressedBackground",
            "ExpanderChevronBorderBrush", "ExpanderChevronBorderPointerOverBrush", "ExpanderChevronBorderPressedBrush",
            "ExpanderChevronForeground", "ExpanderChevronPointerOverForeground", "ExpanderChevronPressedForeground",
        }) _expander.Resources[key] = transparent;
        // PG4/PG7: our group/header surfaces own the fill. Native template parts always stay clear.
        foreach (var key in new[]
        {
            "ExpanderHeaderBorderThickness", "ExpanderContentDownBorderThickness", "ExpanderContentUpBorderThickness",
            "ExpanderChevronBorderThickness", "ExpanderChevronMargin", "ExpanderContentPadding",
        }) _expander.Resources[key] = new Thickness(0);
        // The stock template fixes its chevron to the far right. Remove that slot with lightweight
        // resources and use the old plain glyph beside the caption; the native header still toggles.
        _expander.Resources["ExpanderChevronButtonSize"] = 0d;
        _expander.Resources["ExpanderChevronGlyphSize"] = 0d;
        _expander.Resources["ExpanderHeaderPadding"] = new Thickness(0);
    }

    // The stock template reads ExpanderHeaderPadding (16,0,0,0) and ExpanderContentDownBorderThickness (1,0,1,1)
    // as StaticResource, so the lightweight overrides above cannot reach them: that 16px/1px inset is what pushed
    // "已处理" out of line with the rows. Zero the two template parts directly once they exist.
    private void NormalizeTemplateInsets()
    {
        var pending = new System.Collections.Generic.Stack<DependencyObject>();
        pending.Push(_expander);
        var found = 0;
        while (pending.Count > 0 && found < 2)
        {
            var current = pending.Pop();
            if (current is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Name: "ExpanderHeader" } toggle)
            { toggle.Padding = new Thickness(0); found++; continue; }
            if (current is Border { Name: "ExpanderContent" } content)
            { content.BorderThickness = new Thickness(0); content.Padding = new Thickness(0); found++; continue; }
            if (ReferenceEquals(current, _headerSurface) || ReferenceEquals(current, _itemsBorder)) continue;
            for (var i = VisualTreeHelper.GetChildrenCount(current) - 1; i >= 0; i--) pending.Push(VisualTreeHelper.GetChild(current, i));
        }
    }

    private bool IsItemsSource(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, _itemsBorder)) return true;
        return false;
    }

    // One right-pointing glyph turned 90° when expanded (instead of swapping glyphs) so the change can rotate.
    private void SetChevron(bool expanded)
    {
        _chevron.Glyph = "\uE76C";
        _chevron.Rotation = expanded ? 90f : 0f;
    }

    private void UpdateChevronAffordance()
    {
        var highlighted = _summary.IsActive ? _pointerOver || _focusVisible : _headerPointerOver || _headerFocusVisible;
        _chevron.Opacity = highlighted ? 1 : 0;
        // Brush ThemeResource handles Light/Dark/High Contrast; no outline in any state.
        OpenCodeToolTheme.Apply(_outline, OpenCodeToolTheme.Border(
            _summary.IsActive && highlighted ? "SubtleFillColorSecondaryBrush" : "SubtleFillColorTransparentBrush"));
        OpenCodeToolTheme.Apply(_headerSurface, OpenCodeToolTheme.Border(
            !_summary.IsActive && highlighted ? "SubtleFillColorSecondaryBrush" : "SubtleFillColorTransparentBrush"));
    }

    internal FrameworkElement Element => _root;
    // The timeline asks for this before BindSingleLeaf. Keep the anchor stable while the same
    // real row moves out of its former Expander; never return the soon-to-be-detached header.
    internal FrameworkElement DetailAnchor => _items;
    internal Panel ItemsHost => _items;
    internal bool IsExpanded => _expanded;
    internal void SetCurrentStep(string step)
    {
        _currentStepText.Text = step;
        ToolTipService.SetToolTip(_currentStepText, step);
        RefreshCurrentStep();
    }
    internal void SetBackgroundAction(Action? action) { } // The real operation's row owns its native action.
    internal void SetInlineLeaf(bool inline)
    { if (_inline == inline) return; _inline = inline; ConfigureContainer(); RefreshWorking(); }
    internal void BindSingleLeaf(Action? open, Action? enter, Action? exit, string? name = null, string? glyph = null, OpenCodeToolRowStatus? status = null)
    { var single = open is not null; if (_single == single) return; _single = single; ConfigureContainer(); RefreshWorking(); }

    internal void SetWorkingContext(bool reasoning, bool toolRunning, string? model, OpenCodeToolRowModel? runningTool = null)
    {
        _activity = WorkingActivity(reasoning, runningTool, toolRunning);
        _family = OpenCodeWorkingPhrases.Family(model);
        if (_working.IsActive && _phrases.UpdateContext(_activity, _family, NowMs())) RefreshWorkingText();
    }

    /// <summary>Only a live tool row (including a live background child) refines the working activity.</summary>
    internal static OpenCodeWorkingActivity WorkingActivity(bool reasoning, OpenCodeToolRowModel? runningTool, bool toolRunning = false)
    {
        if (reasoning) return OpenCodeWorkingActivity.Reasoning;
        if (runningTool is not { IsActive: true })
            return toolRunning ? OpenCodeWorkingActivity.Tool : OpenCodeWorkingActivity.General;
        var activity = OpenCodeWorkingPhrases.Activity(runningTool.ToolName,
            waitingForSubagent: runningTool.Status == OpenCodeToolRowStatus.Running || runningTool.IsBackground);
        if (activity != OpenCodeWorkingActivity.Tool) return activity;
        return runningTool.Kind switch
        {
            OpenCodeToolKind.Subagent => runningTool.Status == OpenCodeToolRowStatus.Streaming && !runningTool.IsBackground
                ? OpenCodeWorkingActivity.SubagentDispatch : OpenCodeWorkingActivity.SubagentWait,
            OpenCodeToolKind.Read or OpenCodeToolKind.List => OpenCodeWorkingActivity.Read,
            OpenCodeToolKind.Edit or OpenCodeToolKind.MultiEdit or OpenCodeToolKind.Patch or OpenCodeToolKind.Write => OpenCodeWorkingActivity.Edit,
            OpenCodeToolKind.Shell => OpenCodeWorkingActivity.Shell,
            OpenCodeToolKind.Grep or OpenCodeToolKind.Glob => OpenCodeWorkingActivity.Search,
            OpenCodeToolKind.WebFetch or OpenCodeToolKind.WebSearch or OpenCodeToolKind.Browser => OpenCodeWorkingActivity.Web,
            OpenCodeToolKind.Todo => OpenCodeWorkingActivity.Plan,
            _ => OpenCodeWorkingActivity.Tool,
        };
    }

    internal void Update(OpenCodeStepRunSummary summary, bool expanded, bool headerVisible)
    {
        ArgumentNullException.ThrowIfNull(summary);
        _summary = summary;
        _headerVisible = headerVisible;
        _expanded = expanded;
        SetChevron(_expanded);
        _headerText.Text = summary.HeaderText;
        var textKey = OpenCodeProcessedSummaryStyle.GroupHeaderTextKey(summary.IsActive, summary.ErrorCount, null);
        OpenCodeToolTheme.Apply(_headerText, textKey is null
            ? OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CaptionStyleKey)
            : OpenCodeToolTheme.Text(textKey, OpenCodeInteractionUi.CaptionStyleKey));
        OpenCodeToolTheme.Apply(_icon, OpenCodeToolTheme.Icon(OpenCodeProcessedSummaryStyle.GroupIconKey(summary.IsActive, summary.ErrorCount, null)));
        SetTally(_successTally, _successText, summary.SuccessCount, "个成功");
        SetTally(_errorTally, _errorText, summary.ErrorCount, "个出错");
        ConfigureContainer();
        _updating = true;
        _expander.IsExpanded = _expanded;
        _updating = false;
        RefreshWorking();
        UpdateChevronAffordance();
        UpdateAutomation();
    }

    private void ConfigureContainer()
    {
        var grouped = !_single && !_inline && _headerVisible;
        if (_groupContainer != grouped)
        {
            _expander.Content = null;
            _root.Children.Clear();
            if (grouped) { _expander.Content = _itemsBorder; _root.Children.Add(_outline); }
            else _root.Children.Add(_itemsBorder);
            _groupContainer = grouped;
        }
        var direct = _single || _inline;
        _itemsBorder.Margin = direct ? new Thickness(0) : ChildGuideMargin;
        _itemsBorder.Padding = direct ? new Thickness(0) : ChildGuidePadding;
        _itemsBorder.BorderThickness = direct ? new Thickness(0) : new Thickness(1, 0, 0, 0);
        _itemsBorder.Visibility = direct || IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        RefreshCurrentStep();
    }

    private void RefreshCurrentStep()
        => _currentStepText.Visibility = _summary.IsActive && !_expanded && _headerVisible && !_single && !_inline
            && !string.IsNullOrWhiteSpace(_currentStepText.Text) ? Visibility.Visible : Visibility.Collapsed;

    private void ExpansionChanged(bool expanded)
    {
        if (_updating || _disposed) return;
        _expanded = expanded;
        SetChevron(expanded);
        ConfigureContainer();
        UpdateAutomation();
        _toggled(_groupId, expanded);
    }

    private void UpdateAutomation()
    {
        AutomationProperties.SetName(_expander, _summary.AccessibleName + (IsExpanded ? "，已展开" : "，已折叠"));
        AutomationProperties.SetHelpText(_expander, "展开或收起处理过程");
    }

    private void RefreshWorking()
    {
        var active = !_disposed && _summary.IsActive && _headerVisible && !_single && !_inline;
        if (active && !_working.IsActive) _phrases.Start(_activity, _family, NowMs());
        else if (!active && _working.IsActive) _phrases.Stop();
        _working.SetActive(active);
        _icon.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        _headerText.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        RefreshWorkingText();
        UpdateTimer();
    }

    private static double NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private void RefreshWorkingText()
    {
        if (!_working.IsActive) return;
        var now = NowMs();
        var elapsed = OpenCodeStepRunPresentation.IsPlausibleTimestamp(_summary.StartedAt) && now >= _summary.StartedAt!.Value
            ? OpenCodeStepRunPresentation.FormatDuration(now - _summary.StartedAt.Value) : string.Empty;
        _working.SetText(_phrases.Current, elapsed);
    }

    private void UpdateTimer()
    {
        if (_disposed || !_loaded || !OpenCodeWorkingClock.Shared.HostVisible || !_working.IsActive || !_working.IsVisibleWhileLoaded) { StopTimer(); return; }
        if (_timer is null && DispatcherQueue.GetForCurrentThread() is { } queue)
        {
            _timer = queue.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.IsRepeating = true;
            _timer.Tick += (_, _) => { _phrases.Tick(NowMs()); RefreshWorkingText(); };
        }
        if (_timer is { IsRunning: false }) _timer.Start();
    }

    private void StopTimer() => _timer?.Stop();
    private void HostVisibilityChanged(bool visible)
    {
        UpdateTimer();
        if (visible) RefreshWorkingText();
    }
    internal void Dispose()
    { _disposed = true; OpenCodeWorkingClock.Shared.HostVisibilityChanged -= HostVisibilityChanged; StopTimer(); _phrases.Stop(); _working.Dispose(); }

    private static (StackPanel Panel, TextBlock Text) CreateTally(string glyph, string brushKey, string automationId)
    {
        var text = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionStyleKey);
        text.FontSize = OpenCodeTimelineRowMetrics.FontSize(null);
        text.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
        text.VerticalAlignment = VerticalAlignment.Center;
        OpenCodeToolTheme.Apply(text, OpenCodeToolTheme.Text(brushKey, OpenCodeInteractionUi.CaptionStyleKey));
        // Same 16px status cell / 14px glyph as a row's trailing ✓, so the tally's mark sits in the rows' status column.
        var icon = new FontIcon { Glyph = glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        OpenCodeToolTheme.Apply(icon, OpenCodeToolTheme.Icon(brushKey));
        var cell = new Grid { Width = OpenCodeTimelineRowMetrics.StatusColumn, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        cell.Children.Add(icon);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(text);
        panel.Children.Add(cell);
        AutomationProperties.SetAutomationId(panel, automationId);
        return (panel, text);
    }

    private static void SetTally(StackPanel panel, TextBlock text, int count, string label)
    {
        text.Text = count.ToString(CultureInfo.InvariantCulture);
        panel.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(panel, text.Text + " " + label);
    }
}
