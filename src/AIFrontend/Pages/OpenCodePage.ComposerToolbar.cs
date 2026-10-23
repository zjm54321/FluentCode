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
    private readonly OpenCodeComposerEffortGauge _composerEffortGauge = new();
    private readonly Ellipse _composerConnectedDot = new() { Width = 6, Height = 6 };
    private FrameworkElement? _composerModelLogo;
    private bool _composerExpandedChoices;

    // Use the actual editor width, not the sidebar threshold: a new-session page
    // has no sidebars and can fit labels well before two resident panes would fit.
    internal static bool ExpandBlankComposerChoices(bool newSession, double composerWidth)
        => newSession && composerWidth >= 600;

    private void UpdateComposerToolbarLayout()
    {
        if (_composerExpandedChoices != ExpandBlankComposerChoices(_newSessionDraftActive, ComposerInputLayout.ActualWidth))
            RefreshComposerToolbar();
    }

    private void InitializeComposerToolbar()
    {
        ComposerConnectionButton.Content = _composerConnectedDot;
        _composerConnectedDot.Style = OpenCodeInsightsTheme.Dot(OpenCodeInsightsTheme.Success);
        _insights!.Changed += (_, _) => QueueSessionControlCardRefresh();
        RefreshComposerToolbar();
    }

    private void RefreshComposerToolbar()
    {
        var expanded = ExpandBlankComposerChoices(_newSessionDraftActive, ComposerInputLayout.ActualWidth);
        _composerExpandedChoices = expanded;
        var enabled = SessionControlCardCanEdit();
        OpenCodeModelInfo? model;
        OpenCodeModelRef? selected;
        string? agent, color;
        if (_newSessionDraftActive)
        {
            var value = _blankPreferences;
            var current = value is not null && value.BlankSlotId == _activeBlankSlotId
                && value.Matches(_homeRuntimeSource, _composerController!.Snapshot.Token.Draft);
            var info = current && HasBlankAgentPresentation(value!)
                ? _activeDraftSlot!.Agents.FirstOrDefault(a => a.Id == value!.Agent) : null;
            agent = info?.Name;
            color = info?.Color;
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
            selected = owns ? _agentSelector.CurrentModel : null;
            model = selected is not null && TryCaptureInsightsBinding(out var binding)
                ? _insights?.CurrentModelInfo(binding!, selected) : null;
        }
        BlankAgentButton.Content = expanded ? OpenCodeAgentPresentation.Name(agent ?? "不可用")
            : new FontIcon { Glyph = "\uE99A", FontSize = 18 };
        OpenCodeAgentPresentation.Apply(BlankAgentButton, color);
        if (BlankAgentButton.Content is TextBlock agentLabel) OpenCodeAgentPresentation.Apply(agentLabel, color);
        DescribeComposerIcon(BlankAgentButton, "选择智能体：" + (agent ?? "不可用"));
        var key = expanded + "\n" + model?.Id + "\n" + model?.Name + "\n" + selected?.Id;
        if (BlankModelButton.Tag as string != key || _composerModelLogo is null)
        {
            BlankModelButton.Tag = key;
            _composerModelLogo = expanded
                ? OpenCodeModelLogo.Label(model?.Id ?? selected?.Id, model?.Name ?? selected?.Id ?? "不可用")
                : OpenCodeModelLogo.Create(model?.Id ?? selected?.Id, model?.Name);
        }
        BlankModelButton.Content = _composerModelLogo;
        DescribeComposerIcon(BlankModelButton, "选择模型：" + (model?.Name ?? selected?.Id ?? "不可用"));
        var ticks = OpenCodeComposerEffort.Ticks(model, selected?.Variant);
        _composerEffortGauge.Render(ticks);
        BlankStrengthButton.Content = expanded ? OpenCodeModelPresentation.VariantLabel(selected?.Variant) : _composerEffortGauge;
        BlankStrengthButton.Visibility = ticks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DescribeComposerIcon(BlankStrengthButton, "选择推理强度：" + OpenCodeModelPresentation.VariantLabel(selected?.Variant));
        BlankAgentButton.IsEnabled = enabled;
        BlankModelButton.IsEnabled = enabled;
        BlankStrengthButton.IsEnabled = enabled && ticks.Count > 0;
        _composerEffortGauge.Dimmed = !BlankStrengthButton.IsEnabled; // Visual only: mirror the button's disabled state.
        foreach (var button in new[] { BlankAgentButton, BlankModelButton, BlankStrengthButton })
        {
            button.Width = expanded ? double.NaN : 32;
            button.Padding = expanded ? new Thickness(8, 0, 8, 0) : new Thickness(0);
        }
    }

    private static void DescribeComposerIcon(Button button, string name)
    {
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
    }

    private void ComposerAgentButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SessionControlCardCanEdit()) return;
        if (_newSessionDraftActive) BlankAgentButton_Click(sender, e);
        else _agentSelector.OpenSelection();
    }

    private void ComposerModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SessionControlCardCanEdit()) return;
        if (_newSessionDraftActive) BlankModelButton_Click(sender, e);
        else OpenSessionControlModelPicker(variantOnly: false);
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
