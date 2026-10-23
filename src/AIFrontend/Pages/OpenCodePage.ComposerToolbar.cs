using System.Linq;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private readonly Ellipse _composerConnectedDot = new() { Width = 6, Height = 6 };

    // Use the actual editor width, not the sidebar threshold: a new-session page
    // has no sidebars and can fit labels well before two resident panes would fit.
    internal static (bool Agent, bool Model, bool Effort) ComposerChoiceLabels(double width)
        => (width >= 430, width >= 540, width >= 680);

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
        BlankStrengthButton.SetLabelExpanded(labels.Effort);
        // Native ComboBoxes retain their own chevron (38epx template column); the
        // icon-only width accommodates it instead of clipping a hand-drawn replacement.
        BlankAgentButton.MinWidth = labels.Agent ? 96 : 0;
        BlankAgentButton.Width = labels.Agent ? double.NaN : 60;
        BlankAgentButton.Padding = labels.Agent ? new Thickness(8, 0, 6, 0) : new Thickness(6, 0, 0, 0);
        BlankStrengthButton.MinWidth = labels.Effort ? 64 : 0;
        BlankStrengthButton.Width = labels.Effort ? double.NaN : 60;
        BlankStrengthButton.Padding = labels.Effort ? new Thickness(8, 0, 6, 0) : new Thickness(6, 0, 0, 0);
        BlankModelButton.MinWidth = labels.Model ? 96 : 0;
        BlankModelButton.Width = labels.Model ? double.NaN : 52;
        BlankModelButton.Padding = labels.Model ? new Thickness(8, 0, 8, 0) : new Thickness(4, 0, 4, 0);
        DescribeComposerIcon(BlankAgentButton, "选择智能体：" + (agent ?? "不可用"));
        ToolTipService.SetToolTip(BlankAgentButton, description ?? "智能体说明不可用");
        DescribeComposerIcon(BlankModelButton, "选择模型：" + (model?.Name ?? "不可用"));
        BlankStrengthButton.Visibility = Visibility.Visible;
        DescribeComposerIcon(BlankStrengthButton, "选择推理强度：" + OpenCodeModelPresentation.VariantLabel(selected?.Variant));
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

    private void ComposerSubagentsButton_Click(object sender, RoutedEventArgs e)
        => SessionPaneToggleButton_Click(sender, e); // B1 owns drawers; B4 owns their agent tree.

    private void ComposerConnectionButton_Click(object sender, RoutedEventArgs e)
        => ConnectionFlyout.ShowAt(ComposerConnectionButton);

    partial void SetComposerConnectedDot(bool connected)
        => ComposerConnectionButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
}
