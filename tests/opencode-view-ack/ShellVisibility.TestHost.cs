// Inert window/presenter and side-effect witnesses. The visibility/activation/taskbar guards
// themselves are extracted from production MainWindow; no shell policy is copied here.
using System;
using System.Collections.Generic;
using FluentCode.Pages;

namespace Microsoft.UI.Xaml
{
    public enum WindowActivationState { Deactivated, CodeActivated }
    public sealed class WindowActivatedEventArgs : EventArgs
    {
        public WindowActivationState WindowActivationState { get; init; }
    }
}
namespace Microsoft.UI.Windowing
{
    public enum OverlappedPresenterState { Restored, Minimized }
    public sealed class OverlappedPresenter { public OverlappedPresenterState State { get; set; } }
    public sealed class AppWindow
    {
        private readonly OverlappedPresenter _presenter = new();
        private bool _visible, _failPresenter, _failVisibility;
        public object Presenter => _failPresenter ? throw new InvalidOperationException("synthetic presenter read failure") : _presenter;
        public bool IsVisible => _failVisibility ? throw new InvalidOperationException("synthetic visibility read failure") : _visible;
        internal void Configure(bool minimized, bool visible)
        {
            _presenter.State = minimized ? OverlappedPresenterState.Minimized : OverlappedPresenterState.Restored;
            _visible = visible; _failPresenter = _failVisibility = false;
        }
        internal void FailRead(string getter)
        {
            if (getter == "presenter") _failPresenter = true;
            else if (getter == "visibility") _failVisibility = true;
            else throw new ArgumentException(getter);
        }
    }
}
namespace FluentCode.Pages.OpenCode
{
    // Only a witness for the unchanged working clock's host-visibility input, not a timer model.
    internal sealed class OpenCodeWorkingClock
    {
        internal static OpenCodeWorkingClock Shared { get; } = new();
        internal bool HostVisible { get; private set; }
        internal void SetHostVisible(bool visible) => HostVisible = visible;
    }
}
namespace FluentCode
{
    public sealed partial class MainWindow
    {
        private bool _openCodeWindowActivated, _openCodeWindowVisible, IsHiddenToTray;
        private readonly Microsoft.UI.Windowing.AppWindow AppWindow = new();
        private sealed class TestFrame { internal object? Content; }
        private readonly TestFrame ContentFrame = new();
        private sealed record TrackedTask(string Id, bool IsTerminal);
        private readonly List<TrackedTask> _trackedTaskList = new();
        private readonly List<string> _acknowledged = new();
        internal MainWindow(OpenCodePage page) => ContentFrame.Content = page;
        internal bool TestOnScreen => _openCodeWindowVisible;
        internal bool TestActivated => _openCodeWindowActivated;
        internal int TestAcknowledged => _acknowledged.Count;
        internal int TestTracked => _trackedTaskList.Count;
        internal void TestTrack(string id, bool terminal) => _trackedTaskList.Add(new(id, terminal));
        private void AcknowledgeTrackedTask(string id) { _acknowledged.Add(id); _trackedTaskList.RemoveAll(task => task.Id == id); }
        internal void TestWindow(bool minimized, bool hiddenToTray, bool visible)
        {
            AppWindow.Configure(minimized, visible); IsHiddenToTray = hiddenToTray;
            UpdateOpenCodeWindowVisibility();
        }
        internal void TestActivation(bool activated) => ShellWindow_VisibilityChanged(this, new()
        {
            WindowActivationState = activated ? Microsoft.UI.Xaml.WindowActivationState.CodeActivated : Microsoft.UI.Xaml.WindowActivationState.Deactivated,
        });
        internal void TestWindowReadFailure(string getter) { AppWindow.FailRead(getter); UpdateOpenCodeWindowVisibility(); }
    }
}
