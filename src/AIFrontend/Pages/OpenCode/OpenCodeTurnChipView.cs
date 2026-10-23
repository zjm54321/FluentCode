using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Native compact turn metadata buttons; catalog enrichment never changes row height.</summary>
internal sealed class OpenCodeTurnChipView
{
    private readonly Button _button = CreateButton("OpenCodeModelSwitch");
    private OpenCodeMessage _message = null!;
    private CancellationTokenSource? _read;
    private bool _disposed;

    internal OpenCodeTurnChipView(OpenCodeMessage message)
    {
        _button.Loaded += (_, _) => Enrich();
        _button.Unloaded += (_, _) => _read?.Cancel();
        Update(message);
    }

    internal FrameworkElement Element => _button;
    internal static Button CreateButton(string automationId)
    {
        var button = new Button
        {
            Height = 24, MinHeight = 24, MinWidth = 0, Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAutomationId(button, automationId);
        return button;
    }

    internal void Update(OpenCodeMessage message)
    {
        _message = message;
        _read?.Cancel();
        Render(null);
        if (_button.IsLoaded) Enrich();
    }

    private void Render(OpenCodeModelInfo? model)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = "已切换模型", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var name = string.IsNullOrWhiteSpace(model?.Name) ? _message.Model?.Id ?? "（未提供）" : model.Name;
        if (model is not null && !string.IsNullOrWhiteSpace(model.Name)
            && OpenCodeModelPresentation.Brand(model.Id, model.Name) != OpenCodeModelBrand.Neutral)
            panel.Children.Add(OpenCodeModelLogo.Create(model.Id, model.Name));
        panel.Children.Add(new TextBlock { Text = name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        _button.Content = panel;
        AutomationProperties.SetName(_button, "已切换模型 " + name);
    }

    private async void Enrich()
    {
        _read?.Cancel();
        _read?.Dispose();
        var read = _read = new CancellationTokenSource();
        var token = read.Token;
        var message = _message;
        var scope = OpenCodeImageContext.Find(_button);
        if (_disposed || scope is not { IsCurrent: true }) return;
        try
        {
            var models = await OpenCodeTurnChipSession.For(scope).Models(token);
            if (!token.IsCancellationRequested && !_disposed && ReferenceEquals(message, _message)
                && scope.IsCurrent && ReferenceEquals(scope, OpenCodeImageContext.Find(_button)))
                Render(models.Data?.FirstOrDefault(m => m.Id == message.Model?.Id && m.ProviderId == message.Model?.ProviderId));
        }
        catch (Exception) { /* Unknown/unavailable catalog keeps the opaque model ID, without a logo. */ }
    }

    internal void Dispose() { _disposed = true; _read?.Cancel(); _read?.Dispose(); _read = null; }
}

/// <summary>Reuse the existing explicit, generation-fenced session subtree binding (also used by child windows).</summary>
internal sealed class OpenCodeTurnChipSession
{
    private static readonly ConditionalWeakTable<OpenCodeMarkdownImageScope, OpenCodeTurnChipSession> Sessions = new();
    private readonly OpenCodeMarkdownImageScope _scope;
    private Task<OpenCodeSessionInfo>? _session;
    private Task<OpenCodeModelsResponse>? _models;
    private OpenCodeTurnChipSession(OpenCodeMarkdownImageScope scope) => _scope = scope;
    internal static OpenCodeTurnChipSession For(OpenCodeMarkdownImageScope scope) => Sessions.GetValue(scope, s => new(s));

    internal async Task<string> Directory(CancellationToken token)
    {
        if (!_scope.IsCurrent) throw new OperationCanceledException(token);
        if (_session is { IsCanceled: true } or { IsFaulted: true }) _session = null;
        var info = await (_session ??= _scope.Identity.Client.GetSessionInfoAsync(_scope.Identity.SessionId, token)).WaitAsync(token);
        if (!_scope.IsCurrent) throw new OperationCanceledException(token);
        if (info.Id != _scope.Identity.SessionId || string.IsNullOrWhiteSpace(info.Location?.Directory))
            throw new InvalidOperationException("无法读取：会话未提供有效目录。");
        return info.Location.Directory;
    }

    internal async Task<OpenCodeModelsResponse> Models(CancellationToken token)
    {
        var directory = await Directory(token);
        if (_models is { IsCanceled: true } or { IsFaulted: true }) _models = null;
        return await (_models ??= _scope.Identity.Client.ListModelsAsync(directory, token)).WaitAsync(token);
    }
}
