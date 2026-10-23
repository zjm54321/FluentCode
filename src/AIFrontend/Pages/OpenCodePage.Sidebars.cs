using System;
using System.Collections.Generic;
using System.Linq;
using AIFrontend.Pages.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.ViewManagement;

namespace AIFrontend.Pages;

/// <summary>B1 layout only. Session identity, drafts, controls and the insights view retain their owners.</summary>
public sealed partial class OpenCodePage
{
    private OpenCodeSidebarLayout _sidebarLayout;
    private bool _sidebarApplying;
    private UISettings? _sidebarSettings;
    private readonly Dictionary<SplitView, (VisualStateGroup Group, VisualTransition[] Transitions)> _sidebarTransitions = new();

    private void InitializeSidebars()
    {
        PageRoot.SizeChanged += (_, e) =>
        {
            _sidebarLayout = _sidebarLayout.Resize(e.NewSize.Width);
            ApplySidebarLayout();
        };
        // Layout states are still owned by Home/NewSessionTab; observe them after the
        // entire state setter batch, rather than introducing another layout-state owner.
        SessionLayoutRoot.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => QueueSidebarLayout());
        NewSessionHeader.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => QueueSidebarLayout());
        SessionInsightsButton.RegisterPropertyChangedCallback(IsEnabledProperty, (_, _) => QueueSidebarLayout());
        WorkspaceGrid.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => QueueSidebarLayout());
        ActualThemeChanged += (_, _) => QueueSidebarLayout();
        SessionPane.PaneOpening += (_, _) => AdoptSidebarOpen(OpenCodeSidebarDrawer.Left);
        InsightsPane.PaneOpening += (_, _) => AdoptSidebarOpen(OpenCodeSidebarDrawer.Right);
        PageRoot.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(Sidebars_PreviewKeyDown), true);
        Loaded += Sidebars_Loaded;
        Unloaded += (_, _) =>
        {
            if (_sidebarSettings is not null && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                _sidebarSettings.AnimationsEnabledChanged -= Sidebars_AnimationsEnabledChanged;
            _sidebarSettings = null;
            _sidebarLayout = _sidebarLayout.Escape();
        };
    }

    private void Sidebars_Loaded(object sender, RoutedEventArgs e)
    {
        _sidebarSettings = new UISettings();
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            _sidebarSettings.AnimationsEnabledChanged += Sidebars_AnimationsEnabledChanged;
        _sidebarLayout = _sidebarLayout.Resize(PageRoot.ActualWidth);
        ApplySidebarLayout();
    }

    private void Sidebars_AnimationsEnabledChanged(UISettings sender, object args)
        => DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(sender, _sidebarSettings)) ApplySidebarLayout();
        });

    private void QueueSidebarLayout() => DispatcherQueue.TryEnqueue(ApplySidebarLayout);

    private void ApplySidebarLayout()
    {
        if (_sidebarApplying) return;
        _sidebarApplying = true;
        try
        {
            var hasSessionSurface = SessionLayoutRoot.Visibility == Visibility.Visible
                && NewSessionHeader.Visibility != Visibility.Visible;
            if (!hasSessionSurface) _sidebarLayout = _sidebarLayout.Escape();
            var surface = _sidebarLayout.ForSession(hasSessionSurface);
            var wide = surface.Resident;
            // With no configured workspace the right host is hidden by existing page
            // policy; leave connection details accessible in the header in that case.
            var workspaceVisible = WorkspaceGrid.Visibility == Visibility.Visible;
            var rightResident = wide && workspaceVisible;
            var mode = wide ? SplitViewDisplayMode.Inline : SplitViewDisplayMode.Overlay;
            var animate = _sidebarLayout.AnimateDrawers(_sidebarSettings?.AnimationsEnabled ?? false);
            ConfigureSidebarMotion(SessionPane, animate);
            ConfigureSidebarMotion(InsightsPane, animate);
            SessionPane.DisplayMode = mode;
            InsightsPane.DisplayMode = mode;
            // Keep some exposed surface for light-dismiss even on an extremely narrow page.
            SessionPane.OpenPaneLength = wide ? OpenCodeSidebarLayout.LeftWidth
                : System.Math.Max(0, System.Math.Min(OpenCodeSidebarLayout.LeftWidth, PageRoot.ActualWidth - 48));
            InsightsPane.OpenPaneLength = wide ? OpenCodeSidebarLayout.RightWidth
                : System.Math.Max(0, System.Math.Min(OpenCodeSidebarLayout.RightWidth, PageRoot.ActualWidth - 48));
            SessionPane.IsPaneOpen = surface.LeftOpen;
            InsightsPane.IsPaneOpen = surface.RightOpen;
            SessionPaneToggleButton.Visibility = surface.ShowDrawerButtons ? Visibility.Visible : Visibility.Collapsed;
            SessionPaneToggleButton.IsChecked = surface.ShowDrawerButtons && surface.LeftOpen;
            SessionInsightsButton.Visibility = hasSessionSurface && (surface.ShowDrawerButtons || !workspaceVisible)
                ? Visibility.Visible : Visibility.Collapsed;
            SessionInsightsButton.IsChecked = surface.ShowDrawerButtons && surface.RightOpen;
            InsightsPaneCloseButton.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
            RightSessionContent.Visibility = hasSessionSurface ? Visibility.Visible : Visibility.Collapsed;
            RightSessionDiagnostics.Visibility = hasSessionSurface ? Visibility.Visible : Visibility.Collapsed;
            OverviewRefreshMessagesButton.IsEnabled = hasSessionSurface;
            RightPanelExistingActions.Visibility = rightResident ? Visibility.Visible : Visibility.Collapsed;
            MoveExistingSidebarActions(rightResident);
            // Session connection details live in the resident footer or narrow composer dot.
            ConnectionStatusButton.Visibility = hasSessionSurface ? Visibility.Collapsed : Visibility.Visible;
            ContextRingHost.Visibility = (_sidebarLayout.ShowHeaderContext || !workspaceVisible) && hasSessionSurface
                ? Visibility.Visible : Visibility.Collapsed;
            RightContextRingHost.Visibility = hasSessionSurface ? Visibility.Visible : Visibility.Collapsed;
            RefreshRightPanelMetadata();
            QueueComposerLayout();
        }
        finally { _sidebarApplying = false; }
    }

    private void MoveExistingSidebarActions(bool wide)
    {
        // Only these two small entry controls move. The editor and Billion controls
        // never change parent, so draft identity, IME and in-progress input survive.
        if (wide && RightConnectionStatusHost.Child is null)
        {
            ConnectionFlyout.Hide();
            SidebarHeaderActions.Children.Remove(ConnectionStatusButton);
            RightConnectionStatusHost.Child = ConnectionStatusButton;
        }
        else if (!wide && RightConnectionStatusHost.Child is not null)
        {
            ConnectionFlyout.Hide();
            RightConnectionStatusHost.Child = null;
            SidebarHeaderActions.Children.Insert(1, ConnectionStatusButton);
        }
        var target = wide ? RightContextRingHost : ContextRingHost;
        var previous = wide ? ContextRingHost : RightContextRingHost;
        if (previous.Child is { } ring)
        {
            _insightsView.ContextRing.Flyout.Hide();
            previous.Child = null;
            target.Child = ring;
        }
    }

    // The native WinUI SplitView template stores its slide storyboards in the
    // DisplayModeStates transitions (its states only set final geometry). Remove
    // those transitions for reduced motion; do not replace the native template,
    // light-dismiss layer, focus behavior, brushes or high-contrast resources.
    private void ConfigureSidebarMotion(SplitView pane, bool animate)
    {
        pane.ApplyTemplate();
        if (VisualTreeHelper.GetChildrenCount(pane) == 0
            || VisualTreeHelper.GetChild(pane, 0) is not FrameworkElement root) return;
        foreach (var group in VisualStateManager.GetVisualStateGroups(root))
        {
            if (group.Name != "DisplayModeStates") continue;
            if (!_sidebarTransitions.TryGetValue(pane, out var saved) || !ReferenceEquals(saved.Group, group))
            {
                // A theme/template replacement must not retain the old template tree.
                _sidebarTransitions[pane] = saved = (group, group.Transitions.ToArray());
            }
            if (!animate && group.Transitions.Count > 0)
            {
                group.Transitions.Clear();
                // Also settle an already-running slide immediately when the preference changes.
                VisualStateManager.GoToState(pane, "Closed", false);
                var state = !pane.IsPaneOpen ? "Closed"
                    : pane.DisplayMode == SplitViewDisplayMode.Inline
                        ? pane.PanePlacement == SplitViewPanePlacement.Left ? "OpenInlineLeft" : "OpenInlineRight"
                        : pane.PanePlacement == SplitViewPanePlacement.Left ? "OpenOverlayLeft" : "OpenOverlayRight";
                VisualStateManager.GoToState(pane, state, false);
            }
            else if (animate && group.Transitions.Count == 0)
            {
                foreach (var transition in saved.Transitions) group.Transitions.Add(transition);
            }
        }
    }

    private void AdoptSidebarOpen(OpenCodeSidebarDrawer drawer)
    {
        if (_sidebarApplying || _sidebarLayout.IsWide) return;
        if (_sidebarLayout.Drawer != drawer) _sidebarLayout = _sidebarLayout.Toggle(drawer);
        ApplySidebarLayout();
    }

    private void SessionPaneToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _sidebarLayout = _sidebarLayout.Toggle(OpenCodeSidebarDrawer.Left);
        ApplySidebarLayout();
    }

    private void SessionInsightsButton_Click(object sender, RoutedEventArgs e)
    {
        _sidebarLayout = _sidebarLayout.Toggle(OpenCodeSidebarDrawer.Right);
        ApplySidebarLayout();
        if (_sidebarLayout.RightOpen) OpenSessionInsightsPane();
    }

    private void DismissSidebarDrawers()
    {
        _sidebarLayout = _sidebarLayout.Escape();
        ApplySidebarLayout();
    }

    private void Sidebars_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || _sidebarLayout.Drawer == OpenCodeSidebarDrawer.None) return;
        var drawer = _sidebarLayout.Drawer;
        DismissSidebarDrawers();
        (drawer == OpenCodeSidebarDrawer.Left ? SessionPaneToggleButton : SessionInsightsButton).Focus(FocusState.Programmatic);
        e.Handled = true;
    }

    private void SessionPane_PaneClosed(SplitView sender, object args)
        => SidebarPaneClosed(sender, OpenCodeSidebarDrawer.Left);

    private void InsightsPane_PaneClosed(SplitView sender, object args)
        => SidebarPaneClosed(sender, OpenCodeSidebarDrawer.Right);

    private void SidebarPaneClosed(SplitView sender, OpenCodeSidebarDrawer drawer)
    {
        if (_sidebarApplying || sender.IsPaneOpen) return;
        _sidebarLayout = _sidebarLayout.Dismiss(drawer);
        ApplySidebarLayout();
    }

    private void InsightsPaneCloseButton_Click(object sender, RoutedEventArgs e)
    {
        _sidebarLayout = _sidebarLayout.Dismiss(OpenCodeSidebarDrawer.Right);
        ApplySidebarLayout();
        if (!_sidebarLayout.IsWide) SessionInsightsButton.Focus(FocusState.Programmatic);
    }
}
