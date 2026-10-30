using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Floating-card terminal: one browser-height toolbar over the renderer. Viewing is read-only; close only requests detach.</summary>
internal sealed class OpenCodePersistentPtyView : Grid
{
    internal const double ToolbarHeight = 46;
    internal OpenCodePersistentPtyRenderer Renderer { get; } = new();
    private readonly TextBlock _title = new() { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _status = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _statusDot = new() { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _statusPill = new() { Height = 24, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _detail = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly InfoBar _notice = new() { IsClosable = false, IsOpen = false, Severity = InfoBarSeverity.Warning, CornerRadius = new CornerRadius(0) };
    private readonly Button _back = IconButton("\uE8BB", "关闭终端浮窗");
    private readonly Button _copy = IconButton("\uE8C8", "复制选中的终端文本");
    private string _selection = string.Empty;
    private string? _fitFailure;
    private readonly Button _takeControl = TextButton("接管终端");
    private readonly Button _fit = TextButton("适应窗口");
    private readonly Button _release = TextButton("回到只读");
    private readonly Button _reconnect = TextButton("重新连接");
    private OpenCodePersistentPtyPresentation? _model;
    private ContentDialog? _confirmation;
    internal event Action<OpenCodePersistentPtyViewContext>? TakeControlRequested;
    internal event Action<OpenCodePersistentPtyViewContext>? ReleaseControlRequested;
    internal event Action<OpenCodePersistentPtyViewContext>? DetachRequested;
    internal event Action<OpenCodePersistentPtyViewContext>? ReconnectRequested;
    internal event Action<OpenCodePersistentPtySelection>? CopySelectionRequested;
    internal event Action? AppearanceRequested;
    internal event Action? NewTerminalRequested;

    internal OpenCodePersistentPtyView()
    {
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(ToolbarHeight) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var toolbar = new Grid { Padding = new Thickness(12, 0, 8, 0), ColumnSpacing = 10, BorderThickness = new Thickness(0, 0, 0, 1) };
        Theme(toolbar, "BorderBrush", "DividerStrokeColorDefaultBrush");
        foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        var icon = OpenCodeQuickAccessStyle.TerminalIcon(); // Shared Fluent ">_" terminal icon (16 DIP).
        toolbar.Children.Add(icon);
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Children = { _title, _detail } };
        Grid.SetColumn(heading, 1); toolbar.Children.Add(heading);
        _statusPill.Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { _statusDot, _status } };
        Theme(_statusPill, "Background", "SubtleFillColorSecondaryBrush");
        Grid.SetColumn(_statusPill, 2); toolbar.Children.Add(_statusPill);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Children = { _takeControl, _fit, _release, _reconnect } };
        Grid.SetColumn(actions, 3); toolbar.Children.Add(actions);
        var divider = new Border { Width = 1, Height = 20, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        Theme(divider, "Background", "DividerStrokeColorDefaultBrush");
        var more = IconButton("\uE712", "更多");
        AutomationProperties.SetAutomationId(more, "OpenCodePersistentPtyMore");
        var menu = new MenuFlyout();
        var create = new MenuFlyoutItem { Text = "新建终端" };
        create.Click += (_, _) => NewTerminalRequested?.Invoke();
        menu.Items.Add(create);
        var appearance = new MenuFlyoutItem { Text = "外观" };
        appearance.Click += (_, _) => AppearanceRequested?.Invoke();
        menu.Items.Add(appearance); more.Flyout = menu;
        IDisposable? menuAirspace = null;
        menu.Opening += (_, _) => menuAirspace = Renderer.SuppressAirspace();
        menu.Closed += (_, _) => { menuAirspace?.Dispose(); menuAirspace = null; };
        var chrome = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { _copy, more, divider, _back } };
        Grid.SetColumn(chrome, 4); toolbar.Children.Add(chrome);
        Children.Add(toolbar);
        Grid.SetRow(_notice, 1); Children.Add(_notice);
        // The HWND is inset from every card edge: rounded XAML chrome/shadow stays outside its airspace.
        var ground = (Border)XamlReader.Load("<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Margin=\"12\" Padding=\"12\" CornerRadius=\"6\" BorderThickness=\"1\" BorderBrush=\"{ThemeResource ControlStrokeColorDefaultBrush}\" Background=\"{ThemeResource LayerFillColorDefaultBrush}\" />");
        ground.Child = Renderer;
        AutomationProperties.SetAutomationId(ground, "OpenCodeTerminalInset");
        Grid.SetRow(ground, 2); Children.Add(ground);
        toolbar.SizeChanged += (_, _) => _detail.Visibility = toolbar.ActualWidth < 720 || _detail.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _detail.Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CaptionSubtleStyleKey);
        AutomationProperties.SetAutomationId(this, "OpenCodePersistentPtyView");
        AutomationProperties.SetAutomationId(_back, "OpenCodePersistentPtyClose");
        AutomationProperties.SetAutomationId(_copy, "OpenCodePersistentPtyCopy");
        ToolTipService.SetToolTip(_back, "关闭 · Esc");
        _copy.IsEnabled = false; ToolTipService.SetToolTip(_copy, "复制");
        _copy.Click += (_, _) => { if (_model is { } model && _selection.Length > 0) CopySelectionRequested?.Invoke(new(model.Context, _selection)); };
        Renderer.SelectionChanged += selection =>
        {
            if (_model?.Context != selection.Context) return;
            _selection = selection.Text; _copy.IsEnabled = _selection.Length > 0 && CopySelectionRequested is not null;
        };
        _back.Click += (_, _) => { if (_model is { } model) DetachRequested?.Invoke(model.Context); };
        _reconnect.Click += (_, _) => { if (_model is { Phase: OpenCodePersistentPtyPhase.Disconnected } model) ReconnectRequested?.Invoke(model.Context); };
        _takeControl.Click += async (_, _) => await ConfirmTakeControlAsync();
        _fit.Click += async (_, _) => await RequestFitAsync();
        _release.Click += (_, _) => { if (_model is { } model) ReleaseControlRequested?.Invoke(model.Context); };
        Unloaded += (_, _) => { if (!IsLoaded) _confirmation?.Hide(); };
        ActualThemeChanged += (_, _) => PaintStatus();
        Render(null);
    }
    internal void Render(OpenCodePersistentPtyPresentation? model)
    {
        if (_model?.Context != model?.Context || model?.Phase is OpenCodePersistentPtyPhase.Connecting or OpenCodePersistentPtyPhase.Replaying)
        { _selection = string.Empty; _copy.IsEnabled = false; }
        if (_model?.Context != model?.Context || model?.Writable != true) Renderer.SuspendInput();
        if (_model?.Context != model?.Context || _model?.Role != model?.Role || model?.NeedsInputConsent != true) _confirmation?.Hide();
        if (_model?.Context != model?.Context) _fitFailure = null;
        _model = model; Visibility = model is null ? Visibility.Collapsed : Visibility.Visible;
        _title.Text = model?.Title ?? string.Empty; ToolTipService.SetToolTip(_title, _title.Text);
        _status.Text = model?.Status ?? string.Empty;
        _detail.Text = model is null ? string.Empty : string.Join(" · ", new[] { model.Directory, model.ControllerLabel }.WherePresent());
        ToolTipService.SetToolTip(_detail, _detail.Text.Length == 0 ? null : _detail.Text);
        if (_detail.Text.Length == 0) _detail.Visibility = Visibility.Collapsed;
        else if (_detail.Parent is FrameworkElement { Parent: FrameworkElement toolbar }) _detail.Visibility = toolbar.ActualWidth is > 0 and < 720 ? Visibility.Collapsed : Visibility.Visible;
        ShowNotice(string.Join(" ", new[] { model?.Notice, _fitFailure }.WherePresent()));
        _back.IsEnabled = model is not null && DetachRequested is not null;
        _takeControl.Content = model?.ConsentAction ?? "接管终端";
        _takeControl.Visibility = model is { Phase: OpenCodePersistentPtyPhase.Live, CanTakeControl: true } && (model.Role == OpenCodePersistentPtyRole.Observer || !model.InputAuthorized)
            ? Visibility.Visible : Visibility.Collapsed;
        _takeControl.IsEnabled = model?.NeedsInputConsent == true && TakeControlRequested is not null;
        _fit.Visibility = model?.Writable == true ? Visibility.Visible : Visibility.Collapsed;
        _fit.IsEnabled = model?.Writable == true;
        _release.Visibility = model is { Phase: OpenCodePersistentPtyPhase.Live, Role: OpenCodePersistentPtyRole.Controller } ? Visibility.Visible : Visibility.Collapsed;
        _release.IsEnabled = model?.Phase == OpenCodePersistentPtyPhase.Live && ReleaseControlRequested is not null;
        _reconnect.Visibility = model?.Phase == OpenCodePersistentPtyPhase.Disconnected ? Visibility.Visible : Visibility.Collapsed;
        _reconnect.IsEnabled = ReconnectRequested is not null;
        AutomationProperties.SetName(_status, _status.Text);
        PaintStatus();
    }
    private void ShowNotice(string text)
    {
        _notice.Message = text;
        _notice.Severity = _model?.Phase is OpenCodePersistentPtyPhase.Unavailable or OpenCodePersistentPtyPhase.Disconnected
            ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
        _notice.IsOpen = text.Length > 0;
    }
    private void PaintStatus()
    {
        if (Application.Current is null) return;
        var key = _model?.Phase switch
        {
            OpenCodePersistentPtyPhase.Live => _model.Writable ? "AccentFillColorDefaultBrush" : "SystemFillColorSuccessBrush",
            OpenCodePersistentPtyPhase.Connecting or OpenCodePersistentPtyPhase.Replaying => "SystemFillColorCautionBrush",
            OpenCodePersistentPtyPhase.Exited => "SystemFillColorNeutralBrush",
            _ => "SystemFillColorCriticalBrush",
        };
        if (Application.Current.Resources.TryGetValue(key, out var brush)) _statusDot.Background = (Brush)brush;
    }
    private async Task RequestFitAsync()
    {
        if (_model is not { Writable: true } model) return;
        try { await Renderer.RequestResizeAsync(model.Context); }
        catch (Exception)
        {
            if (_model?.Context == model.Context)
            { _fitFailure = "暂时无法调整终端大小"; ShowNotice(string.Join(" ", new[] { _model.Notice, _fitFailure }.WherePresent())); }
        }
    }
    private async Task ConfirmTakeControlAsync()
    {
        if (_confirmation is not null || _model is not { NeedsInputConsent: true } model
            || TakeControlRequested is null) return;
        var dialog = _confirmation = new ContentDialog { XamlRoot = XamlRoot, Title = model.ConsentAction + "？",
            Content = new TextBlock { Text = model.ConsentExplanation, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = model.ConsentAction, CloseButtonText = "继续只读查看", DefaultButton = ContentDialogButton.Close };
        using var airspace = Renderer.SuppressAirspace();
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && IsLoaded && _model?.Context == model.Context
                && _model.NeedsInputConsent && _model.Role == model.Role)
                TakeControlRequested?.Invoke(model.Context);
        }
        finally { if (ReferenceEquals(_confirmation, dialog)) _confirmation = null; }
    }

    private static Button IconButton(string glyph, string name)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 14 },
            Width = 34, Height = 32, MinWidth = 0, Padding = new Thickness(0), BorderThickness = new Thickness(0) };
        if (Application.Current?.Resources.TryGetValue("OpenCodeTransparentToolButtonStyle", out var style) == true && style is Style quiet) button.Style = quiet;
        AutomationProperties.SetName(button, name);
        return button;
    }
    private static Button TextButton(string text)
        => new() { Content = text, Height = 32, FontSize = 13, Padding = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private static void Theme(FrameworkElement target, string property, string key)
        => target.Style = (Style)XamlReader.Load("<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\""
            + target.GetType().Name + "\"><Setter Property=\"" + property + "\" Value=\"{ThemeResource " + key + "}\"/></Style>");
}

internal static class OpenCodePersistentPtyViewText
{
    internal static System.Collections.Generic.IEnumerable<string> WherePresent(this string?[] values)
    {
        foreach (var value in values) if (!string.IsNullOrEmpty(value)) yield return value;
    }
}
