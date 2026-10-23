using System;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

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
    private OpenCodeToolRowStatus? _status;
    private string _step = "处理中…";
    private bool _reasoning, _toolRunning, _disposed;

    internal OpenCodeToolGroupView(string groupId, Action<string, bool> onUserToggled)
    { GroupId = groupId; _toggled = onUserToggled; }
    internal string GroupId { get; }
    internal bool IsExpanded => _summary?.IsActive == true || _expanded;
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
            _visual.SetWorkingContext(_reasoning, _toolRunning, _model);
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
    internal void SetWorkingContext(bool reasoning, bool toolRunning, string? model)
    { _reasoning = reasoning; _toolRunning = toolRunning; _model = model; _visual?.SetWorkingContext(reasoning, toolRunning, model); }
    internal void BindSingleLeaf(Action? open, Action? enter, Action? exit, string? name = null, string? glyph = null, OpenCodeToolRowStatus? status = null)
    { _open = open; _enter = enter; _exit = exit; _name = name; _glyph = glyph; _status = status; _visual?.BindSingleLeaf(open, enter, exit, name, glyph, status); }
    internal void SetBackgroundAction(Action? action) { _background = action; _visual?.SetBackgroundAction(action); }
    internal void SetInlineLeaf(bool inline) { _inline = inline; _visual?.SetInlineLeaf(inline); }
    internal void Update(OpenCodeStepRunSummary summary, bool expanded, bool headerVisible)
    { _summary = summary; _expanded = summary.IsActive || expanded; _headerVisible = headerVisible; _visual?.Update(summary, _expanded, headerVisible); }
    internal void Dispose()
    { _disposed = true; _visual?.Dispose(); _visual = null; _open = _enter = _exit = _background = null; }
}

/// <summary>
/// W1/W2: a native Expander owns the complete processing group. An active turn cannot fold;
/// completed groups follow the timeline's turn-end expansion state. A single operation is its real row,
/// not a synthetic "已处理" header. Followers retain their items without duplicating the chain header.
/// </summary>
internal sealed class OpenCodeToolGroupVisual
{
    private readonly string _groupId;
    private readonly Action<string, bool> _toggled;
    private readonly StackPanel _root = new();
    private readonly Expander _expander;
    private readonly Border _itemsBorder;
    private readonly StackPanel _items = new();
    private readonly FontIcon _icon;
    private readonly TextBlock _headerText;
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

    internal OpenCodeToolGroupVisual(string groupId, Action<string, bool> onUserToggled)
    {
        _groupId = groupId;
        _toggled = onUserToggled;
        _icon = new FontIcon { Glyph = "\uE9F5", FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        _headerText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionStyleKey);
        _headerText.FontSize = 13;
        _headerText.VerticalAlignment = VerticalAlignment.Center;
        _headerText.TextTrimming = TextTrimming.CharacterEllipsis;
        _working = new OpenCodeWorkingIndicator(groupId);
        _working.VisibilityChanged += UpdateTimer;
        var iconCell = new Grid { Width = 24, VerticalAlignment = VerticalAlignment.Center };
        iconCell.Children.Add(_icon);
        iconCell.Children.Add(_working.Gear);
        var title = new Grid();
        title.Children.Add(_headerText);
        title.Children.Add(_working.Label);
        (_successTally, _successText) = CreateTally("\uE73E", OpenCodeToolTheme.Success, "OpenCodeToolGroup.Success." + groupId);
        (_errorTally, _errorText) = CreateTally("\uE711", OpenCodeToolTheme.Critical, "OpenCodeToolGroup.Errors." + groupId);
        var tally = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        tally.Children.Add(_successTally);
        tally.Children.Add(_errorTally);
        var header = new Grid { ColumnSpacing = 8, MinHeight = 30, HorizontalAlignment = HorizontalAlignment.Stretch };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(iconCell);
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        Grid.SetColumn(tally, 2);
        header.Children.Add(tally);
        _itemsBorder = new Border
        {
            Child = _items,
            Margin = new Thickness(15, 0, 0, 0),
            Padding = new Thickness(8, 0, 0, 0),
            BorderThickness = new Thickness(1, 0, 0, 0),
        };
        OpenCodeToolTheme.Apply(_itemsBorder, OpenCodeToolTheme.Border("SubtleFillColorTransparentBrush", OpenCodeToolTheme.Divider));
        _expander = new Expander
        {
            Header = header,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
        };
        AutomationProperties.SetAutomationId(_expander, "OpenCodeToolGroup." + groupId);
        _expander.Expanding += (_, _) => ExpansionChanged(true);
        _expander.Collapsed += (_, _) => ExpansionChanged(false);
        _root.Loaded += (_, _) => { _loaded = true; RefreshWorking(); };
        _root.Unloaded += (_, _) => { _loaded = false; StopTimer(); };
        ConfigureContainer();
    }

    internal FrameworkElement Element => _root;
    // The timeline asks for this before BindSingleLeaf. Keep the anchor stable while the same
    // real row moves out of its former Expander; never return the soon-to-be-detached header.
    internal FrameworkElement DetailAnchor => _items;
    internal Panel ItemsHost => _items;
    internal bool IsExpanded => _summary.IsActive || _expanded;
    internal void SetCurrentStep(string step) { } // The live header always shows the decorative working phrase (W1).
    internal void SetBackgroundAction(Action? action) { } // The real operation's row owns its native action.
    internal void SetInlineLeaf(bool inline)
    { if (_inline == inline) return; _inline = inline; ConfigureContainer(); RefreshWorking(); }
    internal void BindSingleLeaf(Action? open, Action? enter, Action? exit, string? name = null, string? glyph = null, OpenCodeToolRowStatus? status = null)
    { var single = open is not null; if (_single == single) return; _single = single; ConfigureContainer(); RefreshWorking(); }

    internal void SetWorkingContext(bool reasoning, bool toolRunning, string? model)
    {
        _activity = reasoning ? OpenCodeWorkingActivity.Reasoning : toolRunning ? OpenCodeWorkingActivity.Tool : OpenCodeWorkingActivity.General;
        _family = OpenCodeWorkingPhrases.Family(model);
        if (_working.IsActive && _phrases.UpdateContext(_activity, _family, NowMs())) RefreshWorkingText();
    }

    internal void Update(OpenCodeStepRunSummary summary, bool expanded, bool headerVisible)
    {
        ArgumentNullException.ThrowIfNull(summary);
        _summary = summary;
        _headerVisible = headerVisible;
        _expanded = summary.IsActive || expanded;
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
        UpdateAutomation();
    }

    private void ConfigureContainer()
    {
        var grouped = !_single && !_inline && _headerVisible;
        if (_groupContainer != grouped)
        {
            _expander.Content = null;
            _root.Children.Clear();
            if (grouped) { _expander.Content = _itemsBorder; _root.Children.Add(_expander); }
            else _root.Children.Add(_itemsBorder);
            _groupContainer = grouped;
        }
        var direct = _single || _inline;
        _itemsBorder.Margin = direct ? new Thickness(0) : new Thickness(15, 0, 0, 0);
        _itemsBorder.Padding = direct ? new Thickness(0) : new Thickness(8, 0, 0, 0);
        _itemsBorder.BorderThickness = direct ? new Thickness(0) : new Thickness(1, 0, 0, 0);
        _itemsBorder.Visibility = direct || IsExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ExpansionChanged(bool expanded)
    {
        if (_updating || _disposed) return;
        if (_summary.IsActive && !expanded)
        {
            _updating = true;
            _expander.IsExpanded = true;
            _updating = false;
            return;
        }
        _expanded = expanded;
        ConfigureContainer();
        UpdateAutomation();
        _toggled(_groupId, expanded);
    }

    private void UpdateAutomation()
    {
        AutomationProperties.SetName(_expander, _summary.AccessibleName + (IsExpanded ? "，已展开" : "，已折叠"));
        AutomationProperties.SetHelpText(_expander, _summary.IsActive ? "回合结束后收起处理过程" : "展开或收起处理过程");
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
        if (_disposed || !_loaded || !_working.IsActive || !_working.IsVisibleWhileLoaded) { StopTimer(); return; }
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
    internal void Dispose()
    { _disposed = true; StopTimer(); _phrases.Stop(); _working.Dispose(); }

    private static (StackPanel Panel, TextBlock Text) CreateTally(string glyph, string brushKey, string automationId)
    {
        var text = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionStyleKey);
        text.FontSize = 13;
        OpenCodeToolTheme.Apply(text, OpenCodeToolTheme.Text(brushKey, OpenCodeInteractionUi.CaptionStyleKey));
        var icon = new FontIcon { Glyph = glyph, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        OpenCodeToolTheme.Apply(icon, OpenCodeToolTheme.Icon(brushKey));
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(text);
        panel.Children.Add(icon);
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
