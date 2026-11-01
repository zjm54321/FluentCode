using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages
{
    public sealed partial class OpenCodePage
    {
        private OpenCodeInsightsBinding? _current;
        private sealed record MessageBinding(OpenCodePageSource Source, long Revision, string SessionId, string Directory);
        private MessageBinding? _messageBinding;
        private bool _homeActive = false, _newSessionDraftActive = false, _sessionMissing = false;
        private readonly OpenCodeSessionInsightsController _insights;
#if NATIVE_UI
        private Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue => Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        internal Microsoft.UI.Xaml.XamlRoot XamlRoot => null!;
        internal int DispatcherAccesses => 0;
#else
        private readonly HostDispatcher DispatcherQueue = new();
        internal object XamlRoot { get; } = new();
        internal int DispatcherAccesses => DispatcherQueue.Accesses;
#endif
        internal OpenCodePage(OpenCodeInsightsBinding binding)
        {
            _current = binding; _messageBinding = new(binding.Source, binding.Revision, binding.SessionId, "/work");
            _insights = new(IsCurrentInsightsBinding); InitializePersistentPty();
        }
        internal OpenCodeInsightsBinding Binding => _current!;
        internal OpenCodeSessionInsightsController Insights => _insights;
        private bool IsCurrentInsightsBinding(OpenCodeInsightsBinding binding) => binding == _current && !binding.Source.IsRevoked;
        private bool IsSameMessageBinding(string session, string? directory) => _messageBinding?.SessionId == session && _messageBinding.Directory == directory;
        private void RefreshRightPanelMetadata() => RefreshPersistentPtyScope(_current, _messageBinding?.Directory);
        internal Task PrepareAsync() { RefreshRightPanelMetadata(); return Task.CompletedTask; }
        internal Task DashboardAsync(OpenCodeBillionContext context) => OpenBillionDashboardAsync(context);
        internal void CancelDashboard() => InvalidateBillionDashboardConfirmation();
        internal void Navigate(OpenCodeInsightsBinding? binding)
        { _current = binding; _messageBinding = binding is null ? null : new(binding.Source, binding.Revision, binding.SessionId, "/work"); RefreshRightPanelMetadata(); }
        private void ShowMessage(InfoBarSeverity severity, string title, string message) { }
        private sealed class HostDispatcher
        {
            internal int Accesses; internal bool Hold;
            private readonly Queue<Action> _pending = new();
            internal bool HasThreadAccess { get { Accesses++; return !Hold; } }
            internal bool TryEnqueue(Action action) { if (Hold) { lock (_pending) _pending.Enqueue(action); } else action(); return true; }
            internal void Flush() { Hold = false; while (_pending.TryDequeue(out var action)) action(); }
        }
    }
}
#if !NATIVE_UI
namespace Microsoft.UI.Xaml { public enum TextWrapping { Wrap } }
namespace Microsoft.UI.Xaml.Controls
{
    public enum InfoBarSeverity { Warning }
    public enum ContentDialogResult { None, Primary }
    public enum ContentDialogButton { Close }
    public class TextBlock { public string? Text { get; set; } public Microsoft.UI.Xaml.TextWrapping TextWrapping { get; set; } }
    public sealed class ContentDialog
    {
        public static TaskCompletionSource<ContentDialogResult>? Next; public static ContentDialog? Last;
        public object? XamlRoot { get; set; } public string? Title { get; set; } public object? Content { get; set; }
        public string? PrimaryButtonText { get; set; } public string? CloseButtonText { get; set; } public ContentDialogButton DefaultButton { get; set; }
        public Task<ContentDialogResult> ShowAsync() { Last = this; return Next?.Task ?? Task.FromResult(ContentDialogResult.None); }
        public void Hide() => Next?.TrySetResult(ContentDialogResult.None);
    }
}
namespace Windows.System
{
    public static class Launcher
    {
        public static readonly List<Uri> Opened = new();
        public static Task<bool> LaunchUriAsync(Uri uri) { Opened.Add(uri); return Task.FromResult(true); }
    }
}
#endif
