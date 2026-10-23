using System;
using System.Collections.Generic;
using System.Threading;
using AIFrontend.Services.OpenCode;

// The catalog no longer depends on WinUI controls.
namespace Microsoft.UI.Text { }
namespace Microsoft.UI.Xaml { }
namespace Microsoft.UI.Xaml.Controls { }
namespace Microsoft.UI.Xaml.Automation { }
namespace AIFrontend.Pages
{
    public sealed partial class OpenCodePage
    {
        private readonly Dictionary<string, OpenCodeSessionInfo> _sessionItems = new(StringComparer.Ordinal);
        private bool _homeActive, _newSessionDraftActive;
        private string? _selectedSessionId;
        private CancellationTokenSource? _sessionCts;
        internal IReadOnlyDictionary<string, OpenCodeSessionInfo> Index => _sessionItems;
        internal string? Viewed => _selectedSessionId;
        internal int Changes, Mirrors;
        internal bool Home { set => _homeActive = value; }
        internal bool Blank { set => _newSessionDraftActive = value; }
        private void OnSessionCatalogChanged() => Changes++;
        private void MirrorSessionSelection(string id, bool userInitiated) => Mirrors++;
        internal void SetViewed(string? id, CancellationTokenSource? cts) { _selectedSessionId = id; _sessionCts = cts; }
        internal static bool Root(OpenCodeSessionInfo? session) => IsRootSession(session);
        internal string? Rebuild(List<OpenCodeSessionInfo> sessions) => RebuildSessionList(sessions);
        internal void Append(List<OpenCodeSessionInfo> sessions) => AppendSessionItems(sessions);
        internal void Insert(OpenCodeSessionInfo session) => InsertSessionItemFirst(session);
        internal void Select(string id) => SelectSessionItem(id);
        private void RefreshSessionStateUi() { }
        private void ShowHomePage() => _homeActive = true;
    }
}
