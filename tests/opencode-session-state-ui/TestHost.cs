using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using AIFrontend.Services.OpenCode;
using AIFrontend.Services.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
    public enum VerticalAlignment { Top, Center, Bottom, Stretch }
    public enum GridUnitType { Auto, Pixel, Star }
    public enum TextWrapping { NoWrap, Wrap }
    public enum TextTrimming { None, CharacterEllipsis }
    public readonly record struct GridLength(double Value, GridUnitType GridUnitType)
    { public static GridLength Auto => new(0, GridUnitType.Auto); }
    public class RoutedEventArgs : EventArgs { }
    public class FrameworkElement
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public double MaxWidth { get; set; } = double.PositiveInfinity;
        internal string AutomationName { get; set; } = string.Empty;
        internal object? ToolTip { get; set; }
        public Visibility Visibility { get; set; }
        public VerticalAlignment VerticalAlignment { get; set; }
        public event Action<object, RoutedEventArgs>? Loaded;
        public event Action<object, RoutedEventArgs>? Unloaded;
        internal void Load() => Loaded?.Invoke(this, new());
        internal void Unload() => Unloaded?.Invoke(this, new());
    }
    public class Application { public static Application? Current { get; set; } }
}
namespace Microsoft.UI.Xaml.Media { public class Brush { } }
namespace Microsoft.UI.Xaml.Shapes
{
    public class Ellipse : FrameworkElement { public Microsoft.UI.Xaml.Media.Brush? Fill { get; set; } }
}
namespace Microsoft.UI.Xaml.Automation
{
    public static class AutomationProperties
    {
        public static void SetName(FrameworkElement element, string name) => element.AutomationName = name;
        public static string GetName(FrameworkElement element) => element.AutomationName;
        public static void SetAutomationId(FrameworkElement element, string id) { }
    }
}
namespace Microsoft.UI.Xaml.Controls
{
    public enum Orientation { Vertical, Horizontal }
    public class ProgressBar : FrameworkElement { public bool IsIndeterminate { get; set; } }
    public class ProgressRing : FrameworkElement { public bool IsActive { get; set; } }
    public class TextBlock : FrameworkElement
    {
        public string Text { get; set; } = string.Empty;
        public double FontSize { get; set; }
        public TextWrapping TextWrapping { get; set; }
        public TextTrimming TextTrimming { get; set; }
        public Microsoft.UI.Xaml.Media.Brush? Foreground { get; set; }
    }
    public class ListViewItem : FrameworkElement { public object? Content { get; set; } }
    public static class ToolTipService
    {
        public static void SetToolTip(FrameworkElement element, object? value) => element.ToolTip = value;
        public static object? GetToolTip(FrameworkElement element) => element.ToolTip;
    }
    public class StackPanel : FrameworkElement
    {
        public Orientation Orientation { get; set; }
        public double Spacing { get; set; }
        public Collection<FrameworkElement> Children { get; } = new();
    }
    public class ColumnDefinition { public GridLength Width { get; set; } }
    public class Grid : FrameworkElement
    {
        public Collection<ColumnDefinition> ColumnDefinitions { get; } = new();
        public Collection<FrameworkElement> Children { get; } = new();
        public static void SetColumn(FrameworkElement element, int column) { }
    }
}
namespace AIFrontend.Services.Notifications
{
    // Identity carrier only; real source ownership and App adapter are tested in their own harness.
    public sealed class OpenCodePageSource(string identity, long revision)
    {
        public string Identity { get; } = identity;
        public long Revision { get; } = revision;
        public bool IsRevoked { get; set; }
    }
}
namespace AIFrontend
{
    public sealed class OpenCodeSessionUiStateChangedEventArgs(string endpointIdentity, long revision, long version,
        OpenCodeSessionUiSnapshot? snapshot) : EventArgs
    {
        public string EndpointIdentity { get; } = endpointIdentity;
        public long Revision { get; } = revision;
        public long Version { get; } = version;
        public OpenCodeSessionUiSnapshot? Snapshot { get; } = snapshot;
    }
    public sealed class App : Application
    {
        private EventHandler<OpenCodeSessionUiStateChangedEventArgs>? _changed;
        private EventHandler? _sourceChanged;
        internal int StateSubscribers => _changed?.GetInvocationList().Length ?? 0;
        internal int SourceSubscribers => _sourceChanged?.GetInvocationList().Length ?? 0;
        public event EventHandler<OpenCodeSessionUiStateChangedEventArgs>? OpenCodeSessionUiStateChanged
        { add => _changed += value; remove => _changed -= value; }
        public event EventHandler? OpenCodeSourceChanged
        { add => _sourceChanged += value; remove => _sourceChanged -= value; }
        public OpenCodePageSource? CurrentOpenCodePageSource { get; set; }
        internal OpenCodeSessionUiSnapshot? Snapshot;
        internal readonly List<(OpenCodePageSource Source, long Revision)> Gets = new();
        public OpenCodeSessionUiSnapshot? GetOpenCodeSessionUiSnapshot(OpenCodePageSource source, long revision)
        { Gets.Add((source, revision)); return Snapshot; }
        internal void Emit(string identity, long revision, long version, OpenCodeSessionUiSnapshot? payload = null)
            => _changed?.Invoke(this, new(identity, revision, version, payload));
        internal void SourceChanged() => _sourceChanged?.Invoke(this, EventArgs.Empty);
    }
}
namespace AIFrontend.Pages
{
    public sealed class TestDispatcher
    {
        public bool HasThreadAccess { get; set; } = true;
        private readonly Queue<Action> _queue = new();
        public bool TryEnqueue(Action action) { _queue.Enqueue(action); return true; }
        internal int Count => _queue.Count;
        internal void Drain() { while (_queue.Count > 0) _queue.Dequeue()(); }
    }
    public sealed partial class OpenCodePage : FrameworkElement
    {
        private OpenCodePageSource? _source;
        private long _sourceRevision;
        private bool _pageActive;
        private string? _selectedSessionId;
        private readonly Dictionary<string, ListViewItem> _sessionItems = new(StringComparer.Ordinal);
        public TestDispatcher DispatcherQueue { get; } = new();
        // F6: the top horizontal progress bar is gone; row ring tones are reported through the production hook.
        internal readonly List<(ProgressRing Ring, SessionIndicatorTone Tone)> RowTones = new();
        partial void OnSessionStateRowToneApplied(ProgressRing working, SessionIndicatorTone tone) => RowTones.Add((working, tone));
        internal TextBlock SessionStateHint { get; } = new() { Visibility = Visibility.Collapsed };
        internal IReadOnlyDictionary<string, ListViewItem> Items => _sessionItems;
        public OpenCodePage() => InitializeSessionStateUi();
        internal void Bind(OpenCodePageSource source, string viewed)
        { _source = source; _sourceRevision = source.Revision; _selectedSessionId = viewed; }
        internal void Activate() { _pageActive = true; Load(); }
        internal void Deactivate() { _pageActive = false; Unload(); }
        internal void Refresh() => RefreshSessionStateUi();
        // List fixtures, not reproductions of Rebuild/Append algorithms. Trigger their UI hook directly.
        internal ListViewItem Add(string id)
        {
            var item = new ListViewItem { Content = new StackPanel() };
            _sessionItems.Add(id, item);
            RefreshSessionStateUi();
            return item;
        }
        internal void Remove(string id) { _sessionItems.Remove(id); RefreshSessionStateUi(); }
    }
}
