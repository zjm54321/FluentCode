using System;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace FluentCode.Pages;

public sealed partial class OpenCodePage
{
    private Grid? _fullScreenComposer;
    private Grid? _fullScreenBody;
    private TextBlock? _fullScreenCount;
    private Button? _fullScreenAgent, _fullScreenModel, _fullScreenEffort;
    private Button? _fullScreenSend;
    private OpenCodeComposerBinding? _fullScreenBinding;
    private double _normalComposerMaximum;
    private double _normalComposerMinimum;
    private Thickness _normalComposerPadding;
    private bool _fullScreenClosePending;
    private bool _fullScreenCloseFocus;
    private bool FullScreenComposerActive => _fullScreenComposer?.Visibility == Visibility.Visible;
    private static bool FullScreenControlDown => (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;

    // /editor relinquishes its short-lived command capability before opening the
    // same live-draft editor as the expand button. Never reopen against another tab
    // or a draft edited while the dispatcher callback was waiting.
    private void QueueFullScreenComposer(OpenCodeComposerClientActionTicket ticket)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ticket.Kind != OpenCodeComposerClientActionKind.EditDraft || !_pageActive
                || !OwnsComposerBinding(ticket.Binding) || _composerController?.Snapshot.Token != ticket.Token) return;
            OpenFullScreenComposer();
        });
    }

    private void OpenFullScreenComposer()
    {
        if (FullScreenComposerActive || IsChildComposerSession || _composerBinding is not { } binding
             || !OwnsComposerBinding(binding) || _composerView?.PrepareSubmission() != true
            || !Composer.CanReparent || GetComposerSnapshot() is not { IsActive: true, IsBusy: false }) return;
        EnsureFullScreenComposer();
        if (!Composer.TryReparent(_fullScreenBody!)) return;
        _fullScreenClosePending = false;
        _fullScreenBinding = binding;
        _normalComposerMaximum = Composer.MaxHeight;
        _normalComposerMinimum = Composer.MinHeight;
        _normalComposerPadding = Composer.Padding;
        // Reparent the SAME native editor and companion. One document adapter, ledger, composition owner and undo stack.
        ComposerCompanionHost.Children.Remove(_composerView!);
        _fullScreenBody!.Children.Add(_composerView!);
        Grid.SetRow(Composer, 1);
        Composer.MaxHeight = double.PositiveInfinity;
        Composer.MinHeight = 0;
        Composer.Padding = new Thickness(16);
        ScrollViewer.SetVerticalScrollBarVisibility(Composer, ScrollBarVisibility.Auto);
        _fullScreenComposer!.Visibility = Visibility.Visible;
        RefreshComposerToolbar();
        UpdateFullScreenCount();
        Composer.Focus(FocusState.Programmatic);
    }

    private void EnsureFullScreenComposer()
    {
        if (_fullScreenComposer is not null) return;
        var surface = new Grid { RowSpacing = 16, Padding = new Thickness(24, 16, 24, 16),
            Style = (Style)Resources["FullScreenComposerSurfaceStyle"] };
        surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AutomationProperties.SetAutomationId(surface, "OpenCodeFullScreenComposer");
        var heading = new Grid { ColumnSpacing = 12 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock { Text = "编辑消息", Style = OpenCodeInteractionUi.TryStyle("SubtitleTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center });
        var collapse = new Button { Content = new FontIcon { Glyph = "\uE73F", FontSize = 16 }, Width = 32, Height = 32, MinWidth = 0, Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center };
        if (OpenCodeInteractionUi.TryStyle("OpenCodeTransparentToolButtonStyle") is { } quietCollapse) collapse.Style = quietCollapse;
        AutomationProperties.SetName(collapse, "收起编辑器");
        collapse.Click += (_, _) => CloseFullScreenComposer(focus: true);
        Grid.SetColumn(collapse, 2); heading.Children.Add(collapse);
        surface.Children.Add(heading);
        var body = _fullScreenBody = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        surface.Children.Add(body);
        Composer.DocumentChanged += UpdateFullScreenCount;
        Composer.NativeLayoutReady += TryPendingFullScreenClose;
        var footer = new Grid { ColumnSpacing = 8 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _fullScreenAgent = new Button { Style = (Style)Resources["ComposerAgentDropdownStyle"] };
        _fullScreenModel = new Button(); _fullScreenEffort = new Button();
        _fullScreenAgent.Click += ComposerAgentButton_Click;
        _fullScreenModel.Click += ComposerModelButton_Click;
        OpenCodePickerInput.Track(_fullScreenModel);
        _fullScreenEffort.Click += ComposerEffortButton_Click;
        choices.Children.Add(_fullScreenAgent); choices.Children.Add(_fullScreenModel); choices.Children.Add(_fullScreenEffort);
        footer.Children.Add(choices);
        _fullScreenCount = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Style = OpenCodeInteractionUi.TryStyle("CaptionTextBlockStyle") };
        Grid.SetColumn(_fullScreenCount, 1); footer.Children.Add(_fullScreenCount);
        var done = new Button { Content = "收起" };
        done.Click += (_, _) => CloseFullScreenComposer(focus: true);
        Grid.SetColumn(done, 2); footer.Children.Add(done);
        _fullScreenSend = new Button { Content = "发送", Style = OpenCodeInteractionUi.TryStyle("AccentButtonStyle") };
        _fullScreenSend.Click += (_, _) => SubmitFullScreenComposer();
        Grid.SetColumn(_fullScreenSend, 3); footer.Children.Add(_fullScreenSend);
        Grid.SetRow(footer, 2); surface.Children.Add(footer);
        var overlay = _fullScreenComposer = new Grid { Visibility = Visibility.Collapsed, Padding = new Thickness(16),
            Style = (Style)Resources["FullScreenComposerOverlayStyle"] };
        var card = new Border { Child = surface, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Style = (Style)Resources["FullScreenComposerCardStyle"] };
        overlay.Children.Add(card);
        overlay.SizeChanged += (_, _) => RefreshComposerToolbar();
        Grid.SetRow(overlay, 1); Canvas.SetZIndex(overlay, 100);
        PageRoot.Children.Add(overlay);
    }

    private void RefreshFullScreenComposerChoices(string? agent, string? color, OpenCodeModelInfo? model, OpenCodeModelRef? selected, bool enabled)
    {
        if (!FullScreenComposerActive) return;
        if (_fullScreenBinding is not { } binding || !OwnsComposerBinding(binding) || IsChildComposerSession)
        { CloseFullScreenComposer(focus: false); return; }
        var snapshot = _composerController!.Snapshot;
        var labels = ComposerChoiceLabels(Math.Max(0, _fullScreenComposer!.ActualWidth - 260));
        _fullScreenAgent!.Content = ComposerChoiceLabel(OpenCodeAgentIcons.Glyph(agent), OpenCodeAgentIcons.DisplayName(agent ?? "不可用"), labels.Agent);
        OpenCodeAgentPresentation.Apply(_fullScreenAgent, color);
        _fullScreenModel!.Content = labels.Model ? OpenCodeModelLogo.Label(model?.Id ?? selected?.Id, model?.Name ?? "模型不可用") : OpenCodeModelLogo.Create(model?.Id ?? selected?.Id, model?.Name);
        var gauge = new OpenCodeComposerEffortGauge { Dimmed = !enabled || model is null, VerticalAlignment = VerticalAlignment.Center };
        gauge.Render(OpenCodeComposerEffort.Ticks(model, selected?.Variant));
        _fullScreenEffort!.Content = gauge; // Icon-only in every layout; the level is in the tooltip/accessible name.
        var buttons = new[] { _fullScreenAgent, _fullScreenModel, _fullScreenEffort };
        var expanded = new[] { labels.Agent, labels.Model, false };
        for (var i = 0; i < buttons.Length; i++)
        { buttons[i].MinWidth = 0; buttons[i].Width = expanded[i] ? double.NaN : 36; buttons[i].Height = 32; buttons[i].Padding = expanded[i] ? new Thickness(8, 0, 8, 0) : new Thickness(0); }
        DescribeComposerIcon(_fullScreenAgent, "选择智能体：" + OpenCodeAgentIcons.DisplayName(agent ?? "不可用"));
        DescribeComposerIcon(_fullScreenModel, "选择模型：" + (model?.Name ?? "不可用"));
        DescribeComposerIcon(_fullScreenEffort, "选择推理强度：" + OpenCodeModelPresentation.VariantLabel(selected?.Variant));
        _fullScreenCount!.Visibility = _fullScreenComposer.ActualWidth >= 600 ? Visibility.Visible : Visibility.Collapsed;
        _fullScreenAgent.IsEnabled = _fullScreenModel.IsEnabled = enabled;
        _fullScreenEffort.IsEnabled = enabled && model is not null;
        _fullScreenSend!.IsEnabled = snapshot.CanSubmit && !snapshot.IsBusy && snapshot.HasDraft;
    }

    private void UpdateFullScreenCount()
    {
        if (_fullScreenCount is null) return;
        var text = Composer.Text;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').Length;
        _fullScreenCount.Text = $"{lines} 行 · {new System.Globalization.StringInfo(text).LengthInTextElements} 字";
        if (_fullScreenSend is not null) _fullScreenSend.IsEnabled = GetComposerSnapshot() is { CanSubmit: true, IsBusy: false, HasDraft: true };
    }

    private void CloseFullScreenComposer(bool focus)
    {
        if (!FullScreenComposerActive) return;
        if (_isComposing || !Composer.CanReparent || _composerView?.InputEvidence.Composing == true)
        {
            _fullScreenClosePending = true;
            _fullScreenCloseFocus |= focus;
            return;
        }
        var binding = _fullScreenBinding;
        if (binding is not null && OwnsComposerBinding(binding) && _composerView?.PrepareSubmission() != true) return;
        var start = Composer.SelectionStart;
        var length = Composer.SelectionLength;
        if (!Composer.TryReparent(ComposerEditorHost, 0)) return;
        _fullScreenClosePending = false;
        _fullScreenCloseFocus = false;
        _fullScreenBody!.Children.Remove(_composerView!);
        Grid.SetRow(Composer, 0);
        ComposerCompanionHost.Children.Add(_composerView!);
        Composer.MaxHeight = _normalComposerMaximum;
        Composer.MinHeight = _normalComposerMinimum;
        Composer.Padding = _normalComposerPadding;
        _fullScreenComposer!.Visibility = Visibility.Collapsed;
        _fullScreenBinding = null;
        if (focus && binding is not null && OwnsComposerBinding(binding))
            RestoreExpandedComposerSelection(binding, start, length);
        _composerView!.RequestRender();
        QueueComposerLayout();
    }
    private void TryPendingFullScreenClose()
    {
        if (!_fullScreenClosePending || _isComposing || !Composer.CanReparent || _composerView?.InputEvidence.IsClean != true) return;
        CloseFullScreenComposer(_fullScreenCloseFocus);
    }

    private void SubmitFullScreenComposer()
    {
        if (_composerView?.PrepareSubmission() != true || _fullScreenSend?.IsEnabled != true) return;
        var binding = _fullScreenBinding;
        var token = _composerController!.Snapshot.Token;
        CloseFullScreenComposer(focus: false);
        _composerView!.RequestRender();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (binding is not null && ReferenceEquals(binding, _composerBinding) && OwnsComposerBinding(binding)
                && _composerController?.Snapshot.Token == token) RequestComposerSubmission();
        });
    }
}
