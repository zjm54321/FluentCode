#pragma warning disable CS0169, CS0414, CS0649, CS0067
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AIFrontend.Services.Notifications;
using Microsoft.UI.Xaml;

namespace AIFrontend.Pages;

// Only platform/IO collaborators are doubled. The Loaded/pause/resume/final-release decisions,
// draft transitions and message-binding rebase are the verbatim production methods.
public sealed partial class OpenCodePage
{
    private bool IsLoaded, _pageReleased, _statusRefreshRunning, _isLoadingOlderMessages, _isLoadingMoreSessions;
    private bool _messageHistoryNeedsState;
    private int _messageChainGeneration;
    private object? _messageHistoryWorkflowOwner;
    private event EventHandler? PageReleased;
    private enum MessagePaginationChainState { None }
    private readonly List<MessageWindow> _retainedMessageWindows = new();
    internal int SourceSubscriptions, ConversationSubscriptions, EventStarts, FinalReleases;
    internal bool EventRunning;
    internal bool AckVisible;
    private void SubscribeAppSourceChanged() => SourceSubscriptions++;
    private void AttachConversationEvents() => ConversationSubscriptions++;
    private void AttachViewAcknowledgementEvents() { }
    private void StartPolling() { }
    private void QueueAgentTreeRefresh() { }
    private void RefreshRightPanelMetadata() { }
    private void RevokeMessageRefresh() { }
    private void ResetConversationImages() { } // Image leases are exercised by opencode-session-return.
    private void PauseTerminalView() { }
    private void ResetAgentTree() { }
    private void SessionStateUi_Unloaded(object sender, RoutedEventArgs e) { }
    private void ClearRightPanelCopyHint() { }
    private void StopDockMotion() { }
    private sealed class SettingsBoundary
    {
        internal event EventHandler? AnimationsEnabledChanged;
        internal event EventHandler? Changed;
    }
    private SettingsBoundary? _sidebarSettings;
    private readonly SettingsBoundary _homeSettings = new();
    private bool _wslQuickAccessSubscribed;
    private void Sidebars_AnimationsEnabledChanged(object? sender, EventArgs e) { }
    private void WslQuickAccessSettingsChanged(object? sender, EventArgs e) { }
    private void ResetWslQuickAccessHome() { }
    private TimerBoundary? _homeChromeTimer;
    internal void LateUnload() => OnPageUnloaded(this, new());
    internal int MessageClears => _conversationView.Clears;
    internal bool SourceBound => _client is not null && _source is not null;
    internal void WatchFinalRelease() => PageReleased += (_, _) => FinalReleases++;
    internal bool BindingCurrent => _messageBinding is { } binding && IsSameMessageBinding(binding.SessionId, binding.Directory);
    internal static void NavigationLifecycleTest(string xaml)
    {
        var checks = 0;
        void Check(bool value, string reason) { checks++; if (!value) throw new Exception(reason); }
        var page = new OpenCodePage(xaml); page.Load(); page.WatchFinalRelease();
        page._hostWindowVisible = true; // Last real host-window push; reparent does not push again.
        page.AddSession("ses_nav_a"); page.AddSession("ses_nav_b");
        page.Select(page.Session("ses_nav_a")); page.Text("first draft");
        page.Select(page.Session("ses_nav_b")); page.Text("second draft");
        var source = page.SourceInstance; var selected = page.SessionTabs.SelectedItem;
        var tabs = page.SessionTabs.TabItems.ToArray(); var clears = page.MessageClears;
        var oldFence = page.CaptureBindingFence(); var starts = page.EventStarts;
        Check(page.SourceSubscriptions == 1 && page.ConversationSubscriptions == 1, "one initial subscription per event");
        page.Load(); page.LateUnload();
        Check(page.SourceSubscriptions == 1 && page.ConversationSubscriptions == 1 && page.EventStarts == starts,
            "duplicate Loaded/late Unloaded cannot duplicate or remove current subscriptions");
        page.LeavePage(); page.LeavePage();
        Check(!oldFence() && !page.EventRunning && !page.AckVisible && page.SourceSubscriptions == 0 && page.ConversationSubscriptions == 0,
            "navigation pause invalidates old callbacks, stops events and unsubscribes once");
        Check(page.SourceBound && page.MessageClears == clears && tabs.SequenceEqual(page.SessionTabs.TabItems)
            && ReferenceEquals(selected, page.SessionTabs.SelectedItem), "navigation preserves client, message rows and exact native tab/selection actors");
        page.ReturnPage(source); page.ReturnPage(source);
        Check(page.EventRunning && page.AckVisible && page.EventStarts == starts + 1 && page.SourceSubscriptions == 1 && page.ConversationSubscriptions == 1,
            "return resumes current source events exactly once");
        Check(page.BindingCurrent && page.Composer.Text == "second draft" && page.MessageClears == clears,
            "same-source return rebases accepted message binding and restores selected draft without clearing rows");
        page.Select(page.Session("ses_nav_a"));
        Check(page.Composer.Text == "first draft", "other session draft survives navigation away/back");
        page.LeavePage(); page.ShutdownPage(); page.ShutdownPage(); page.ReturnPage(source);
        Check(page.FinalReleases == 1 && !page.SourceBound && !page.EventRunning && page.SlotCount == 0
            && page.SourceSubscriptions == 0 && page.ConversationSubscriptions == 0, "final release while cached disposes resources once and cannot reload");
        var visible = new OpenCodePage(xaml); visible.Load(); visible.WatchFinalRelease(); visible.ShutdownPage();
        Check(visible.FinalReleases == 1 && !visible.SourceBound && visible.SourceSubscriptions == 0,
            "window close before native Unloaded also pauses and releases the active page");
        var sourceText = System.IO.File.ReadAllText(Path.Combine(Path.GetDirectoryName(xaml)!, "OpenCodePage.xaml.cs"));
        Check(sourceText.Contains("NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;"), "production Frame page is Required-cached");
        var browser = System.IO.File.ReadAllText(Path.Combine(Path.GetDirectoryName(xaml)!, "OpenCodePage.Browser.cs"));
        var panel = System.IO.File.ReadAllText(Path.Combine(Path.GetDirectoryName(xaml)!, "OpenCodePage.BrowserPanel.cs"));
        Check(!browser.Contains("Unloaded +=") && browser.Contains("PageReleased += (_, _) => DisposeBrowserEngineResources()")
            && panel.Contains("PageReleased +="), "browser engine/selection/panel disposal belongs to final release, not temporary unload");
        Console.WriteLine($"PASS NAV2 actual page Loaded/pause/resume/final-release: {checks} assertions (native Frame/WebView runtime checked by parent)");
    }
}
