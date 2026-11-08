// Only native control/property storage, theme lookup, ring playback and unrelated page actors
// are inert. The production status slot and extracted tab/classification methods execute verbatim.
using System;
using System.Collections.Generic;
using FluentCode.Services.OpenCode;

namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
    public enum VerticalAlignment { Stretch, Top, Center, Bottom }
    public enum HorizontalAlignment { Stretch, Left, Center, Right }
    public enum TextWrapping { NoWrap, Wrap }
    public enum TextTrimming { None, CharacterEllipsis }
    public enum GridUnitType { Auto, Pixel, Star }
    public readonly record struct Thickness(double Left, double Top, double Right, double Bottom);
    public readonly record struct GridLength(double Value, GridUnitType GridUnitType = GridUnitType.Pixel)
    {
        public static GridLength Auto => new(1, GridUnitType.Auto);
    }
    public sealed record Style(string Key);
    public class FrameworkElement
    {
        public double Width { get; set; } = double.NaN;
        public double Height { get; set; } = double.NaN;
        public double MaxWidth { get; set; } = double.PositiveInfinity;
        public Thickness Margin { get; set; }
        public VerticalAlignment VerticalAlignment { get; set; }
        public HorizontalAlignment HorizontalAlignment { get; set; }
        public bool IsHitTestVisible { get; set; } = true;
        public Visibility Visibility { get; set; } = Visibility.Visible;
        public Style? Style { get; set; }
        internal int Column { get; set; }
        internal string AutomationId { get; set; } = "";
        internal string AutomationName { get; set; } = "";
        internal string HelpText { get; set; } = "";
        internal string ItemStatus { get; set; } = "";
        internal Automation.Peers.AccessibilityView AccessibilityView { get; set; }
        internal Automation.Peers.FrameworkElementAutomationPeer? Peer { get; set; }
        internal object? ToolTip { get; set; }
    }
}
namespace Microsoft.UI.Xaml.Controls
{
    using Microsoft.UI.Xaml;
    public class Grid : FrameworkElement
    {
        public List<ColumnDefinition> ColumnDefinitions { get; } = new();
        public List<FrameworkElement> Children { get; } = new();
        public static void SetColumn(FrameworkElement element, int column) => element.Column = column;
    }
    public sealed class ColumnDefinition { public GridLength Width { get; set; } }
    public sealed class TextBlock : FrameworkElement
    {
        public string Text { get; set; } = "";
        public TextWrapping TextWrapping { get; set; }
        public TextTrimming TextTrimming { get; set; }
    }
    public sealed class ProgressRing : FrameworkElement
    {
        public bool IsActive { get; set; }
        public bool IsIndeterminate { get; set; } = true;
    }
    public sealed class FontIcon : FrameworkElement
    {
        public string Glyph { get; set; } = "";
        public double FontSize { get; set; }
    }
    public sealed class InfoBadge : FrameworkElement { }
    public sealed class TabViewItem : FrameworkElement
    {
        public object? Tag { get; set; }
        public object? Header { get; set; }
        public object? ContextFlyout { get; set; }
        public HorizontalAlignment HorizontalContentAlignment { get; set; }
        public bool IsClosable { get; set; }
    }
    public sealed class TabView { public List<TabViewItem> TabItems { get; } = new() { new() }; }
    public static class ToolTipService
    {
        public static void SetToolTip(FrameworkElement element, object value) => element.ToolTip = value;
    }
}
namespace Microsoft.UI.Xaml.Shapes
{
    public sealed class Ellipse : FrameworkElement { }
}
namespace Microsoft.UI.Xaml.Automation
{
    public static class AutomationElementIdentifiers { public static object ItemStatusProperty { get; } = new(); }
    public static class AutomationProperties
    {
        public static void SetAutomationId(FrameworkElement element, string value) => element.AutomationId = value;
        public static void SetName(FrameworkElement element, string value) => element.AutomationName = value;
        public static void SetHelpText(FrameworkElement element, string value) => element.HelpText = value;
        public static string GetItemStatus(FrameworkElement element) => element.ItemStatus;
        public static void SetItemStatus(FrameworkElement element, string value) => element.ItemStatus = value;
        public static void SetAccessibilityView(FrameworkElement element, Peers.AccessibilityView value) => element.AccessibilityView = value;
    }
}
namespace Microsoft.UI.Xaml.Automation.Peers
{
    public enum AccessibilityView { Content, Control, Raw }
    public sealed class FrameworkElementAutomationPeer
    {
        public readonly List<(object Property, object Old, object New)> Changes = new();
        public static FrameworkElementAutomationPeer? FromElement(FrameworkElement element) => element.Peer;
        public void RaisePropertyChangedEvent(object property, object oldValue, object newValue) => Changes.Add((property, oldValue, newValue));
    }
}
namespace FluentCode.Pages.OpenCode
{
    using Microsoft.UI.Xaml;
    using Microsoft.UI.Xaml.Automation;
    using Microsoft.UI.Xaml.Controls;
    internal sealed record OpenCodeDraftSlotKey;
    internal static class OpenCodeToolTheme
    {
        internal static Style Ring(string key) => new(key);
        internal static Style Icon(string key) => new(key);
        internal static void Apply(FrameworkElement element, Style? style) => element.Style = style;
    }
    internal static class OpenCodeInteractionUi
    {
        internal const string AttentionDotStyleKey = "OpenCodeAttentionDotStyle";
        internal static Style TryStyle(string key) => new(key);
    }
    internal static class OpenCodeProgressRingSync
    {
        internal static ProgressRing Create(double size, string accessibleName)
        {
            var ring = new ProgressRing { Width = size, Height = size, IsActive = true };
            AutomationProperties.SetName(ring, accessibleName);
            return ring;
        }
    }
}
namespace FluentCode.Pages
{
    using FluentCode.Pages.OpenCode;
    using Microsoft.UI.Xaml.Controls;
    public sealed partial class OpenCodePage
    {
        private readonly Dictionary<string, SessionTabEntry> _sessionTabs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _sessionTitles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, OpenCodeSessionUiState> _sessionTabStates = new(StringComparer.Ordinal);
        private readonly TabView SessionTabs = new();
        private bool _suppressTabSelection = false;
        private string? _activeTabSessionId = null;
        private string? CurrentSessionTitle => null;
        internal event Action<string?>? SessionTitleChanged;
        private object? CreateSessionTabMenu(SessionTabEntry entry) => null;
        private void OnBrowserSessionTabOpened(string sessionId) { }
        internal TabViewItem Tab(string id, string title, OpenCodeSessionUiState? state)
        {
            _sessionTitles[id] = title;
            if (state is null) _sessionTabStates.Remove(id); else _sessionTabStates[id] = state;
            if (!_sessionTabs.TryGetValue(id, out var entry)) CreateSessionTab(id);
            else ApplySessionTabState(entry);
            return _sessionTabs[id].Item;
        }
        internal OpenCodeTabStatusSlot Slot(string id) => _sessionTabs[id].Status;
        internal TextBlock Title(string id) => _sessionTabs[id].Title;
    }
}
