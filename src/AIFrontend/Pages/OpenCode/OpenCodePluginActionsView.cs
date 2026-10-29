using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Small current-session fragment, intended beside the existing preset metadata.</summary>
internal sealed class OpenCodePluginActionsView : StackPanel
{
    private readonly TextBlock _preset = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Visibility = Visibility.Collapsed };
    private readonly Button _apply = new() { Content = "应用预设…", MinHeight = 32 };
    private readonly Button _clear = new() { Content = "清除加密内容…", MinHeight = 32 };
    private readonly Button _acknowledge = new() { Content = "确认风险并解除本地保护…", MinHeight = 32, Visibility = Visibility.Collapsed };
    private readonly TextBlock _protectionNotice = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly ProgressRing _pending = new() { Width = 16, Height = 16, MinWidth = 0, MinHeight = 0, Visibility = Visibility.Collapsed };
    private OpenCodePluginActionsPresentation? _model;
    private ContentDialog? _dialog;
    internal event Action<OpenCodePresetApplyRequest>? PresetApplyRequested;
    internal event Action<OpenCodePluginActionContext>? ClearEncryptedRequested;
    internal event Action<OpenCodePluginProtectionAcknowledgement>? ProtectionAcknowledgementRequested;

    internal OpenCodePluginActionsView(bool showPresetSummary = true)
    {
        Spacing = 8;
        _preset.Visibility = showPresetSummary ? Visibility.Visible : Visibility.Collapsed;
        Children.Add(_preset);
        Children.Add(new TextBlock { Text = "预设仅影响之后新建的子会话", TextWrapping = TextWrapping.Wrap, FontSize = 12,
            Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CaptionSubtleStyleKey) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_apply); actions.Children.Add(_pending);
        Children.Add(actions); Children.Add(_clear); Children.Add(_notice); Children.Add(_protectionNotice); Children.Add(_acknowledge);
        _clear.Style = OpenCodeInteractionUi.TryStyle("SubtleButtonStyle");
        _clear.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetAutomationId(this, "OpenCodePluginSessionActions");
        AutomationProperties.SetName(_apply, "按完整名称应用会话预设");
        AutomationProperties.SetName(_clear, "清除当前会话的加密内容，需要确认");
        _apply.Click += async (_, _) => await ApplyDialogAsync();
        _clear.Click += async (_, _) => await ClearDialogAsync();
        _acknowledge.Click += async (_, _) => await AcknowledgeDialogAsync();
        AutomationProperties.SetName(_acknowledge, "确认上次插件操作结果仍未知，并仅解除该操作的本地保护");
        Unloaded += (_, _) => { if (!IsLoaded) _dialog?.Hide(); };
        Render(null);
    }

    internal void Render(OpenCodePluginActionsPresentation? model)
    {
        if (_model?.Context != model?.Context || _model?.UnknownProtection?.OperationId != model?.UnknownProtection?.OperationId) _dialog?.Hide();
        _model = model; Visibility = model is null ? Visibility.Collapsed : Visibility.Visible;
        _preset.Text = model?.PresetSummary ?? string.Empty;
        _apply.IsEnabled = model is { CanApplyPreset: true, Pending: false, UnknownProtection: null } && PresetApplyRequested is not null;
        _clear.IsEnabled = model is { CanClearEncrypted: true, Pending: false, UnknownProtection: null } && ClearEncryptedRequested is not null;
        _pending.IsActive = model?.Pending == true;
        _pending.Visibility = _pending.IsActive ? Visibility.Visible : Visibility.Collapsed;
        _notice.Text = model?.Notice ?? string.Empty;
        if (model?.ClearResult is { } counts) _notice.Text += (_notice.Text.Length == 0 ? string.Empty : "\n") + counts.Summary;
        _notice.Visibility = _notice.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(_notice, _notice.Text);
        var protection = model?.UnknownProtection;
        _protectionNotice.Text = protection is null ? string.Empty : "上次“" + protection.ActionLabel + "”的服务器结果仍未知。刷新不能确认该操作结果；可确认风险后，仅解除这次操作的本地保护。";
        _protectionNotice.Visibility = _acknowledge.Visibility = protection is null ? Visibility.Collapsed : Visibility.Visible;
        _acknowledge.IsEnabled = model is not null && protection is not null && ProtectionAcknowledgementRequested is not null
            && OpenCodePluginActionsPolicy.CanAcknowledge(model, new(model.Context, protection.OperationId));
    }

    private bool Owns(OpenCodePluginActionContext context) => IsLoaded && _model?.Context == context && _model.Pending == false;
    private async Task ApplyDialogAsync()
    {
        if (_dialog is not null || _model is not { CanApplyPreset: true, Pending: false, UnknownProtection: null } model || PresetApplyRequested is null) return;
        var name = new TextBox { Header = "预设完整名称", PlaceholderText = "输入服务器配置中的名称", AcceptsReturn = false };
        AutomationProperties.SetName(name, "预设完整名称，按原样发送");
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = "应用到当前会话，仅影响之后新建的子会话。名称由服务器核对；不会修改全局设置。", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(name);
        var dialog = _dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "应用会话预设", Content = content,
            PrimaryButtonText = "应用", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, IsPrimaryButtonEnabled = false };
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = OpenCodePluginActionsPolicy.CanSubmitExactName(name.Text);
        try
        {
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && Owns(model.Context) && _model is { CanApplyPreset: true, UnknownProtection: null }
                && OpenCodePluginActionsPolicy.CanSubmitExactName(name.Text)) PresetApplyRequested?.Invoke(new(model.Context, name.Text));
        }
        finally { if (ReferenceEquals(_dialog, dialog)) _dialog = null; }
    }

    private async Task ClearDialogAsync()
    {
        if (_dialog is not null || _model is not { CanClearEncrypted: true, Pending: false, UnknownProtection: null } model || ClearEncryptedRequested is null) return;
        var dialog = _dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "清除当前会话的加密内容？",
            Content = new TextBlock { Text = "服务器会先停止当前会话的执行，再清理消息与事件中的加密字段。聊天正文和本地文件保留；应用内不提供撤销。", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "确认清除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && Owns(model.Context) && _model is { CanClearEncrypted: true, UnknownProtection: null })
                ClearEncryptedRequested?.Invoke(model.Context);
        }
        finally { if (ReferenceEquals(_dialog, dialog)) _dialog = null; }
    }

    private async Task AcknowledgeDialogAsync()
    {
        if (_dialog is not null || _model is not { UnknownProtection: { } protection } model || ProtectionAcknowledgementRequested is null) return;
        var request = new OpenCodePluginProtectionAcknowledgement(model.Context, protection.OperationId);
        if (!OpenCodePluginActionsPolicy.CanAcknowledge(model, request)) return;
        var dialog = _dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "解除这次操作的本地保护？",
            Content = new TextBlock { Text = "上次“" + protection.ActionLabel + "”在服务器上是否完成仍未知。确认只解除这次操作的本地保护，不会重试、撤销或确认服务器结果；之后仍需自行检查状态。", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "理解风险，解除本地保护", CloseButtonText = "保留保护", DefaultButton = ContentDialogButton.Close };
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && IsLoaded && OpenCodePluginActionsPolicy.CanAcknowledge(_model, request))
            {
                _acknowledge.IsEnabled = false;
                ProtectionAcknowledgementRequested?.Invoke(request);
                // Only a subsequent authoritative host model may remove the notice/protection.
            }
        }
        finally { if (ReferenceEquals(_dialog, dialog)) _dialog = null; }
    }
}
