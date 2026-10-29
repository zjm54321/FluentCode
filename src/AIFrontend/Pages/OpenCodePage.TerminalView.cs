using System;
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
    private readonly OpenCodeTerminalState _terminalState = new();
    private OpenCodeTerminalView? _terminalView;
    private TreeBinding? _terminalBinding;
    private CancellationTokenSource? _terminalCts;
    private DispatcherTimer? _terminalTimer;
    private Control? _terminalReturnFocus;
    private bool _terminalInitialized;
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

    private void TerminalViewHost_Loaded(object sender, RoutedEventArgs e)
    {
        if (_terminalInitialized) return;
        _terminalInitialized = true;
        // Page pause captures the verified shell before the tree resets; Loaded's fresh tree
        // reopens that shell. Only final release discards the retained terminal identity.
        PageReleased += (_, _) => { _pausedTerminal = null; CloseTerminalView(restoreFocus: false); };
        // Also works when focus has moved to a dock while the terminal is open.
        PageRoot.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, key) =>
        {
            if (_terminalState.ShellId is null || key.Key != VirtualKey.Escape) return;
            key.Handled = true;
            CloseTerminalView();
        }), true);
    }

    private void OpenTerminalView(string shellId)
    {
        var binding = CaptureTreeBinding();
        if (binding is null || _treeDirectory is null || !_treeShells.TryGetValue(shellId, out var shell)) return;
        ClosePersistentPtyView(restoreFocus: false);
        var returnFocus = _terminalState.ShellId is null ? FocusManager.GetFocusedElement(XamlRoot) as Control : _terminalReturnFocus;
        CloseTerminalView(restoreFocus: false);
        _terminalReturnFocus = returnFocus;
        _terminalBinding = binding;
        _terminalCts = CancellationTokenSource.CreateLinkedTokenSource(_pageCts!.Token, binding.Source.RevocationToken);
        _terminalState.Open(shell);
        if (_terminalView is null)
        {
            _terminalView = new OpenCodeTerminalView();
            _terminalView.CloseRequested += () => CloseTerminalView();
            TerminalViewHost.Children.Add(_terminalView);
        }
        ConversationSurface.Visibility = Visibility.Collapsed;
        TerminalViewHost.Visibility = Visibility.Visible;
        _terminalView.Visibility = Visibility.Visible;
        _terminalView.Render(_terminalState);
        _terminalView.Begin();
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
        TerminalViewHost.Visibility = Visibility.Collapsed;
        ConversationSurface.Visibility = Visibility.Visible;
        if (restoreFocus && _terminalReturnFocus?.Focus(FocusState.Programmatic) != true) Composer.Focus(FocusState.Programmatic);
        _terminalReturnFocus = null;
    }

    private void TerminalOutputTick(object? sender, object e)
    {
        if (_terminalBinding is not { } binding || !IsTreeCurrent(binding)) { CloseTerminalView(restoreFocus: false); return; }
        if (_terminalState.Running || _terminalState.HasMore) _ = ReadTerminalOutputAsync();
        // Keep the cheap binding check alive even after exit, so a session/source switch closes the view.
    }

    private async Task ReadTerminalOutputAsync()
    {
        if (_terminalReading || _terminalBinding is not { } binding || !IsTreeCurrent(binding)
            || _terminalState.ShellId is not { } id || _treeDirectory is not { } directory || _terminalCts is null) return;
        _terminalReading = true;
        var ct = _terminalCts.Token;
        var version = _terminalVersion;
        bool Current() => !ct.IsCancellationRequested && version == _terminalVersion && IsTreeCurrent(binding) && _terminalState.ShellId == id;
        try
        {
            var info = await TreeReadAsync(binding, () => binding.Client.GetTreeShellAsync(id, directory, ct), ct);
            if (!Current()) return;
            // The ID was supplied by the current session's already-verified tree projection.
            // Foreground message-backed jobs need not repeat metadata.sessionID on GET/shell.
            _terminalState.ApplyInfo(info);
            do
            {
                var page = await TreeReadAsync(binding, () => binding.Client.ReadTreeShellOutputAsync(id, directory, _terminalState.Cursor, ct), ct);
                if (!Current()) return;
                _terminalState.ApplyOutput(page);
                _terminalView!.Render(_terminalState);
                // Drain retained pages after exit too; no dependence on the running-only shell list.
            } while (_terminalState.HasMore && Current());
        }
        catch (OperationCanceledException) { }
        catch (OpenCodeApiException error) when (error.StatusCode == 404)
        {
            if (!Current()) return;
            _terminalState.UseRecordedOutput();
            _terminalView!.Render(_terminalState);
        }
        catch (OpenCodeException)
        {
            if (!Current()) return;
            _terminalState.Notice = "输出读取不可用（任务可能已被后端移除）；已读取的输出予以保留";
            _terminalView!.Render(_terminalState);
        }
        finally
        {
            _terminalReading = false;
            if (version != _terminalVersion && _terminalState.ShellId is not null) _ = ReadTerminalOutputAsync();
        }
    }
}
