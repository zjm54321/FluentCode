// Inert controls and unrelated page state only. All list algorithms and the selection handler
// are extracted from current production sources by run.ps1, never reproduced in this host.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
    public enum HorizontalAlignment { Left, Center, Right, Stretch }
    public enum TextTrimming { None, CharacterEllipsis }
    public class FrameworkElement
    {
        public Visibility Visibility { get; set; }
        public double Opacity { get; set; } = 1;
        internal string AutomationName { get; set; } = string.Empty;
    }
}

namespace Windows.UI.Text
{
    public struct FontWeight { public ushort Weight { get; set; } }
}

namespace Microsoft.UI.Text
{
    public static class FontWeights
    {
        public static Windows.UI.Text.FontWeight SemiBold => new() { Weight = 600 };
    }
}

namespace Microsoft.UI.Xaml.Automation
{
    public static class AutomationProperties
    {
        public static void SetName(FrameworkElement item, string name) => item.AutomationName = name;
        public static string GetName(FrameworkElement item) => item.AutomationName;
    }
}

namespace Microsoft.UI.Xaml.Controls
{
    public sealed class TextBlock : FrameworkElement
    {
        public string Text { get; set; } = string.Empty;
        public double FontSize { get; set; }
        public Windows.UI.Text.FontWeight FontWeight { get; set; }
        public TextTrimming TextTrimming { get; set; }
    }

    public sealed class StackPanel : FrameworkElement
    {
        public double Spacing { get; set; }
        public Collection<FrameworkElement> Children { get; } = new();
    }

    public sealed class ListViewItem : FrameworkElement
    {
        public object? Tag { get; set; }
        public object? Content { get; set; }
        public HorizontalAlignment HorizontalContentAlignment { get; set; }
    }

    public sealed class SelectionChangedEventArgs : EventArgs { }

    public sealed class ListView : FrameworkElement
    {
        private object? _selected;
        public ListView() => Items = new ItemCollection(this);
        public ItemCollection Items { get; }
        public event Action<object, SelectionChangedEventArgs>? SelectionChanged;
        internal Action<string>? MutationObserved { get; set; }
        internal List<object?> Scrolls { get; } = new();
        internal bool ThrowOnScroll { get; set; }
        public object? SelectedItem
        {
            get => _selected;
            set
            {
                if (ReferenceEquals(_selected, value)) return;
                _selected = value;
                SelectionChanged?.Invoke(this, new SelectionChangedEventArgs());
            }
        }
        public void ScrollIntoView(object? item)
        {
            if (ThrowOnScroll) throw new InvalidOperationException("host.scroll.failure");
            Scrolls.Add(item);
        }

        // Selection notifications are synchronous; removal/clear of the selected item clears it.
        // No navigation, filtering, deduplication or suppression rules are implemented here.
        public sealed class ItemCollection(ListView owner) : Collection<object>
        {
            protected override void InsertItem(int index, object item)
            {
                owner.MutationObserved?.Invoke("Insert");
                base.InsertItem(index, item);
            }
            protected override void RemoveItem(int index)
            {
                owner.MutationObserved?.Invoke("Remove");
                var old = this[index];
                base.RemoveItem(index);
                if (ReferenceEquals(owner.SelectedItem, old)) owner.SelectedItem = null;
            }
            protected override void ClearItems()
            {
                owner.MutationObserved?.Invoke("Clear");
                base.ClearItems();
                owner.SelectedItem = null;
            }
        }
    }
}

namespace AIFrontend.Pages
{
    public sealed partial class OpenCodePage
    {
        private readonly Dictionary<string, ListViewItem> _sessionItems = new(StringComparer.Ordinal);
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, OpenCodeSessionInfo> HomeSessionMetadata = new();
        private bool _suppressSessionSelection;
        private bool _homeActive = false; // Home presentation is outside this session-list fixture.
        private bool _newSessionDraftActive = false;
        private string? _selectedSessionId;
        private CancellationTokenSource? _sessionCts;
        internal ListView SessionList { get; } = new();
        internal TextBlock SessionsHint { get; } = new();
        internal List<string> SwitchRequests { get; } = new();
        internal int NewSessionTabRequests { get; private set; }
        internal List<bool> SelectionSuppression { get; } = new();
        internal List<bool> MutationSuppression { get; } = new();
        internal IReadOnlyDictionary<string, ListViewItem> Index => _sessionItems;
        internal string? Viewed => _selectedSessionId;
        internal bool Suppress { get => _suppressSessionSelection; set => _suppressSessionSelection = value; }

        public OpenCodePage()
        {
            SessionList.MutationObserved = _ => MutationSuppression.Add(_suppressSessionSelection);
            SessionList.SelectionChanged += (_, _) => SelectionSuppression.Add(_suppressSessionSelection);
            SessionList.SelectionChanged += SessionList_SelectionChanged;
        }

        // A sink for the real selection handler, not a simulated SwitchToSession implementation.
        private Task SwitchToSessionAsync(string id)
        {
            SwitchRequests.Add(id);
            return Task.CompletedTask;
        }

        internal void SetViewed(string? id, CancellationTokenSource? cts)
        { _selectedSessionId = id; _sessionCts = cts; }
        internal static bool Root(OpenCodeSessionInfo? session) => IsRootSession(session);
        internal string? Rebuild(List<OpenCodeSessionInfo> sessions) => RebuildSessionList(sessions);
        internal void Append(List<OpenCodeSessionInfo> sessions) => AppendSessionItems(sessions);
        internal void InsertFirst(OpenCodeSessionInfo session) => InsertSessionItemFirst(session);
        internal void Select(string id) => SelectSessionItem(id);
        private void RefreshSessionStateUi() { } // Unrelated indicator UI hook; list algorithms remain linked.
        private void ShowHomePage() { } // Home presentation is outside this session-list fixture.
        private void RequestNewSessionTab() => NewSessionTabRequests++; // Blank-tab presentation boundary only.
    }
}
