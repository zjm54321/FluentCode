using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.Configuration;
using Microsoft.UI.Xaml;

namespace FluentCode.Pages;

public sealed partial class OpenCodePage
{
    private OpenCodeWslQuickAccessView? _wslQuickAccessView;
    private readonly IWslQuickAccessLauncher _wslQuickAccessLauncher = new WindowsWslQuickAccessLauncher();
    private readonly WslHostPathMapping _wslHostPaths = new(
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe"), new WslHomeCommandRunner());
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
    private bool _wslQuickAccessSubscribed;

    private void InitializeWslQuickAccess()
    {
        _wslQuickAccessView = new();
        WslQuickAccessSlot.Children.Add(_wslQuickAccessView);
        Motion.FluentMotion.PressScale(_wslQuickAccessView); // Quick-access tile: press 0.97, render-only.
        WslQuickAccessNoteSlot.Children.Add(_wslQuickAccessView.Note);
        _wslQuickAccessView.Note.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
            WslQuickAccessNoteSlot.Visibility = WslQuickAccessSlot.Visibility == Visibility.Visible
                ? _wslQuickAccessView.Note.Visibility : Visibility.Collapsed);
        _selectedWslEditor = _wslEditorPreference.Load();
        _wslQuickAccessView.ExplorerRequested += async (_, _) => await LaunchWslQuickAccessAsync(null);
        _wslQuickAccessView.EditorRequested += async (_, e) => await LaunchWslQuickAccessAsync(e.Name, e.SelectDefault);
        _wslQuickAccessView.EditorMenuRequested += (_, _) => DetectWslEditors();
        InitializeBrowserPanel();
        Loaded += (_, _) =>
        {
            if (_wslQuickAccessSubscribed) return;
            _wslQuickAccessSubscribed = true;
            _homeSettings!.Changed += WslQuickAccessSettingsChanged;
            DetectWslEditors();
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            _wslQuickAccessSubscribed = false;
            _homeSettings!.Changed -= WslQuickAccessSettingsChanged;
            ResetWslQuickAccessHome();
            WslQuickAccessSlot.Visibility = Visibility.Collapsed;
            WslQuickAccessNoteSlot.Visibility = Visibility.Collapsed;
        };
    }

    private void WslQuickAccessSettingsChanged(object? sender, AppSettingsChangedEventArgs e)
        => DispatcherQueue.TryEnqueue(DetectWslEditors);

    private void DetectWslEditors()
    {
        _wslEditors = WslHostEditorDetection.Detect(_wslEditorHost);
        if (_homeSettings?.Current?.OpenCode.Kind == FluentCode.Models.OpenCodeConnectionKind.Ssh)
            _wslEditors = OpenCodeSshQuickAccess.Editors(_wslEditors);
        RefreshWslQuickAccess();
    }

    private WslQuickAccessState CurrentWslQuickAccess()
    {
        var settings = _homeSettings?.Current?.OpenCode;
        var connected = _pageActive && _connection == ConnectionState.Connected && _client is not null
            && _source is { IsRevoked: false } && !_homeActive;
        if (settings?.Kind == FluentCode.Models.OpenCodeConnectionKind.Ssh)
            return OpenCodeSshQuickAccess.Resolve(settings.Kind, connected, settings.SshHost, CurrentWslQuickAccessDirectory());
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
        if (state.Visible && FluentCode.Models.OpenCodeSettings.IsValidWslDistributionName(distribution)
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
        UpdateRightQuickAccessRow();
        _wslQuickAccessView.Render(state, _wslEditors, _selectedWslEditor);
        WslQuickAccessNoteSlot.Visibility = state.Visible ? _wslQuickAccessView.Note.Visibility : Visibility.Collapsed;
        RefreshBrowserPanel();
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

    private async Task LaunchWslQuickAccessAsync(string? editorName, bool selectDefault = false)
    {
        // Re-read live state at invocation; a menu may outlive a directory or connection change.
        var state = CurrentWslQuickAccess();
        if (!state.Visible || state.Target is not { } target) { RefreshWslQuickAccess(); return; }
        var view = _wslQuickAccessView;
        var editorAction = editorName is not null;
        if (view is null || Motion.ActionFeedback.Current(view.ActionControl(editorAction)) == Motion.ActionFeedback.State.Busy) return;
        Motion.ActionFeedback.Busy(view.ActionControl(editorAction));
        try
        {
            var mapped = state.IsSsh ? target : await _wslHostPaths.MapAsync(target);
            // Mapping is asynchronous: do not launch a folder from a superseded session/connection.
            if (CurrentWslQuickAccess().Target != target) return;
            state = state with { Target = mapped };
            if (editorName is not null)
            {
                _wslEditors = WslHostEditorDetection.Detect(_wslEditorHost);
                if (state.IsSsh) _wslEditors = OpenCodeSshQuickAccess.Editors(_wslEditors);
                await OpenCodeWslQuickAccess.LaunchEditorAsync(state, _wslEditors, _selectedWslEditor,
                    selectDefault ? editorName : null, name =>
                    {
                        _wslEditorPreference.Save(name);
                        _selectedWslEditor = name;
                        RefreshWslQuickAccess();
                    }, _wslQuickAccessLauncher);
                RefreshWslQuickAccess();
            }
            else await OpenCodeWslQuickAccess.LaunchAsync(state, null, _wslQuickAccessLauncher);
            if (CurrentWslQuickAccess().Target == target) view.ShowActionSuccess(editorAction);
        }
        catch (Exception)
        {
            if (CurrentWslQuickAccess().Target == target)
            {
                view.ShowFailure();
                Motion.ActionFeedback.Failure(view.ActionControl(editorAction), "无法打开主机应用", "请检查安装与目录访问权限");
            }
        }
        finally
        {
            if (Motion.ActionFeedback.Current(view.ActionControl(editorAction)) == Motion.ActionFeedback.State.Busy) view.ResetAction(editorAction);
        }
    }

    private WslQuickAccessState ResolveDetailPath(MessageHistoryRead read, string path)
    {
        var settings = _homeSettings?.Current?.OpenCode;
        if (!IsCurrentMessageHistoryRead(read)) return new(true, null, "会话或连接已切换");
        if (settings?.Kind is not (FluentCode.Models.OpenCodeConnectionKind.Wsl or FluentCode.Models.OpenCodeConnectionKind.Ssh))
            return new(true, null, "当前连接未提供已授权的本机路径映射；不能将远程路径作为本机路径打开");
        if (!path.StartsWith('/') && path is not "~" && !path.StartsWith("~/", StringComparison.Ordinal))
        {
            if (read.Directory?.StartsWith('/') != true) return new(true, null, "会话未提供可解析相对路径的绝对工作目录");
            path = read.Directory.TrimEnd('/') + "/" + path;
        }
        if (settings.Kind == FluentCode.Models.OpenCodeConnectionKind.Ssh)
            return OpenCodeSshQuickAccess.Resolve(settings.Kind, true, settings.SshHost, path);
        var home = ReferenceEquals(_wslHomeSource, read.Source) && _wslHomeDistribution == settings.WslDistribution ? _wslAuthoritativeHome : null;
        return OpenCodeWslQuickAccess.Resolve(settings.Kind, true, settings.WslDistribution, path, home);
    }

    private WslHostEditor? DetailPathEditor() => WslEditorChoices.Resolve(_wslEditors, _selectedWslEditor).Current;

    private string? DetailPathUnavailableReason(MessageHistoryRead read, string path, bool directory)
    {
        var state = ResolveDetailPath(read, path);
        if (state.IsSsh && directory) return "SSH 连接的文件夹不能在资源管理器中打开，请用 Zed 或 VS Code";
        return state.Target is null ? state.Reason ?? "当前路径不可用"
            : !directory && DetailPathEditor() is null ? state.IsSsh ? "未找到可打开此文件的 VS Code 或 Zed" : "未检测到可用于打开此 WSL 文件的主机编辑器" : null;
    }

    private async Task LaunchDetailPathAsync(MessageHistoryRead read, string path, bool directory, bool reveal, int? line,
        Microsoft.UI.Xaml.Controls.ContentControl? feedback = null)
    {
        var state = ResolveDetailPath(read, path);
        if (state.Target is not { } target || DetailPathUnavailableReason(read, path, directory) is not null) return;
        if (feedback is not null)
        {
            if (Motion.ActionFeedback.Current(feedback) == Motion.ActionFeedback.State.Busy) return;
            Motion.ActionFeedback.Busy(feedback);
        }
        var launched = false;
        try
        {
            if (directory)
            {
                if (reveal)
                {
                    var parent = target.LinuxDirectory[..(target.LinuxDirectory.LastIndexOf('/') + 1)];
                    state = OpenCodeWslQuickAccess.Resolve(FluentCode.Models.OpenCodeConnectionKind.Wsl, true, target.Distribution, parent);
                }
                if (state.Target is not { } folder) return;
                state = state with { Target = await _wslHostPaths.MapAsync(folder) };
                if (IsCurrentMessageHistoryRead(read)) { await OpenCodeWslQuickAccess.LaunchAsync(state, null, _wslQuickAccessLauncher); launched = true; }
            }
            else if (DetailPathEditor() is { } editor && IsCurrentMessageHistoryRead(read))
            {
                target = state.IsSsh ? target : await _wslHostPaths.MapAsync(target);
                if (!IsCurrentMessageHistoryRead(read)) return;
                var launch = OpenCodeWslQuickAccess.EditorLaunch(target, editor);
                if (line is > 0 && editor.OpenMode == WslEditorOpenMode.RemoteWsl)
                    launch = new(launch.Executable, new[] { "--remote", state.IsSsh ? "ssh-remote+" + target.SshHost : "wsl+" + target.Distribution,
                        "--goto", target.LinuxDirectory + ":" + line.Value });
                await _wslQuickAccessLauncher.OpenEditorAsync(launch);
                launched = true;
            }
            if (launched && IsCurrentMessageHistoryRead(read))
            {
                if (feedback is not null) Motion.ActionFeedback.Success(feedback, directory ? "已打开文件夹" : "已在编辑器中打开");
                else Motion.ActionFeedback.Announce(this, directory ? "已打开文件夹" : "已在编辑器中打开");
            }
        }
        catch (Exception)
        {
            if (IsCurrentMessageHistoryRead(read))
            {
                if (feedback is not null) Motion.ActionFeedback.Failure(feedback, "无法打开路径");
                ShowMessage(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning, "无法打开路径", "主机未能打开该路径；请复制路径后手动打开。");
            }
        }
        finally { if (feedback is not null && Motion.ActionFeedback.Current(feedback) == Motion.ActionFeedback.State.Busy) Motion.ActionFeedback.Reset(feedback); }
    }
}
