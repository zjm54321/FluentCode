using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

internal sealed class OpenCodeWslQuickAccessView : StackPanel
{
    private readonly Button _explorer = Action("\uE838", ExplorerText);
    private readonly Button _editor = Action("\uE70F", EditorText);
    private const string ExplorerText = "在资源管理器中打开";
    private const string EditorText = "在主机文本编辑器中打开";
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap };
    public event EventHandler? ExplorerRequested;
    public event EventHandler? EditorRequested;
    public event EventHandler? EditorMenuRequested;
    private readonly MenuFlyout _editors = new();

    public OpenCodeWslQuickAccessView()
    {
        // Compact strip on the shared 16epx right-pane column; 4epx between rows keeps the two actions one group.
        Spacing = 4;
        Margin = new Thickness(16, 4, 16, 8);
        _note.Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CaptionSubtleStyleKey);
        _note.Margin = new Thickness(2, 2, 0, 0);
        Children.Add(_explorer);
        Children.Add(_editor);
        Children.Add(_note);
        AutomationProperties.SetAutomationId(_explorer, "OpenCodeWslExplorer");
        AutomationProperties.SetAutomationId(_editor, "OpenCodeWslHostEditor");
        _explorer.Click += (_, _) => ExplorerRequested?.Invoke(this, EventArgs.Empty);
        _editor.Flyout = _editors;
        _editors.Opening += (_, _) => EditorMenuRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Render(WslQuickAccessState state, IReadOnlyList<WslHostEditor> editors)
    {
        _explorer.IsEnabled = state.Target is not null;
        var supported = false;
        _editors.Items.Clear();
        foreach (var editor in editors)
        {
            var enabled = state.Target is not null && editor.VerifiedWsl && editor.Executable is not null;
            supported |= enabled;
            var item = new MenuFlyoutItem { Text = editor.Name + (editor.Reason is { } reason ? " · " + reason : ""), IsEnabled = enabled };
            item.Click += (_, _) => EditorRequested?.Invoke(this, EventArgs.Empty);
            _editors.Items.Add(item);
        }
        _editor.IsEnabled = supported;
        _note.Text = state.Reason ?? (supported ? state.Target?.LinuxDirectory : "未检测到支持 WSL 的主机编辑器");
        ToolTipService.SetToolTip(_explorer, state.Reason ?? state.Target?.UncPath);
        var explanations = new List<string>();
        foreach (var editor in editors) explanations.Add(editor.Name + "：" + (editor.Reason ?? "支持 WSL 远程模式"));
        ToolTipService.SetToolTip(_editor, state.Reason ?? string.Join("\n", explanations));
    }

    // Native button, left-aligned glyph + label; explicit automation name because the content is no longer a bare string.
    private static Button Action(string glyph, string text)
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 5, 10, 5),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            },
        };
        AutomationProperties.SetName(button, text);
        return button;
    }

    public void ShowFailure() => _note.Text = "无法打开主机应用，请检查安装与目录访问权限";
}
