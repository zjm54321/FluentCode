using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private readonly OpenCodeShellTerminalState _terminalState = new();
    private OpenCodePersistentPtyView? _terminalView;
    private OpenCodeShellTerminalAdapter? _shellTerminalAdapter;
    private OpenCodePersistentPtyViewContext? _shellTerminalContext;
    private OpenCodeShellTerminalRequest? _shellTerminalRequest;
    private string? _shellOutputApiId;
    private TreeBinding? _terminalBinding;
    private CancellationTokenSource? _terminalCts;
    private DispatcherTimer? _terminalTimer;
    private Control? _terminalReturnFocus;
    private bool _terminalReading;
    private long _terminalVersion;
    private (TreeBinding Binding, string Directory, OpenCodeTreeShell Shell)? _pausedTerminal;

    private void PauseTerminalView()
    {
        if (_terminalBinding is { } binding && _treeDirectory is { } directory
            && _terminalState.ShellId is { } id && _treeShells.TryGetValue(id, out var shell))
            _pausedTerminal = (binding, directory, shell);
        CloseTerminalView(restoreFocus: false);
    }

    private void ResumeTerminalView()
    {
        if (_pausedTerminal is not { } paused) return;
        _pausedTerminal = null;
        if (_homeActive || _newSessionDraftActive || CaptureTreeBinding() is not { } current
            || !ReferenceEquals(paused.Binding.Source, current.Source) || paused.Binding.Revision != current.Revision
            || paused.Binding.SessionId != current.SessionId || paused.Directory != _treeDirectory) return;
        // /shell lists only running commands. A command which finished while hidden still has
        // its detail/output route, so retain its verified identity and let that route reconcile it.
        _treeShells.TryAdd(paused.Shell.Id, paused.Shell);
        OpenTerminalView(paused.Shell.Id);
    }

    private void InitializeShellTerminal()
    {
        // Page pause captures the verified shell before the tree resets; Loaded's fresh tree
        // reopens that shell. Only final release discards the retained terminal identity.
        PageReleased += (_, _) => { _pausedTerminal = null; CloseTerminalView(restoreFocus: false); };
    }

    private void OpenTerminalView(string shellId)
    {
        var binding = CaptureTreeBinding();
        if (binding is null || _treeDirectory is null || !_treeShells.TryGetValue(shellId, out var shell)) return;
        OpenShellTerminal(binding, shell, null);
    }

    private void OpenTerminalView(OpenCodeShellTerminalRequest request)
    {
        var binding = CaptureTreeBinding();
        if (binding is null || _treeDirectory is null) return;
        var shell = request.ShellId is { } id && _treeShells.TryGetValue(id, out var known) ? known
            : new OpenCodeTreeShell(request.ShellId ?? "recorded:" + Guid.NewGuid().ToString("N"), request.Command, _treeDirectory,
                OpenCodeTreeShellKind.Foreground, request.Running ? OpenCodeTreeStatus.Running : OpenCodeTreeStatus.Unknown, null, request.Output);
        shell = shell with { Command = request.Command, Output = shell.Completed ? shell.Output : request.Output };
        OpenShellTerminal(binding, shell, request);
    }

    private void OpenShellTerminal(TreeBinding binding, OpenCodeTreeShell shell, OpenCodeShellTerminalRequest? request)
    {
        ClosePersistentPtyView(restoreFocus: false);
        var returnFocus = _terminalState.ShellId is null ? FocusManager.GetFocusedElement(XamlRoot) as Control : _terminalReturnFocus;
        CloseTerminalView(restoreFocus: false);
        _terminalReturnFocus = returnFocus;
        _terminalBinding = binding;
        _shellTerminalRequest = request;
        _shellOutputApiId = request is null ? shell.Id : request.ShellId;
        _terminalCts = CancellationTokenSource.CreateLinkedTokenSource(_pageCts!.Token, binding.Source.RevocationToken);
        _terminalState.Open(shell);
        var view = _terminalView = new OpenCodePersistentPtyView();
        var context = _shellTerminalContext = new(Guid.NewGuid(), _terminalVersion, binding.SessionId, shell.Id);
        view.Renderer.UseShellViewport();
        _shellTerminalAdapter = new(view.Renderer, context);
        view.DetachRequested += _ => CloseTerminalView();
        view.CopySelectionRequested += selection =>
        {
            if (_shellTerminalContext != selection.Context) return;
            try { var data = new Windows.ApplicationModel.DataTransfer.DataPackage(); data.SetText(selection.Text); Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data); }
            catch (Exception) { }
        };
        BindTerminalAppearance(view);
        ShowPersistentPtyOverlay(view, shell.Id);
        _ = RenderShellTerminalAsync(_terminalCts.Token); // Stored full output is immediately available, before REST.
        DismissSidebarDrawers();
        _terminalTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _terminalTimer.Tick -= TerminalOutputTick;
        _terminalTimer.Tick += TerminalOutputTick;
        _terminalTimer.Start();
        _ = ReadTerminalOutputAsync();
    }

    private void CloseTerminalView(bool restoreFocus = true)
    {
        _terminalVersion++;
        _terminalTimer?.Stop();
        _terminalCts?.Cancel();
        _terminalCts?.Dispose();
        _terminalCts = null;
        _terminalBinding = null;
        _terminalState.Close();
        _shellTerminalAdapter = null; _shellTerminalContext = null;
        _shellTerminalRequest = null; _shellOutputApiId = null;
        if (_terminalView is { } view)
        {
            HidePersistentPtyOverlay(view);
            view.Renderer.Dispose();
            _terminalView = null;
        }
        if (restoreFocus && _terminalReturnFocus?.Focus(FocusState.Programmatic) != true) Composer.Focus(FocusState.Programmatic);
        _terminalReturnFocus = null;
    }

    private void PaintShellTerminal()
    {
        if (_terminalView is not { } view || _shellTerminalContext is not { } context) return;
        view.Render(new(context, _terminalState.Command, _terminalState.Cwd,
            _terminalState.Running ? OpenCodePersistentPtyPhase.Live : OpenCodePersistentPtyPhase.Exited,
            OpenCodePersistentPtyRole.Observer, _terminalState.TimeText, false, false, _terminalState.Notice, _terminalState.StatusText));
    }

    private async Task RenderShellTerminalAsync(CancellationToken ct)
    {
        PaintShellTerminal();
        if (_shellTerminalAdapter is not { } adapter) return;
        try { await adapter.RenderAsync(_terminalState.Output, ct, _terminalState.Command); }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!ct.IsCancellationRequested && ReferenceEquals(adapter, _shellTerminalAdapter))
            { _terminalTimer?.Stop(); _terminalState.Notice = "原生终端不可用"; PaintShellTerminal(); }
        }
    }

    private void UpdateShellTerminalTranscript(IReadOnlyList<OpenCodeMessage> messages)
    {
        if (_shellTerminalRequest is not { } request || _terminalBinding is not { } binding || !IsTreeCurrent(binding)
            || _terminalCts is null) return;
        if (_shellOutputApiId is { } shellId && messages.Select(OpenCodeAgentTreeProjection.Shell)
            .LastOrDefault(shell => shell is { Completed: true } && shell.Id == shellId) is { } completion)
        {
            _terminalState.ApplyCompletion(completion);
            _ = RenderShellTerminalAsync(_terminalCts.Token);
            return;
        }
        var message = messages.FirstOrDefault(item => item.Id == request.MessageId);
        if (message is null) return;
        if (request.PartId is { Length: > 0 } partId)
        {
            var part = message.Content?.FirstOrDefault(item => item.Id == partId);
            if (part is null) return;
            var model = OpenCodeToolRowPresentation.Build(part);
            if (model.Kind != OpenCodeToolKind.Shell) return;
            _terminalState.UpdateRecordedOutput(model.Command ?? model.Target, model.OutputText ?? string.Empty, model.IsActive, _shellOutputApiId is null);
        }
        else _terminalState.UpdateRecordedOutput(message.Command ?? request.Command, message.Output?.Output ?? request.Output,
            message.Status == OpenCodeShellStatuses.Running, _shellOutputApiId is null);
        _ = RenderShellTerminalAsync(_terminalCts.Token);
    }

    private void TerminalOutputTick(object? sender, object e)
    {
        if (_terminalBinding is not { } binding || !IsTreeCurrent(binding)) { CloseTerminalView(restoreFocus: false); return; }
        if (_terminalState.Running || _terminalState.HasMore) _ = ReadTerminalOutputAsync();
        // Keep the cheap binding check alive even after exit, so a session/source switch closes the view.
    }

    private async Task ReadTerminalOutputAsync()
    {
        if (_shellOutputApiId is not { } apiId) return; // Tool-part-only output is updated by accepted transcript snapshots, never a made-up REST ID.
        if (_terminalReading || _terminalBinding is not { } binding || !IsTreeCurrent(binding)
            || _terminalState.ShellId is not { } id || _treeDirectory is not { } directory || _terminalCts is null) return;
        _terminalReading = true;
        var ct = _terminalCts.Token;
        var version = _terminalVersion;
        bool Current() => !ct.IsCancellationRequested && version == _terminalVersion && IsTreeCurrent(binding) && _terminalState.ShellId == id;
        try
        {
            await OpenCodeShellTerminalReader.ReadAsync(
                () => TreeReadAsync(binding, () => binding.Client.GetTreeShellAsync(apiId, directory, ct), ct),
                cursor => TreeReadAsync(binding, () => binding.Client.ReadTreeShellOutputAsync(apiId, directory, cursor, ct), ct),
                _terminalState, Current, () => RenderShellTerminalAsync(ct), ct);
        }
        catch (OperationCanceledException) { }
        catch (OpenCodeApiException error) when (error.StatusCode == 404)
        {
            if (!Current()) return;
            if (_treeShells.TryGetValue(apiId, out var completed) && completed.Completed) _terminalState.ApplyCompletion(completed);
            else _terminalState.UseRecordedOutput();
            await RenderShellTerminalAsync(ct);
        }
        catch (OpenCodeException)
        {
            if (!Current()) return;
            _terminalState.Notice = "输出读取不可用（任务可能已被后端移除）；已读取的输出予以保留";
            await RenderShellTerminalAsync(ct);
        }
        finally
        {
            _terminalReading = false;
            if (version != _terminalVersion && _terminalState.ShellId is not null) _ = ReadTerminalOutputAsync();
        }
    }
}
