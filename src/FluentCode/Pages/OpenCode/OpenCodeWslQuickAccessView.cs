using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.OpenCode;

internal sealed class OpenCodeWslQuickAccessView : StackPanel
{
    private readonly Button _explorer = Action("\uEC50", ExplorerText);
    // Primary editor target (40, same as the explorer button) + native separator (1) + a slim right chevron segment
    // instead of WinUI's 35-DIP one, so the control is about one button wide, not two.
    internal const double EditorChevronWidth = 20, EditorSplitWidth = 61;
    private readonly SplitButton _editor = new()
    {
        Width = EditorSplitWidth, Height = 36, MinWidth = 0, Padding = new Thickness(8, 0, 8, 0),
    };
    private readonly Grid _editorIcon = new() { Width = 16, Height = 16 };
    private readonly ContentControl _editorFeedback = new() { HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
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
        // Keep a stable icon host: async editor-icon refreshes must not replace an active feedback overlay.
        _editorFeedback.Content = _editorIcon;
        _editor.Content = _editorFeedback;
        _editor.Resources["SplitButtonSecondaryButtonSize"] = EditorChevronWidth;
        // Same transparent rest state as the explorer/browser/terminal tiles (stock SplitButton is control-filled);
        // hover/pressed fills and the elevation border stay native.
        Services.Appearance.ThemeResources.Aliases(_editor, ("SplitButtonBackground", "SubtleFillColorTransparentBrush"));
        // The template right-pads its 12-DIP chevron by 12; centre it in the slimmer native segment.
        _editor.Loaded += (_, _) =>
        {
            if (FindTemplatePart(_editor, "SecondaryButton") is Button chevron)
                chevron.Padding = new Thickness(0, 0, (EditorChevronWidth - 12) / 2, 0);
        };
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
        SetEditorIcon(OpenCodeWslQuickAccessIcons.Fallback());
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
        if (version == _renderVersion) SetEditorIcon(icon);
    }

    private void SetEditorIcon(UIElement icon) { _editorIcon.Children.Clear(); _editorIcon.Children.Add(icon); }

    internal ContentControl ActionControl(bool editor) => editor ? _editorFeedback : _explorer;

    internal void ResetAction(bool editor) => FluentCode.Motion.ActionFeedback.Reset(ActionControl(editor));

    internal void ShowActionSuccess(bool editor) => FluentCode.Motion.ActionFeedback.Success(ActionControl(editor), editor ? "已在编辑器中打开" : "已在资源管理器中打开");

    private static async System.Threading.Tasks.Task SetMenuIconAsync(MenuFlyoutItem item, WslHostEditor editor)
        => item.Icon = await OpenCodeWslQuickAccessIcons.CreateAsync(editor);

    private static FrameworkElement? FindTemplatePart(DependencyObject root, string name)
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Name: var n } element && n == name) return element;
            if (FindTemplatePart(child, name) is { } found) return found;
        }
        return null;
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
            Width = 40, Height = 36, MinWidth = 0, Padding = new Thickness(0),
            Content = new FontIcon { Glyph = glyph, FontSize = OpenCodeQuickAccessStyle.IconSize },
        };
        if (OpenCodeInteractionUi.TryStyle("OpenCodeTransparentToolButtonStyle") is { } quiet) button.Style = quiet;
        AutomationProperties.SetName(button, text);
        return button;
    }

    public void ShowFailure() => SetNote("无法打开主机应用，请检查安装与目录访问权限");
}
