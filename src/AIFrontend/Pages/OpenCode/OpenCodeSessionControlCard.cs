using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace AIFrontend.Pages.OpenCode;

// Presentation only. All selections and operation authority stay with the existing page/selector.
internal sealed class OpenCodeSessionControlCard : Grid
{
    internal OpenCodeNativeComboBox AgentButton { get; } = Entry("OpenCodeSidebarAgent", "选择智能体");
    internal OpenCodeNativeComboBox ModelButton { get; } = Entry("OpenCodeSidebarModel", "选择模型");
    internal OpenCodeNativeComboBox VariantButton { get; } = Entry("OpenCodeSidebarVariant", "选择推理强度");
    private readonly Grid _editableRows = new() { RowSpacing = 4, ColumnSpacing = 4 };
    private readonly Grid _readOnlyRows = new() { RowSpacing = 4, ColumnSpacing = 8, Padding = new Thickness(8, 4, 8, 4), IsHitTestVisible = false };
    private readonly TextBlock _readOnlyAgentName = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly FontIcon _readOnlyAgentIcon = new() { FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _readOnlyModel = new();
    private readonly TextBlock _readOnlyEffortName = new();
    private readonly Grid _readOnlyAgent;
    private readonly Grid _readOnlyEffort;
    // Layout D bottom-left floating card: in-app acrylic, card stroke, 8 radius, raised with a system ThemeShadow.
    private readonly Border _chrome = new() { Padding = new Thickness(8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
        Shadow = new ThemeShadow(), Translation = new Vector3(0, 0, 16) };

    internal OpenCodeSessionControlCard()
    {
        // Same outer edges as the 子智能体 card above; the dock grid owns the gutter and the 12epx card gap.
        AutomationProperties.SetAutomationId(this, "OpenCodeSessionControlCard");
        var rows = _editableRows;
        rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumnSpan(AgentButton, 2);
        Grid.SetRow(ModelButton, 1);
        Grid.SetRow(VariantButton, 1);
        Grid.SetColumn(VariantButton, 1);
        // Model keeps the flexible width; effort is short ("高" / "xhigh"), capped so long variants ellipsize.
        VariantButton.MinWidth = 64;
        VariantButton.MaxWidth = 104;
        rows.Children.Add(AgentButton);
        rows.Children.Add(ModelButton);
        rows.Children.Add(VariantButton);
        _chrome.Child = rows;
        _readOnlyRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _readOnlyRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _readOnlyRows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _readOnlyRows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _readOnlyAgent = WithIcon(_readOnlyAgentName, OpenCodeAgentIcons.DefaultGlyph, _readOnlyAgentIcon);
        _readOnlyEffort = WithIcon(_readOnlyEffortName, "\uEC4A");
        Grid.SetColumnSpan(_readOnlyAgent, 2);
        Grid.SetRow(_readOnlyModel, 1);
        Grid.SetRow(_readOnlyEffort, 1);
        Grid.SetColumn(_readOnlyEffort, 1);
        _readOnlyRows.Children.Add(_readOnlyAgent);
        _readOnlyRows.Children.Add(_readOnlyModel);
        _readOnlyRows.Children.Add(_readOnlyEffort);
        Children.Add(_chrome);
        _chrome.Style = OpenCodeInsightsTheme.DockCard(); // ThemeResource setters: light / dark / high contrast
    }

    internal void Render(string agent, string? color, string? modelId, string modelName, string variant,
        string scope, bool agentEnabled, bool modelEnabled, bool variantEnabled, string? agentDescription, bool pendingAgent = false)
    {
        if (_chrome.Child != _editableRows) _chrome.Child = _editableRows;
        _chrome.Visibility = Visibility.Visible;
        Describe(AgentButton, "智能体：" + agent, scope, (agentDescription ?? "智能体说明不可用") + (pendingAgent ? " · 下一条消息生效" : string.Empty));
        Describe(ModelButton, "模型：" + modelName, scope);
        Describe(VariantButton, "推理强度：" + variant, scope);
        AgentButton.IsEnabled = agentEnabled;
        ModelButton.IsEnabled = modelEnabled;
        VariantButton.IsEnabled = variantEnabled;
    }

    internal void RenderReadOnly(OpenCodeChildSessionControl value)
    {
        // Plain visuals, not disabled DropDownButtons: no chevrons, tab stops, tooltip or hover affordance.
        AgentButton.IsEnabled = ModelButton.IsEnabled = VariantButton.IsEnabled = false;
        _chrome.Child = _readOnlyRows;
        _readOnlyAgent.Visibility = value.Agent is null ? Visibility.Collapsed : Visibility.Visible;
        _readOnlyAgentName.Text = OpenCodeAgentIcons.DisplayName(value.Agent);
        _readOnlyAgentIcon.Glyph = OpenCodeAgentIcons.Glyph(value.Agent);
        AutomationProperties.SetName(_readOnlyAgent, "智能体：" + _readOnlyAgentName.Text);
        _readOnlyModel.Visibility = value.ModelName is null ? Visibility.Collapsed : Visibility.Visible;
        var modelKey = value.Model?.Id + "\n" + value.ModelName;
        if (_readOnlyModel.Tag as string != modelKey)
        {
            _readOnlyModel.Tag = modelKey;
            _readOnlyModel.Child = value.ModelName is null ? null : OpenCodeModelLogo.Label(value.Model?.Id, value.ModelName);
        }
        AutomationProperties.SetName(_readOnlyModel, "模型：" + value.ModelName);
        _readOnlyEffort.Visibility = value.Effort is null ? Visibility.Collapsed : Visibility.Visible;
        _readOnlyEffortName.Text = value.Effort ?? string.Empty;
        AutomationProperties.SetName(_readOnlyEffort, "推理强度：" + value.Effort);
        _chrome.Visibility = value.Agent is null && value.ModelName is null && value.Effort is null
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private static OpenCodeNativeComboBox Entry(string id, string name)
    {
        var button = new OpenCodeNativeComboBox { HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 4, 6, 4), MinWidth = 0, MinHeight = 32 };
        AutomationProperties.SetAutomationId(button, id);
        AutomationProperties.SetName(button, name);
        return button;
    }

    private static Grid WithIcon(FrameworkElement label, string glyph, FontIcon? icon = null)
    {
        var grid = new Grid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        grid.Children.Add(icon ?? new FontIcon { Glyph = glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
        return grid;
    }

    private static void Describe(Control button, string name, string scope, string? tooltip = null)
    {
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetHelpText(button, scope);
        ToolTipService.SetToolTip(button, tooltip ?? name + "\n" + scope);
    }
}
