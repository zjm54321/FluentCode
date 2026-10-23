using System;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Appearance;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using Windows.UI.Core;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private readonly OpenCodeComposerShellState _shellComposer = new();
    private OpenCodeComposerBinding? _shellBinding;
    private int _shellDraftStart, _shellDraftLength;

    partial void InitializeShellComposer()
    {
        AppFonts.Apply(ShellComposer, AppFontSlot.Monospace);
        AppFonts.Apply(ShellPromptGlyph, AppFontSlot.Monospace);
        _composerView!.TerminalRequested += (_, _) => EnterShellComposer();
        _composerView.SlotGesturesRevoked += (_, _) =>
        {
            ExitShellComposer(focus: false);
            _shellComposer.Reset();
            _shellBinding = null;
            ShellComposer.Text = string.Empty;
        };
        Composer.BeforeTextChanging += (_, e) =>
        {
            if (_shellComposer.Active || _composerView is null || _isSending || !GetComposerSnapshot().IsActive
                || !OpenCodeComposerShellState.IsEntry(Composer.Text, e.NewText, Composer.SelectionStart,
                    Composer.SelectionLength, _isComposing || _suppressComposerFocus || !_pageActive,
                    _composerView.IsAutocompleteVisible)) return;
            e.Cancel = true; // Consumed BEFORE the normal draft ledger sees it; the original text/selection is intact.
            var binding = _composerBinding;
            DispatcherQueue.TryEnqueue(() => { if (ReferenceEquals(binding, _composerBinding)) EnterShellComposer(); });
        };
        ShellComposer.TextChanged += (_, _) => { _shellComposer.Text = ShellComposer.Text; UpdateShellComposerState(); };
        ShellComposer.PreviewKeyDown += (_, e) =>
        {
            if (_shellComposing) return; // Let the native IME consume its own keys.
            var ctrl = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;
            var shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0;
            if (OpenCodeComposerShellState.IsExit(e.Key == VirtualKey.Escape, e.Key == VirtualKey.Back,
                ctrl && e.Key == VirtualKey.C, ShellComposer.SelectionStart, ShellComposer.Text))
            { e.Handled = true; ExitShellComposer(focus: true); }
            else if (e.Key == VirtualKey.Enter && !shift)
            {
                e.Handled = true;
                if (!_shellEnterLatched && DateTimeOffset.UtcNow - _shellCompositionEnded > TimeSpan.FromMilliseconds(250))
                {
                    _shellEnterLatched = true;
                    RequestComposerSubmission();
                }
            }
        };
        ShellComposer.KeyUp += (_, e) => { if (e.Key == VirtualKey.Enter) _shellEnterLatched = false; };
        ShellComposer.TextCompositionStarted += (_, _) => _shellComposing = true;
        ShellComposer.TextCompositionEnded += (_, _) => { _shellComposing = false; _shellCompositionEnded = DateTimeOffset.UtcNow; };
        ShellComposer.LostFocus += (_, _) => { _shellComposing = false; _shellEnterLatched = false; };
    }

    private bool _shellComposing;
    private bool _shellEnterLatched;
    private DateTimeOffset _shellCompositionEnded = DateTimeOffset.MinValue;

    private void EnterShellComposer()
    {
        if (_shellComposer.Active || IsChildComposerSession || !_pageActive || _composerView is null || _composerBinding is not { } binding
            || !OwnsComposerBinding(binding) || GetComposerSnapshot().IsBusy || HasPendingNativeAction()
            || ComposerCoveredByOverlay || !_composerView.PrepareShellMode()) return;
        if (!ReferenceEquals(_shellBinding, binding)) _shellComposer.Reset();
        _shellBinding = binding;
        _shellDraftStart = Composer.SelectionStart;
        _shellDraftLength = Composer.SelectionLength;
        _shellComposer.Enter();
        VisualStateManager.GoToState(this, "TerminalComposerMode", false);
        ShellComposer.Text = _shellComposer.Text;
        ComposerInputLayout.Visibility = ComposerCompanionHost.Visibility = Visibility.Collapsed;
        ComposerShellHost.Visibility = Visibility.Visible;
        FadeInComposerSurface(ComposerShellHost);
        UpdateShellComposerState();
        ShellComposer.Focus(FocusState.Programmatic);
        ShellComposer.Select(ShellComposer.Text.Length, 0);
    }

    private void ExitShellComposer(bool focus)
    {
        if (!_shellComposer.Active) return;
        _shellComposer.Exit();
        VisualStateManager.GoToState(this, "NormalComposerMode", false);
        _shellComposing = false;
        ComposerShellHost.Visibility = Visibility.Collapsed;
        ComposerInputLayout.Visibility = ComposerCompanionHost.Visibility = Visibility.Visible;
        FadeInComposerSurface(ComposerInputLayout);
        // The normal TextBox and controller draft were never overwritten (including chips/attachments).
        if (focus && _pageActive)
        {
            Composer.Select(Math.Min(_shellDraftStart, Composer.Text.Length),
                Math.Min(_shellDraftLength, Composer.Text.Length - Math.Min(_shellDraftStart, Composer.Text.Length)));
            Composer.Focus(FocusState.Programmatic);
            // A fast 204 can restore focus before Enter is released; its repeat must not submit the normal draft.
            _enterDown = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Enter) & CoreVirtualKeyStates.Down) != 0;
        }
    }

    private static Windows.UI.ViewManagement.UISettings? _shellMotionSettings;

    /// <summary>Visual only: a short fade for the surface that just became visible. Skipped when the system
    /// "Animation effects" setting is off; never delays focus, input or state changes.</summary>
    private static void FadeInComposerSurface(UIElement surface)
    {
        bool animate;
        try { animate = (_shellMotionSettings ??= new Windows.UI.ViewManagement.UISettings()).AnimationsEnabled; }
        catch (Exception) { animate = false; }
        if (!animate) { surface.Opacity = 1; return; }
        var fade = new DoubleAnimation
        {
            From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(140)),
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 },
        };
        Storyboard.SetTarget(fade, surface);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var story = new Storyboard();
        story.Children.Add(fade);
        story.Begin();
    }

    partial void UpdateShellComposerState()
    {
        if (!_shellComposer.Active) return;
        if (_shellBinding is not { } binding || !OwnsComposerBinding(binding)) { ExitShellComposer(focus: false); return; }
        var ready = !GetComposerSnapshot().IsBusy && !HasPendingNativeAction() && !ComposerCoveredByOverlay;
        ShellComposer.IsEnabled = ready;
    }

    partial void TrySubmitShell(string? delivery, ref bool handled)
    {
        if (!_shellComposer.Active) return;
        handled = true;
        if (delivery == OpenCodeDeliveryModes.Queue)
        { ShowMessage(InfoBarSeverity.Warning, "无法排队", "Shell 命令不能排队；请按 Enter 执行。"); return; }
        if (!_pageActive || _shellComposing || !_shellComposer.CanSubmit || _composerBinding is not { } binding
            || !OwnsComposerBinding(binding) || GetComposerSnapshot().IsBusy || HasPendingNativeAction() || ComposerCoveredByOverlay) return;
        if (OpenCodeComposerController.IsUnbound(binding))
            _ = CreateSessionAndSubmitAsync(_composerController!.Snapshot.Token, _shellComposer.Text);
        else _ = SubmitSessionShellAsync(binding, _shellComposer.Text);
    }

    partial void OnCreatedShellSubmit(string command)
    {
        if (_composerBinding is { } binding && OwnsComposerBinding(binding) && !OpenCodeComposerController.IsUnbound(binding))
        {
            _shellBinding = binding;
            _shellComposer.Text = command;
            EnterShellComposer(); // Creation changed slots; reattach the separate command, never replace the normal draft.
            _ = SubmitSessionShellAsync(binding, command);
        }
    }

    private async Task SubmitSessionShellAsync(OpenCodeComposerBinding binding, string command)
    {
        if (_isSending || !OwnsComposerBinding(binding)) return;
        var owner = BeginLocalAgentSubmission();
        _isSending = true;
        RefreshComposerView();
        UpdateComposerButtons();
        var started = false;
        bool Owns() => OwnsComposerSubmission(binding, owner);
        try
        {
            using var cancellation = CreateOperationToken(binding.Source);
            using (var lease = await OpenCodeInteractionGate.Shared.EnterAsync(binding.Source.Identity, binding.SessionId, cancellation.Token))
            {
                if (!Owns() || HasPendingNativeAction() || ComposerCoveredByOverlay) return;
                if (lease.GetAgentSwitchProtection() is not null || lease.GetComposerSubmissionProtection() is not null
                    || lease.GetSessionControlProtection() is not null)
                { ShowMessage(InfoBarSeverity.Warning, "Shell 未执行", "当前会话有未确认的操作保护，请先处理后再运行。"); return; }
                await binding.Client.ExecuteSessionShellGuardedAsync(binding.SessionId, command, action =>
                {
                    var allowed = false;
                    binding.Source.TryExecute(cancellation.Token, () =>
                    {
                        if (!Owns()) return;
                        started = allowed = true;
                        action();
                    });
                    return allowed;
                }, cancellation.Token);
            }
            if (!Owns()) return;
            if (ReferenceEquals(_shellBinding, binding))
            {
                _shellComposer.Text = string.Empty;
                ShellComposer.Text = string.Empty;
                ExitShellComposer(focus: true);
            }
            // A display refresh failure cannot turn an acknowledged 204 into an unknown execution result.
            try { await RefreshMessagesAsync(forceFollow: true); } // Events/history own all output.
            catch (Exception)
            {
                if (Owns()) ShowMessage(InfoBarSeverity.Warning, "Shell 请求已确认", "消息刷新未完成；请刷新会话查看实际输出。");
            }
        }
        catch (OpenCodeApiException error)
        {
            if (Owns()) ShowMessage(InfoBarSeverity.Error, "Shell 请求失败", $"服务器返回 HTTP {error.StatusCode}；命令草稿已保留，不会自动重试。");
        }
        catch (Exception)
        {
            if (Owns()) ShowMessage(InfoBarSeverity.Warning, "Shell 请求未完成", started
                ? "执行结果未知；请查看会话输出。命令草稿已保留，不会自动重试。" : "命令未提交；请检查当前连接后再运行。");
        }
        finally
        {
            if (EndLocalAgentSubmission(owner))
            {
                _isSending = false;
                if (_pageActive) UpdateComposerButtons();
                RefreshComposerView();
            }
        }
    }
}
