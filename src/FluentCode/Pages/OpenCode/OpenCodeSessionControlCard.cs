using System.Numerics;
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using FluentCode.Services.OpenCode;

namespace FluentCode.Pages.OpenCode;

// Presentation only. All selections and operation authority stay with the existing page/selector.
internal sealed class OpenCodeSessionControlCard : Grid
{
    internal OpenCodeNativeComboBox AgentButton { get; } = Entry("OpenCodeSidebarAgent", "选择智能体");
    internal Button ModelButton { get; } = ModelEntry();
    internal OpenCodeNativeComboBox VariantButton { get; } = Entry("OpenCodeSidebarVariant", "选择推理强度");
    private readonly Grid _editableRows = new() { RowSpacing = 4, ColumnSpacing = 4 };
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
        Children.Add(_chrome);
        _chrome.Style = OpenCodeInsightsTheme.DockCard(); // ThemeResource setters: light / dark / high contrast
    }

    internal void Render(string agent, string? color, string? modelId, string modelName, string variant,
        string scope, bool agentEnabled, bool modelEnabled, bool variantEnabled, string? agentDescription, bool pendingAgent = false,
        bool pendingModel = false)
    {
        if (_chrome.Child != _editableRows) _chrome.Child = _editableRows;
        _chrome.Visibility = Visibility.Visible;
        Describe(AgentButton, "智能体：" + agent, scope, (agentDescription ?? "智能体说明不可用") + (pendingAgent ? " · 下一条消息生效" : string.Empty));
        Describe(ModelButton, "模型：" + modelName, scope + (pendingModel ? " · 下次发送生效" : string.Empty));
        Describe(VariantButton, "推理强度：" + variant, scope + (pendingModel ? " · 下次发送生效" : string.Empty));
        AgentButton.IsEnabled = agentEnabled;
        ModelButton.IsEnabled = modelEnabled;
        VariantButton.IsEnabled = variantEnabled;
    }

    internal void RenderReadOnly(OpenCodeChildSessionControl value)
    {
        // Preserve the exact same two-row native controls. Only the child's execution projection seeds them.
        if (_chrome.Child != _editableRows) _chrome.Child = _editableRows;
        _chrome.Visibility = Visibility.Visible;
        var noChoices = Array.Empty<OpenCodeNativeComboBox.Choice>();
        AgentButton.Render(value.Agent is { } agent ? new[] { OpenCodeNativeComboBox.Agent(new(agent, agent, null, true, null)) }
            : noChoices, value.Agent, false);
        var model = value.ModelName is { } name && value.Model is { } reference
            ? new FluentCode.Services.OpenCode.OpenCodeModelInfo { Id = reference.Id, ProviderId = reference.ProviderId, Name = name } : null;
        RenderModelButton(ModelButton, model, false);
        var effort = new OpenCodeEffortChoice(value.Model?.Variant);
        VariantButton.Render(value.Effort is { } label ? new[] { OpenCodeNativeComboBox.Effort(value.Model?.Variant, label) } : noChoices, effort, false);
        Describe(AgentButton, "智能体：" + (value.Agent ?? "未知"), "子智能体会话仅供查看。");
        Describe(ModelButton, "模型：" + (value.ModelName ?? "未知"), "子智能体会话仅供查看。");
        Describe(VariantButton, "推理强度：" + (value.Effort ?? "未知"), "子智能体会话仅供查看。");
    }

    private static OpenCodeNativeComboBox Entry(string id, string name)
    {
        var button = new OpenCodeNativeComboBox { HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 4, 6, 4), MinWidth = 0, MinHeight = 32 };
        AutomationProperties.SetAutomationId(button, id);
        AutomationProperties.SetName(button, name);
        return button;
    }

    private static Button ModelEntry()
    {
        var button = new Button { HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 4, 6, 4), MinWidth = 0, MinHeight = 32 };
        AutomationProperties.SetAutomationId(button, "OpenCodeSidebarModel");
        AutomationProperties.SetName(button, "选择模型");
        return button;
    }

    internal static void RenderModelButton(Button button, OpenCodeModelInfo? model, bool enabled, bool expanded = true)
    {
        var identity = model?.ProviderId + "\t" + model?.Id + "\t" + model?.Name + "\t" + expanded;
        if (button.Tag as string != identity)
        {
            button.Tag = identity;
            button.Content = model is null ? new TextBlock { Text = "未知" }
                : expanded ? OpenCodeModelLogo.Label(model.Id, model.Name!) : OpenCodeModelLogo.Create(model.Id, model.Name);
        }
        button.IsEnabled = enabled;
    }

    private static void Describe(Control button, string name, string scope, string? tooltip = null)
    {
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetHelpText(button, scope);
        ToolTipService.SetToolTip(button, tooltip ?? name + "\n" + scope);
    }
}
