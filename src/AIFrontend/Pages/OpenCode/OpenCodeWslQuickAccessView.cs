using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

internal sealed class OpenCodeWslQuickAccessView : StackPanel
{
    private readonly Button _explorer = Action("\uEC50", ExplorerText);
    private readonly Button _editor = Action("\uE70F", EditorText);
    private const string ExplorerText = "在资源管理器中打开";
    private const string EditorText = "在主机文本编辑器中打开";
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    /// <summary>Reason / failure line; the page places it under the directory row (hidden when there is nothing to explain).</summary>
    public TextBlock Note => _note;
    public event EventHandler? ExplorerRequested;
    public event EventHandler<WslEditorRequestedEventArgs>? EditorRequested;
    public event EventHandler? EditorMenuRequested;
    private readonly MenuFlyout _editors = new();

    public OpenCodeWslQuickAccessView()
    {
        // Session card: two native icon buttons directly below the session ID.
        Orientation = Orientation.Horizontal;
        Spacing = 2;
        _note.Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CaptionSubtleStyleKey);
        Children.Add(_explorer);
        Children.Add(_editor);
        AutomationProperties.SetAutomationId(_explorer, "OpenCodeWslExplorer");
        AutomationProperties.SetAutomationId(_editor, "OpenCodeWslHostEditor");
        _explorer.Click += (_, _) => ExplorerRequested?.Invoke(this, EventArgs.Empty);
        _editor.Flyout = _editors;
        _editors.Opening += (_, _) => EditorMenuRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Render(WslQuickAccessState state, IReadOnlyList<WslHostEditor> editors, string? selectedEditor)
    {
        _explorer.IsEnabled = state.Target is not null;
        var supported = false;
        _editors.Items.Clear();
        foreach (var editor in editors)
        {
            var enabled = state.Target is not null && editor.Executable is not null;
            supported |= enabled;
            var item = new ToggleMenuFlyoutItem { Text = editor.Name + (editor.Reason is { } reason ? " · " + reason : ""), IsEnabled = enabled,
                IsChecked = editor.Name == selectedEditor };
            item.Click += (_, _) => EditorRequested?.Invoke(this, new(editor.Name));
            _editors.Items.Add(item);
        }
        _editor.IsEnabled = supported;
        // The directory row already shows the path; only explain what is unavailable.
        SetNote(state.Reason ?? (supported ? null : "未检测到支持 WSL 的主机编辑器"));
        ToolTipService.SetToolTip(_explorer, ExplorerText + "\n" + (state.Reason ?? state.Target?.HostPath));
        var explanations = new List<string>();
        foreach (var editor in editors) explanations.Add(editor.Name + "：" + (editor.Reason
            ?? (editor.OpenMode == WslEditorOpenMode.RemoteWsl ? "WSL 远程模式" : "打开 WSL UNC 文件夹")));
        ToolTipService.SetToolTip(_editor, EditorText + "\n" + (state.Reason ?? string.Join("\n", explanations)));
    }

    private void SetNote(string? text)
    {
        _note.Text = text ?? string.Empty;
        _note.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    // Native transparent icon button; the label lives in the tooltip and automation name.
    private static Button Action(string glyph, string text)
    {
        var button = new Button
        {
            Width = 30, Height = 30, MinWidth = 0, Padding = new Thickness(0),
            Content = new FontIcon { Glyph = glyph, FontSize = 14 },
        };
        if (OpenCodeInteractionUi.TryStyle("OpenCodeTransparentToolButtonStyle") is { } quiet) button.Style = quiet;
        AutomationProperties.SetName(button, text);
        return button;
    }

    public void ShowFailure() => SetNote("无法打开主机应用，请检查安装与目录访问权限");
}
