using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Configuration;
using Microsoft.UI.Xaml;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private OpenCodeWslQuickAccessView? _wslQuickAccessView;
    private readonly IWslQuickAccessLauncher _wslQuickAccessLauncher = new WindowsWslQuickAccessLauncher();
    private readonly IWslHostEditorEnvironment _wslEditorHost = new WindowsWslHostEditorEnvironment();
    private IReadOnlyList<WslHostEditor> _wslEditors = Array.Empty<WslHostEditor>();
    private readonly WslQuickAccessEditorPreference _wslEditorPreference = new(
        System.IO.Path.Combine(AppDataPaths.GetRootDirectory(null), "opencode-wsl-quick-access-editor.txt"));
    private string? _selectedWslEditor;
    private string? _wslHomeDistribution;
    private object? _wslHomeSource;
    private string? _wslAuthoritativeHome;
    private Task<string?>? _wslHomeRead;
    private CancellationTokenSource? _wslHomeCts;

    private void InitializeWslQuickAccess()
    {
        _wslQuickAccessView = new();
        WslQuickAccessSlot.Children.Add(_wslQuickAccessView);
        WslQuickAccessNoteSlot.Children.Add(_wslQuickAccessView.Note);
        _selectedWslEditor = _wslEditorPreference.Load();
        _wslQuickAccessView.ExplorerRequested += async (_, _) => await LaunchWslQuickAccessAsync(null);
        _wslQuickAccessView.EditorRequested += async (_, e) => await LaunchWslQuickAccessAsync(e.Name);
        _wslQuickAccessView.EditorMenuRequested += (_, _) => DetectWslEditors();
        Loaded += (_, _) =>
        {
            _homeSettings!.Changed += WslQuickAccessSettingsChanged;
            DetectWslEditors();
        };
        Unloaded += (_, _) =>
        {
            _homeSettings!.Changed -= WslQuickAccessSettingsChanged;
            ResetWslQuickAccessHome();
            WslQuickAccessSlot.Visibility = Visibility.Collapsed;
            WslQuickAccessNoteSlot.Visibility = Visibility.Collapsed;
        };
    }

    private void WslQuickAccessSettingsChanged(object? sender, AppSettingsChangedEventArgs e)
        => DispatcherQueue.TryEnqueue(RefreshWslQuickAccess);

    private void DetectWslEditors()
    {
        _wslEditors = WslHostEditorDetection.Detect(_wslEditorHost);
        RefreshWslQuickAccess();
    }

    private WslQuickAccessState CurrentWslQuickAccess()
    {
        var settings = _homeSettings?.Current?.OpenCode;
        var connected = _pageActive && _connection == ConnectionState.Connected && _client is not null
            && _source is { IsRevoked: false } && !_homeActive;
        var home = ReferenceEquals(_wslHomeSource, _source) && _wslHomeDistribution == settings?.WslDistribution
            ? _wslAuthoritativeHome : null;
        return OpenCodeWslQuickAccess.Resolve(settings?.Kind, connected, settings?.WslDistribution,
            CurrentWslQuickAccessDirectory(), home);
    }

    // Same accepted session binding as the composer/message reader; no other session/host cwd.
    private string? CurrentWslQuickAccessDirectory()
        => _selectedSessionId is { } session && IsSameMessageBinding(session, _messageBinding?.Directory)
            ? _messageBinding?.Directory
            : _newSessionDraftActive ? _activeDraftSlot?.Key.Directory : null;

    private void RefreshWslQuickAccess()
    {
        if (_wslQuickAccessView is null) return;
        var state = CurrentWslQuickAccess();
        var distribution = _homeSettings?.Current?.OpenCode.WslDistribution;
        if (!state.Visible || _wslHomeDistribution != distribution || !ReferenceEquals(_wslHomeSource, _source))
            ResetWslQuickAccessHome();
        if (state.Visible && AIFrontend.Models.OpenCodeSettings.IsValidWslDistributionName(distribution)
            && WslQuickAccessHome.NeedsHome(CurrentWslQuickAccessDirectory()) && _wslHomeRead is null)
        {
            _wslHomeDistribution = distribution;
            _wslHomeSource = _source;
            _wslHomeCts = new();
            _wslHomeRead = WslQuickAccessHome.ResolveAsync(distribution!,
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe"),
                new WslHomeCommandRunner(), _wslHomeCts.Token);
            _ = CompleteWslQuickAccessHomeAsync(_wslHomeRead);
        }
        WslQuickAccessSlot.Visibility = state.Visible ? Visibility.Visible : Visibility.Collapsed;
        WslQuickAccessSection.Visibility = WslQuickAccessSlot.Visibility;
        WslQuickAccessNoteSlot.Visibility = WslQuickAccessSlot.Visibility;
        _wslQuickAccessView.Render(state, _wslEditors, _selectedWslEditor);
    }

    private void ResetWslQuickAccessHome()
    {
        _wslHomeCts?.Cancel();
        _wslHomeCts?.Dispose();
        _wslHomeCts = null;
        _wslHomeRead = null;
        _wslHomeDistribution = null;
        _wslHomeSource = null;
        _wslAuthoritativeHome = null;
    }

    private async Task CompleteWslQuickAccessHomeAsync(Task<string?> read)
    {
        string? home = null;
        try { home = await read; }
        catch (OperationCanceledException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (System.IO.IOException) { }
        catch (InvalidOperationException) { }
        if (!ReferenceEquals(read, _wslHomeRead)) return;
        _wslAuthoritativeHome = home;
        RefreshWslQuickAccess();
    }

    private async Task LaunchWslQuickAccessAsync(string? editorName)
    {
        // Re-read live state at invocation; a menu may outlive a directory or connection change.
        var state = CurrentWslQuickAccess();
        if (!state.Visible || state.Target is not { } target) { RefreshWslQuickAccess(); return; }
        try
        {
            if (editorName is not null)
            {
                var editor = WslHostEditorDetection.Detect(_wslEditorHost).First(e => e.Name == editorName);
                if (editor.Executable is null) { DetectWslEditors(); return; }
                _wslEditorPreference.Save(editor.Name);
                _selectedWslEditor = editor.Name;
                await OpenCodeWslQuickAccess.LaunchAsync(state, editor, _wslQuickAccessLauncher);
                RefreshWslQuickAccess();
            }
            else await OpenCodeWslQuickAccess.LaunchAsync(state, null, _wslQuickAccessLauncher);
        }
        catch (Exception) { _wslQuickAccessView?.ShowFailure(); }
    }
}
