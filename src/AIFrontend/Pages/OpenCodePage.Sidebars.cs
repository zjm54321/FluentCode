using System;
using System.Collections.Generic;
using System.Linq;
using AIFrontend.Pages.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.System;
using Windows.UI.ViewManagement;

namespace AIFrontend.Pages;

/// <summary>
/// Layout D (对齐停靠): wide pages dock floating acrylic cards beside the reading column (top aligned with the
/// first message, bottom cards aligned with the composer); narrow pages fold them into the composer's second
/// row plus the header's context % and 概况 entries. Session identity, drafts, controls and the insights view
/// retain their owners and never change parent.
/// </summary>
public sealed partial class OpenCodePage
{
    private OpenCodeSidebarLayout _sidebarLayout;
    private bool _sidebarApplying;
    private UISettings? _sidebarSettings;
    private readonly Dictionary<SplitView, (VisualStateGroup Group, VisualTransition[] Transitions)> _sidebarTransitions = new();
    private Storyboard? _dockMotion;
    private bool _dockFolding;
    private bool _sidebarSuppressSlide;
    private readonly CompositeTransform _leftDockTransform = new();
    private readonly CompositeTransform _rightDockTransform = new();
    private readonly Flyout _overviewFlyout = new() { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };

    private void InitializeSidebars()
    {
        _overviewFlyout.Closed += (_, _) =>
        {
            if (!_sidebarLayout.IsWide && !_sidebarApplying)
            {
                _sidebarLayout = _sidebarLayout.Dismiss(OpenCodeSidebarDrawer.Right);
                ApplySidebarLayout();
            }
        };
        PageRoot.SizeChanged += (_, e) => OnSidebarWidthChanged(e.NewSize.Width);
        LeftDock.RenderTransform = _leftDockTransform;
        LeftDock.RenderTransformOrigin = new Point(0, 0.5);
        RightDock.RenderTransform = _rightDockTransform;
        RightDock.RenderTransformOrigin = new Point(1, 0.5);
        // Card insets follow the real reading column: top = first message row, bottom = composer bottom.
        MessageScroll.SizeChanged += (_, _) => UpdateDockInsets();
        ComposerCell.SizeChanged += (_, _) => UpdateDockInsets();
        SessionPane.SizeChanged += (_, _) => UpdateDockInsets();
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
            StopDockMotion();
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

    private void OnSidebarWidthChanged(double width)
    {
        // A fold in progress commits with the final width when it completes.
        if (_dockFolding) return;
        var previous = _sidebarLayout;
        var next = previous.Resize(width);
        var cardsShown = SessionPane.IsPaneOpen || InsightsPane.IsPaneOpen;
        var animate = OpenCodeSidebarLayout.AnimateModeSwitch(previous, next, _sidebarSettings?.AnimationsEnabled ?? false);
        if (animate && previous.IsWide && cardsShown)
        {
            // Wide → narrow: fold the resident cards toward their edges, then hand over to the narrow layout.
            PlayDockMotion(fold: true, () =>
            {
                _sidebarLayout = _sidebarLayout.Resize(PageRoot.ActualWidth);
                _sidebarSuppressSlide = true;
                try { ApplySidebarLayout(); }
                finally { _sidebarSuppressSlide = false; }
                StopDockMotion();
                if (_sidebarLayout.IsWide) PlayDockMotion(fold: false, null);
            });
            return;
        }

        _sidebarLayout = next;
        ApplySidebarLayout();
        // Narrow → wide: the cards pop out from their edges.
        if (animate && next.IsWide && (SessionPane.IsPaneOpen || InsightsPane.IsPaneOpen)) PlayDockMotion(fold: false, null);
    }

    private void PlayDockMotion(bool fold, Action? completed)
    {
        StopDockMotion();
        var duration = new Duration(TimeSpan.FromMilliseconds(fold ? 160 : 240));
        EasingFunctionBase ease = new ExponentialEase { Exponent = 6, EasingMode = fold ? EasingMode.EaseIn : EasingMode.EaseOut };
        var story = new Storyboard();
        void Add(DependencyObject target, string property, double from, double to)
        {
            var animation = new DoubleAnimation { From = from, To = to, Duration = duration, EasingFunction = ease };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            story.Children.Add(animation);
        }
        const double scale = 0.94, shift = 24;
        foreach (var (dock, transform, edge) in new[] { (LeftDock, _leftDockTransform, -1.0), (RightDock, _rightDockTransform, 1.0) })
        {
            Add(dock, "Opacity", fold ? 1 : 0, fold ? 0 : 1);
            Add(transform, "ScaleX", fold ? 1 : scale, fold ? scale : 1);
            Add(transform, "ScaleY", fold ? 1 : scale, fold ? scale : 1);
            Add(transform, "TranslateX", fold ? 0 : edge * shift, fold ? edge * shift : 0);
        }
        _dockMotion = story;
        _dockFolding = fold;
        story.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_dockMotion, story)) return;
            _dockFolding = false;
            if (completed is not null) completed();
            else StopDockMotion();
        };
        story.Begin();
    }

    private void StopDockMotion()
    {
        var story = _dockMotion;
        _dockMotion = null;
        _dockFolding = false;
        story?.Stop(); // returns opacity / transforms to their resting values
    }

    private void UpdateDockInsets()
    {
        if (SessionLayoutRoot.Visibility != Visibility.Visible || SessionPane.ActualHeight <= 0) return;
        double top = 12, bottom = 16;
        try
        {
            if (MessageScroll.ActualHeight > 0)
                top = MessageScroll.TransformToVisual(SessionPane).TransformPoint(new Point(0, 0)).Y;
            if (ComposerCell.Visibility == Visibility.Visible && ComposerCell.ActualHeight > 0)
                bottom = SessionPane.ActualHeight
                    - ComposerCell.TransformToVisual(SessionPane).TransformPoint(new Point(0, ComposerCell.ActualHeight)).Y;
        }
        catch (ArgumentException) { return; } // not in the same visual tree yet
        top = Math.Clamp(top, 8, Math.Max(8, SessionPane.ActualHeight / 3));
        bottom = Math.Clamp(bottom, 8, Math.Max(8, SessionPane.ActualHeight / 3));
        var outer = OpenCodeSidebarLayout.DockOuterMargin;
        var inner = OpenCodeSidebarLayout.DockInnerGap;
        LeftDock.Padding = new Thickness(outer, top, inner, bottom);
        // Narrow: 概况 opens just under the header buttons as a floating panel.
        RightDock.Padding = _sidebarLayout.IsWide ? new Thickness(inner, top, outer, bottom) : new Thickness(0);
    }

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
            var animate = !_sidebarSuppressSlide && _sidebarLayout.AnimateDrawers(_sidebarSettings?.AnimationsEnabled ?? false);
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
            ConfigureRightOverview(wide);
            InsightsPane.IsPaneOpen = wide && surface.RightOpen;
            // Wide: the dock columns continue the opaque reading surface so the cards float on one plane.
            // Narrow: drawers are only their floating card (transparent around it, light-dismiss outside).
            ApplyDockSurface(LeftDock, wide);
            ApplyDockSurface(RightDock, wide);
            RightDock.VerticalAlignment = wide ? VerticalAlignment.Stretch : VerticalAlignment.Top;
            // Subagents and connection live in the composer's second row in narrow; the header keeps only
            // the context % and 概况. Wide shows no header entries at all.
            SessionPaneToggleButton.Visibility = Visibility.Collapsed;
            SessionPaneToggleButton.IsChecked = surface.ShowDrawerButtons && surface.LeftOpen;
            SessionInsightsButton.Visibility = hasSessionSurface && (surface.ShowDrawerButtons || !workspaceVisible)
                ? Visibility.Visible : Visibility.Collapsed;
            RightSessionContent.Visibility = hasSessionSurface ? Visibility.Visible : Visibility.Collapsed;
            RightSessionDiagnostics.Visibility = hasSessionSurface ? Visibility.Visible : Visibility.Collapsed;
            RightPanelExistingActions.Visibility = rightResident ? Visibility.Visible : Visibility.Collapsed;
            MoveExistingSidebarActions(rightResident);
            // Session connection details live in the resident footer or narrow composer dot.
            ConnectionStatusButton.Visibility = hasSessionSurface ? Visibility.Collapsed : Visibility.Visible;
            ContextRingHost.Visibility = (_sidebarLayout.ShowHeaderContext || !workspaceVisible) && hasSessionSurface
                ? Visibility.Visible : Visibility.Collapsed;
            RightContextRingHost.Visibility = Visibility.Collapsed;
            RightContextDivider.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            _insightsView.ShowContext = wide;
            RefreshRightPanelMetadata();
            OnSessionInsightsVisibilityChanged(hasSessionSurface && workspaceVisible && surface.RightOpen);
            if (!hasSessionSurface || (!wide && !surface.RightOpen)) _overviewFlyout.Hide();
            QueueSessionControlCardRefresh();
            UpdateDockInsets();
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
        _insightsView.ContextRing.ShowPercent = true;
    }

    private void ConfigureRightOverview(bool wide)
    {
        // Move only the root: the native Billion inputs keep their immediate parents and draft state.
        if (wide && InsightsPane.Pane is null)
        {
            _overviewFlyout.Hide();
            _overviewFlyout.Content = null;
            InsightsPane.Pane = RightDock;
        }
        else if (!wide && InsightsPane.Pane is not null)
        {
            InsightsPane.Pane = null;
            _overviewFlyout.Content = RightDock;
        }
        RightDock.Width = wide ? double.NaN : 304;
        RightDock.RowDefinitions[0].Height = wide ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        RightDock.RowSpacing = wide ? 12 : 0;
        foreach (var card in new[] { RightSessionCard, RightFooterCard })
        {
            if (wide)
            {
                card.ClearValue(Border.BackgroundProperty);
                card.BorderThickness = new Thickness(1);
            }
            else
            {
                card.Background = null;
                card.BorderThickness = new Thickness(0);
            }
        }
    }

    private static void ApplyDockSurface(Grid dock, bool wide)
    {
        // The XAML style supplies the theme-aware SolidBackgroundFillColorBaseBrush; a local null hides it.
        if (wide) dock.ClearValue(Panel.BackgroundProperty);
        else dock.Background = null;
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
        if (_sidebarLayout.RightOpen)
        {
            OpenSessionInsightsPane();
            if (!_sidebarLayout.IsWide) _overviewFlyout.ShowAt(SessionInsightsButton);
        }
    }

    private void OpenBillionSettingsPane()
    {
        if (_sidebarLayout.IsWide) return;
        if (_sidebarLayout.Drawer != OpenCodeSidebarDrawer.Right)
            _sidebarLayout = _sidebarLayout.Toggle(OpenCodeSidebarDrawer.Right);
        ApplySidebarLayout();
        _overviewFlyout.ShowAt(SessionInsightsButton);
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
        (drawer == OpenCodeSidebarDrawer.Left ? (Control)ComposerSubagentsButton : SessionInsightsButton).Focus(FocusState.Programmatic);
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

}
