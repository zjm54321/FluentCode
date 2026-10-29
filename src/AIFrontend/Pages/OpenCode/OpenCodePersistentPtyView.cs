using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Center-terminal presentation. Viewing is read-only; closing only requests detach.</summary>
internal sealed class OpenCodePersistentPtyView : Grid
{
    internal OpenCodePersistentPtyRenderer Renderer { get; } = new();
    private readonly TextBlock _title = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _detail = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Button _back = new() { Content = new FontIcon { Glyph = "\uE72B", FontSize = 16 }, Width = 36, Height = 36, MinWidth = 0, Padding = new Thickness(0) };
    private readonly Button _copy = new() { Content = new FontIcon { Glyph = "\uE8C8", FontSize = 14 }, Width = 32, Height = 32, MinWidth = 0, Padding = new Thickness(0), IsEnabled = false };
    private string _selection = string.Empty;
    private readonly Button _takeControl = new() { Content = "接管终端", MinHeight = 32 };
    private readonly Button _fit = new() { Content = "适应窗口", MinHeight = 32, Visibility = Visibility.Collapsed };
    private readonly Button _release = new() { Content = "回到只读", MinHeight = 32, Visibility = Visibility.Collapsed };
    private readonly Button _reconnect = new() { Content = "重新连接", MinHeight = 32, Visibility = Visibility.Collapsed };
    private OpenCodePersistentPtyPresentation? _model;
    private ContentDialog? _confirmation;
    internal event Action<OpenCodePersistentPtyViewContext>? TakeControlRequested;
    internal event Action<OpenCodePersistentPtyViewContext>? ReleaseControlRequested;
    internal event Action<OpenCodePersistentPtyViewContext>? DetachRequested;
    internal event Action<OpenCodePersistentPtyViewContext>? ReconnectRequested;
    internal event Action<OpenCodePersistentPtySelection>? CopySelectionRequested;

    internal OpenCodePersistentPtyView()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid { ColumnSpacing = 12, RowSpacing = 6, Padding = new Thickness(12, 8, 12, 8) };
        for (var row = 0; row < 3; row++) header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        header.Children.Add(_back); Grid.SetColumn(_title, 1); header.Children.Add(_title);
        Grid.SetColumn(_copy, 2); header.Children.Add(_copy);
        _status.TextWrapping = TextWrapping.Wrap; Grid.SetRow(_status, 1); Grid.SetColumnSpan(_status, 3); header.Children.Add(_status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { _takeControl, _fit, _release, _reconnect } };
        Grid.SetRow(actions, 2); Grid.SetColumnSpan(actions, 3); header.Children.Add(actions);
        Children.Add(header);
        _detail.Margin = new Thickness(16, 0, 16, 8); Grid.SetRow(_detail, 1); Children.Add(_detail);
        Grid.SetRow(Renderer, 2); Children.Add(Renderer);
        _title.Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CodeStyleKey);
        _detail.Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CaptionSubtleStyleKey);
        AutomationProperties.SetAutomationId(this, "OpenCodePersistentPtyView");
        AutomationProperties.SetName(_back, "返回会话，断开查看但不结束终端");
        ToolTipService.SetToolTip(_back, "返回会话 · 不结束终端");
        AutomationProperties.SetName(_copy, "复制选中的终端文本"); ToolTipService.SetToolTip(_copy, "复制选中文本；不会向终端发送 Ctrl+C");
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
        ToolTipService.SetToolTip(_release, "断开当前控制连接并重新只读连接；不结束终端，也不保证下一控制者");
        ToolTipService.SetToolTip(_fit, "明确请求将受控终端调整为当前窗口大小");
        Unloaded += (_, _) => { if (!IsLoaded) _confirmation?.Hide(); };
        Render(null);
    }
    internal void Render(OpenCodePersistentPtyPresentation? model)
    {
        if (_model?.Context != model?.Context || model?.Phase is OpenCodePersistentPtyPhase.Connecting or OpenCodePersistentPtyPhase.Replaying)
        { _selection = string.Empty; _copy.IsEnabled = false; }
        if (_model?.Context != model?.Context || model?.Writable != true) Renderer.SuspendInput();
        if (_model?.Context != model?.Context || _model?.Role != model?.Role || model?.NeedsInputConsent != true) _confirmation?.Hide();
        _model = model; Visibility = model is null ? Visibility.Collapsed : Visibility.Visible;
        _title.Text = model?.Title ?? string.Empty; ToolTipService.SetToolTip(_title, _title.Text);
        _status.Text = model?.Status ?? string.Empty;
        _detail.Text = model is null ? string.Empty : string.Join(" · ", new[] { model.Directory, model.ControllerLabel, model.Notice }.WherePresent());
        _detail.Visibility = _detail.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _back.IsEnabled = model is not null && DetachRequested is not null;
        _takeControl.Content = model?.ConsentAction ?? "接管终端";
        _takeControl.Visibility = model is { Phase: OpenCodePersistentPtyPhase.Live } && (model.Role == OpenCodePersistentPtyRole.Observer || !model.InputAuthorized)
            ? Visibility.Visible : Visibility.Collapsed;
        _takeControl.IsEnabled = model?.NeedsInputConsent == true && TakeControlRequested is not null;
        _fit.Visibility = model?.Writable == true ? Visibility.Visible : Visibility.Collapsed;
        _fit.IsEnabled = model?.Writable == true;
        _release.Visibility = model is { Phase: OpenCodePersistentPtyPhase.Live, Role: OpenCodePersistentPtyRole.Controller } ? Visibility.Visible : Visibility.Collapsed;
        _release.IsEnabled = model?.Phase == OpenCodePersistentPtyPhase.Live && ReleaseControlRequested is not null;
        _reconnect.Visibility = model?.Phase == OpenCodePersistentPtyPhase.Disconnected ? Visibility.Visible : Visibility.Collapsed;
        _reconnect.IsEnabled = ReconnectRequested is not null;
        AutomationProperties.SetName(_status, _status.Text);
    }
    private async Task RequestFitAsync()
    {
        if (_model is not { Writable: true } model) return;
        try { await Renderer.RequestResizeAsync(model.Context); }
        catch (Exception)
        {
            if (_model?.Context == model.Context)
            { _detail.Text = "暂时无法请求调整终端大小，请检查连接状态。"; _detail.Visibility = Visibility.Visible; }
        }
    }
    private async Task ConfirmTakeControlAsync()
    {
        if (_confirmation is not null || _model is not { NeedsInputConsent: true } model
            || TakeControlRequested is null) return;
        var dialog = _confirmation = new ContentDialog { XamlRoot = XamlRoot, Title = model.ConsentAction + "？",
            Content = new TextBlock { Text = model.ConsentExplanation, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = model.ConsentAction, CloseButtonText = "继续只读查看", DefaultButton = ContentDialogButton.Close };
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && IsLoaded && _model?.Context == model.Context
                && _model.NeedsInputConsent && _model.Role == model.Role)
                TakeControlRequested?.Invoke(model.Context);
        }
        finally { if (ReferenceEquals(_confirmation, dialog)) _confirmation = null; }
    }
}

internal static class OpenCodePersistentPtyViewText
{
    internal static System.Collections.Generic.IEnumerable<string> WherePresent(this string?[] values)
    {
        foreach (var value in values) if (!string.IsNullOrEmpty(value)) yield return value;
    }
}
