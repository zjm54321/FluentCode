using System;
using System.Collections.Generic;
using System.Linq;
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

    private void InitializeWslQuickAccess()
    {
        _wslQuickAccessView = new();
        WslQuickAccessSlot.Children.Add(_wslQuickAccessView);
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
            WslQuickAccessSlot.Visibility = Visibility.Collapsed;
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
        // Same accepted session binding as the composer/message reader. Never substitute a host cwd,
        // a project from another session, or an inferred username. api/info supplies no home directory.
        var directory = _selectedSessionId is { } session && IsSameMessageBinding(session, _messageBinding?.Directory)
            ? _messageBinding?.Directory
            : _newSessionDraftActive ? _activeDraftSlot?.Key.Directory : null;
        return OpenCodeWslQuickAccess.Resolve(settings?.Kind, connected, settings?.WslDistribution, directory);
    }

    private void RefreshWslQuickAccess()
    {
        if (_wslQuickAccessView is null) return;
        var state = CurrentWslQuickAccess();
        WslQuickAccessSlot.Visibility = state.Visible ? Visibility.Visible : Visibility.Collapsed;
        _wslQuickAccessView.Render(state, _wslEditors, _selectedWslEditor);
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
