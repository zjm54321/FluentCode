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
    internal Button AgentButton { get; } = Entry("OpenCodeSidebarAgent", "选择智能体");
    internal Button ModelButton { get; } = Entry("OpenCodeSidebarModel", "选择模型");
    internal Button VariantButton { get; } = Entry("OpenCodeSidebarVariant", "选择推理强度");
    private readonly TextBlock _agentName = OpenCodeAgentPresentation.Name(string.Empty);
    // Layout D bottom-left floating card: in-app acrylic, card stroke, 8 radius, raised with a system ThemeShadow.
    private readonly Border _chrome = new() { Padding = new Thickness(8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
        Shadow = new ThemeShadow(), Translation = new Vector3(0, 0, 16) };

    internal OpenCodeSessionControlCard()
    {
        // Same outer edges as the 子智能体 card above; the dock grid owns the gutter and the 12epx card gap.
        AutomationProperties.SetAutomationId(this, "OpenCodeSessionControlCard");
        var rows = new Grid { RowSpacing = 4, ColumnSpacing = 4 };
        rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // A theme-resource setter survives Apply's ClearValue when the next agent has no custom RGB.
        AgentButton.Style = (Style)XamlReader.Load("<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"Button\"><Setter Property=\"Foreground\" Value=\"{ThemeResource AccentTextFillColorPrimaryBrush}\"/></Style>");
        AgentButton.Content = WithChevron(_agentName);
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
        Children.Add(_chrome);
        _chrome.Style = OpenCodeInsightsTheme.DockCard(); // ThemeResource setters: light / dark / high contrast
    }

    internal void Render(string agent, string? color, string? modelId, string modelName, string variant,
        string scope, bool agentEnabled, bool modelEnabled, bool variantEnabled, string? agentDescription)
    {
        _agentName.Text = agent;
        OpenCodeAgentPresentation.Apply(AgentButton, color);
        // Reuse the logo visual while typing; this is a visual identity, never a selection cache.
        var visualKey = modelId + "\n" + modelName;
        if (ModelButton.Tag as string != visualKey)
        {
            ModelButton.Tag = visualKey;
            ModelButton.Content = WithChevron(OpenCodeModelLogo.Label(modelId, modelName));
        }
        VariantButton.Content = WithChevron(new TextBlock { Text = variant, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        Describe(AgentButton, "智能体：" + agent, scope, agentDescription ?? "智能体说明不可用");
        Describe(ModelButton, "模型：" + modelName, scope);
        Describe(VariantButton, "推理强度：" + variant, scope);
        AgentButton.IsEnabled = agentEnabled;
        ModelButton.IsEnabled = modelEnabled;
        VariantButton.IsEnabled = variantEnabled;
    }

    private static Button Entry(string id, string name)
    {
        var button = new Button { HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 4, 6, 4), MinWidth = 0, MinHeight = 32 };
        AutomationProperties.SetAutomationId(button, id);
        AutomationProperties.SetName(button, name);
        return button;
    }

    // Small trailing chevron: these entries open pickers, so they read as selectors rather than plain buttons.
    private static Grid WithChevron(FrameworkElement label)
    {
        var grid = new Grid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(label);
        var chevron = new FontIcon { Glyph = "\uE70D", FontSize = 10, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(chevron);
        return grid;
    }

    private static void Describe(Button button, string name, string scope, string? tooltip = null)
    {
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetHelpText(button, scope);
        ToolTipService.SetToolTip(button, tooltip ?? name + "\n" + scope);
    }
}
