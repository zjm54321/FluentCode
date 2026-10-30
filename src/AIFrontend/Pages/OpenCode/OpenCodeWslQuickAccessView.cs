using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

internal sealed class OpenCodeWslQuickAccessView : StackPanel
{
    private readonly Button _explorer = Action("\uEC50", ExplorerText);
    private readonly SplitButton _editor = new()
    {
        // ~40px primary target plus the native dropdown segment.
        Width = 72, Height = 36, MinWidth = 0, Padding = new Thickness(8, 0, 8, 0),
    };
    private WslHostEditor? _currentEditor;
    private int _renderVersion;
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
        Spacing = 4;
        VerticalAlignment = VerticalAlignment.Center;
        _note.Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CaptionSubtleStyleKey);
        Children.Add(_explorer);
        Children.Add(_editor);
        AutomationProperties.SetAutomationId(_explorer, "OpenCodeWslExplorer");
        AutomationProperties.SetAutomationId(_editor, "OpenCodeWslHostEditor");
        _explorer.Click += (_, _) => ExplorerRequested?.Invoke(this, EventArgs.Empty);
        _editor.Click += (_, _) =>
        {
            if (_currentEditor is { } editor) EditorRequested?.Invoke(this, new(editor.Name));
        };
        _editor.Flyout = _editors;
        _editors.Opening += (_, _) => EditorMenuRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Render(WslQuickAccessState state, IReadOnlyList<WslHostEditor> editors, string? selectedEditor)
    {
        _explorer.IsEnabled = state.Target is not null;
        var choices = WslEditorChoices.Resolve(editors, selectedEditor);
        _currentEditor = choices.Current;
        var version = ++_renderVersion;
        _editor.Content = OpenCodeWslQuickAccessIcons.Fallback();
        if (_currentEditor is { } currentEditor) _ = SetPrimaryIconAsync(currentEditor, version);
        var supported = state.Target is not null && _currentEditor is not null;
        _editors.Items.Clear();
        foreach (var editor in choices.Installed)
        {
            var item = new MenuFlyoutItem { Text = editor.Name, IsEnabled = state.Target is not null };
            // MenuFlyoutItem.Click is an action, not merely a radio checked-state change.
            item.Click += (_, _) => EditorRequested?.Invoke(this, new(editor.Name, selectDefault: true));
            _editors.Items.Add(item);
            _ = SetMenuIconAsync(item, editor);
        }
        _editor.IsEnabled = supported;
        // The directory row already shows the path; only explain what is unavailable.
        SetNote(state.Reason ?? (supported ? null : "未检测到支持 WSL 的主机编辑器"));
        ToolTipService.SetToolTip(_explorer, ExplorerText + "\n" + (state.Reason ?? state.Target?.HostPath));
        var editorText = _currentEditor is { } current ? "在 " + current.Name + " 中打开" : EditorText;
        AutomationProperties.SetName(_editor, editorText);
        ToolTipService.SetToolTip(_editor, editorText + "\n" + (state.Reason
            ?? (_currentEditor?.OpenMode == WslEditorOpenMode.RemoteWsl ? "WSL 远程模式" : state.Target?.HostPath)));
    }

    private async System.Threading.Tasks.Task SetPrimaryIconAsync(WslHostEditor editor, int version)
    {
        var icon = await OpenCodeWslQuickAccessIcons.CreateAsync(editor);
        if (version == _renderVersion) _editor.Content = icon;
    }

    private static async System.Threading.Tasks.Task SetMenuIconAsync(MenuFlyoutItem item, WslHostEditor editor)
        => item.Icon = await OpenCodeWslQuickAccessIcons.CreateAsync(editor);

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
            Width = 40, Height = 36, MinWidth = 0, Padding = new Thickness(0),
            Content = new FontIcon { Glyph = glyph, FontSize = 18 },
        };
        if (OpenCodeInteractionUi.TryStyle("OpenCodeTransparentToolButtonStyle") is { } quiet) button.Style = quiet;
        AutomationProperties.SetName(button, text);
        return button;
    }

    public void ShowFailure() => SetNote("无法打开主机应用，请检查安装与目录访问权限");
}
