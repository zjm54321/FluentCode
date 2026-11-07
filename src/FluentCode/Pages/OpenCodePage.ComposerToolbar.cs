using System.Linq;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace FluentCode.Pages;

public sealed partial class OpenCodePage
{
    private readonly Ellipse _composerConnectedDot = new() { Width = 6, Height = 6 };

    // Use the actual editor width, not the sidebar threshold: a new-session page
    // has no sidebars and can fit labels well before two resident panes would fit.
    // The effort control is icon-only in every layout (its level lives in the tooltip/accessible name and the
    // list rows); only agent and model chips grow labels with width.
    internal static (bool Agent, bool Model) ComposerChoiceLabels(double width)
        => (width >= 430, width >= 540);

    private void UpdateComposerToolbarLayout()
    {
        RefreshComposerToolbar();
    }

    private void InitializeComposerToolbar()
    {
        ComposerConnectionButton.Content = _composerConnectedDot;
        _composerConnectedDot.Style = OpenCodeInsightsTheme.Dot(OpenCodeInsightsTheme.Success);
        _insights!.Changed += (_, _) => QueueSessionControlCardRefresh();
        BindNativePickerChoices(BlankAgentButton, BlankModelButton, BlankStrengthButton);
        InitializeComposerContextMenu();
        RefreshComposerToolbar();
    }

    private void RefreshComposerToolbar()
    {
        var labels = ComposerChoiceLabels(ComposerInputLayout.ActualWidth);
        RefreshChildComposerVisibility();
        var enabled = SessionControlCardCanEdit();
        OpenCodeModelInfo? model;
        OpenCodeModelRef? selected;
        string? agent, color, description;
        if (_newSessionDraftActive)
        {
            var value = _blankPreferences;
            var current = value is not null && value.BlankSlotId == _activeBlankSlotId
                && value.Matches(_homeRuntimeSource, _composerController!.Snapshot.Token.Draft);
            var info = current && HasBlankAgentPresentation(value!)
                ? _activeDraftSlot!.Agents.FirstOrDefault(a => a.Id == value!.Agent) : null;
            agent = info?.Name;
            color = info?.Color;
            description = info?.Description;
            selected = current && HasBlankMetadata(value!) ? value!.Model : null;
            model = _blankModels.FirstOrDefault(m => selected is not null && m.Id == selected.Id && m.ProviderId == selected.ProviderId);
            enabled &= current && !_newSessionCreatePending && !_blankPickerPending;
        }
        else
        {
            var owns = ReferenceEquals(_agentSelectorSource, _source) && _agentSelectorRevision == _sourceRevision
                && _agentSelectorSessionId == _selectedSessionId;
            agent = owns ? _agentSelector.CurrentAgentDisplay : null;
            color = owns ? _agentSelector.CurrentAgentColor : null;
            description = owns ? _agentSelector.CurrentAgentDescription : null;
            selected = owns ? _agentSelector.CurrentModel : null;
            model = selected is not null && TryCaptureInsightsBinding(out var binding)
                ? _insights?.CurrentModelInfo(binding!, selected) : null;
        }
        RenderNativePickerChoices(BlankAgentButton, BlankModelButton, BlankStrengthButton, enabled, labels.Model);
        BlankAgentButton.SetLabelExpanded(labels.Agent);
        BlankStrengthButton.SetLabelExpanded(false);
        // The native popup remains, but closed controls have no redundant chevron. Icon-only
        // pickers share one 36-DIP square-ish footprint with zero padding so every glyph is centred.
        BlankAgentButton.MinWidth = labels.Agent ? 96 : 0;
        BlankAgentButton.Width = labels.Agent ? double.NaN : 36;
        BlankAgentButton.Padding = labels.Agent ? new Thickness(8, 0, 6, 0) : new Thickness(0);
        BlankStrengthButton.MinWidth = 0;
        BlankStrengthButton.Width = 36;
        BlankStrengthButton.Padding = new Thickness(0);
        BlankModelButton.MinWidth = labels.Model ? 96 : 0;
        BlankModelButton.Width = labels.Model ? double.NaN : 36;
        BlankModelButton.Padding = labels.Model ? new Thickness(8, 0, 8, 0) : new Thickness(0);
        DescribeComposerIcon(BlankAgentButton, "选择智能体：" + (agent ?? "不可用"));
        ToolTipService.SetToolTip(BlankAgentButton, description ?? "智能体说明不可用");
        DescribeComposerIcon(BlankModelButton, "选择模型：" + (model?.Name ?? "不可用"));
        BlankStrengthButton.Visibility = Visibility.Visible;
        DescribeComposerIcon(BlankStrengthButton, "选择推理强度：" + OpenCodeModelPresentation.VariantLabel(selected?.Variant));
        if (!_newSessionDraftActive)
        {
            if (_agentSelector.HasPendingAgent)
                ToolTipService.SetToolTip(BlankAgentButton, (description ?? "智能体说明不可用") + " · 下次发送生效");
            if (_agentSelector.HasPendingModel)
            {
                ToolTipService.SetToolTip(BlankModelButton, "选择模型：" + model?.Name + " · 下次发送生效");
                ToolTipService.SetToolTip(BlankStrengthButton, "选择推理强度：" + OpenCodeModelPresentation.VariantLabel(selected?.Variant) + " · 下次发送生效");
            }
        }
        RefreshFullScreenComposerChoices(agent, color, model, selected, enabled);
    }

    private static FrameworkElement ComposerChoiceLabel(string glyph, string text, bool expanded)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16 });
        if (expanded) panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private static void DescribeComposerIcon(Control button, string name)
    {
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
    }

    private void ComposerAgentButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SessionControlCardCanEdit()) return;
        if (_newSessionDraftActive) BlankAgentButton_Click(sender, e);
        else _agentSelector.OpenSelection(sender as FrameworkElement);
    }

    private void ComposerModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SessionControlCardCanEdit()) return;
        if (_newSessionDraftActive) BlankModelButton_Click(sender, e);
        else OpenSessionControlModelPicker(variantOnly: false, anchor: sender as FrameworkElement);
    }

    private void ComposerEffortButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SessionControlCardCanEdit()) return;
        if (_newSessionDraftActive) BlankStrengthButton_Click(sender, e);
        else OpenSessionControlModelPicker(variantOnly: true);
    }

    // Segoe Fluent Icons: BlockContact (person with prohibition) / Contact / People / Group.
    internal static string SubagentGlyph(int running) => running switch
    {
        <= 0 => "\uE8F8",
        1 => "\uE77B",
        2 => "\uE716",
        _ => "\uE902",
    };

    internal static string SubagentsLabel(int running)
        => running <= 0 ? "后台子智能体：无运行中" : "后台子智能体：" + running + " 个运行中";

    private void RenderSubagentCount(int running)
    {
        ComposerSubagentsGlyph.Glyph = SubagentGlyph(running);
        DescribeComposerIcon(ComposerSubagentsButton, SubagentsLabel(running));
    }

    private void ComposerSubagentsButton_Click(object sender, RoutedEventArgs e)
        => SessionPaneToggleButton_Click(sender, e); // B1 owns drawers; B4 owns their agent tree.

    private void ComposerConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        // Narrow: the dot carries the 概况 footer (directory + version); its › row opens the same connection details.
        if (_composerOverviewInDot && _composerOverviewFlyout is { } overview) overview.ShowAt(ComposerConnectionButton);
        else ConnectionFlyout.ShowAt(ComposerConnectionButton);
    }

    partial void SetComposerConnectedDot(bool connected)
        => ComposerConnectionButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
}
