using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode.Browser;

// Inert server boundary. The production peer, event feed, leases and manager execute unchanged.
namespace AIFrontend.Services.OpenCode
{
    public sealed record OpenCodeEvent(string Type, string LocationDirectory, JsonElement Data);
    public class OpenCodeException : Exception { }
    public sealed class OpenCodeTransportException : OpenCodeException { }
    public sealed class OpenCodeTimeoutException : OpenCodeException { }
    public sealed class OpenCodeBrowserRpcException : OpenCodeException
    {
        public bool IsUnsupported => false;
        public bool IsUnavailable => false;
    }
    public sealed class OpenCodeClient(OpenCodeBrowserEventFeed feed)
    {
        internal readonly ConcurrentDictionary<string, string> Connections = new();
        internal readonly ConcurrentDictionary<string, string> Directories = new();
        internal readonly ConcurrentDictionary<string, OpenCodeBrowserCommand> Commands = new();
        internal readonly ConcurrentDictionary<string, OpenCodeBrowserOutcome> Results = new();
        public async Task<string> BrowserAttachAsync(string directory, string session, string connection, CancellationToken ct)
        {
            Connections[session] = connection; Directories[session] = directory;
            Publish(directory, connection, "attached");
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return "closed";
        }
        internal void Publish(string directory, string connection, string type, string? request = null)
            => feed.Publish(new("rpc.experimental.browser.control", directory, JsonSerializer.SerializeToElement(new
            { connectionID = connection, type, version = 4, requestID = request })));
        public Task PostBrowserStateAsync(string directory, string session, string connection, OpenCodeBrowserState state, CancellationToken ct) => Task.CompletedTask;
        public Task<OpenCodeBrowserCommand> GetBrowserCommandAsync(string directory, string session, string connection, string request, CancellationToken ct)
            => Task.FromResult(Commands[session + ":" + request]);
        public Task PostBrowserResultAsync(string directory, string session, string connection, string request, OpenCodeBrowserOutcome outcome, CancellationToken ct)
        { Results[session + ":" + request] = outcome; return Task.CompletedTask; }
        public Task<string> OpenBrowserTunnelAsync(string directory, string session, string connection, OpenCodeBrowserTunnelTarget target, CancellationToken ct) => throw new NotSupportedException();
        public Task<OpenCodeBrowserTunnelRead> ReadBrowserTunnelAsync(string directory, string session, string connection, string tunnel, CancellationToken ct) => throw new NotSupportedException();
        public Task WriteBrowserTunnelAsync(string directory, string session, string connection, string tunnel, byte[] data, bool? end, CancellationToken ct) => throw new NotSupportedException();
        public Task CloseBrowserTunnelAsync(string directory, string session, string connection, string tunnel, CancellationToken ct) => Task.CompletedTask;
    }
}

namespace AIFrontend.Services.OpenCode.Browser
{
    public sealed record OpenCodeBrowserTunnelTarget(string Host, int Port);
    public sealed record OpenCodeBrowserTunnelRead(byte[] Data, bool Eof);
}

namespace AIFrontend.Pages.OpenCode
{
    internal enum OpenCodeTreeStatus { Unknown, Running, Succeeded, Failed, Interrupted }
}

namespace AIFrontend.Pages
{
    // UI/source seams for source-linking the actual child-directory and peer-composition methods.
    public sealed partial class OpenCodePage
    {
        private bool _pageActive = true;
        private bool _homeActive = false;
        private bool _treeChildrenAccepted = true;
        private bool SourceReady => true;
        private readonly AIFrontend.Services.OpenCode.OpenCodeClient _client;
        private readonly Source _source = new();
        private readonly Dictionary<string, int> _sessionTabs = new();
        private readonly Dictionary<string, Info> _sessionItems = new();
        private readonly Dictionary<string, Info> _treeChildren = new();
        private readonly Dictionary<string, OpenCode.OpenCodeTreeStatus> _treeStatuses = new();
        private readonly Dictionary<string, (bool Working, long Version)> _liveSessionStatuses = new();
        private readonly Dictionary<string, object> _browserWorkspaces = new();
        private readonly Selector _agentSelector = new();
        private object? _agentSelectorSource;
        private int _agentSelectorRevision;
        private readonly int _sourceRevision = 1;
        private string? _agentSelectorSessionId;
        private string? _selectedSessionId;
        private Binding? _treeBinding;
        private readonly object? _treeParentTarget = null;
        private readonly MessageBinding? _messageBinding = null;
        private void SyncBrowserSource() { }
        private void RefreshBrowserPanel() { }
        private bool IsTreeCurrent(Binding binding) => binding.SessionId == _selectedSessionId;
        private string? KnownSessionDirectory(string id) => _sessionItems.GetValueOrDefault(id)?.Location?.Directory;
        private bool IsSameMessageBinding(string id, string? directory) => false;
        partial void OnBrowserAgentTreeChanged();
        partial void OnBrowserSessionTabOpened(string sessionId);
        partial void OnBrowserSessionTabClosed(string sessionId);
        partial void OnBrowserEventSyncStarting();
        partial void OnBrowserEventStreamHealthChanged(bool connected);
        partial void OnBrowserControlEvent(AIFrontend.Services.OpenCode.OpenCodeEvent item);
        partial void OnBrowserEventSyncStopped();
        partial void OnBrowserEngineSessionTabClosed(string sessionId) => _browserWorkspaces.Remove(sessionId);
        internal OpenCodePage()
        {
            _client = new(_browserEventFeed);
            _browserEventFeed.SetConnected(true);
        }
        internal void OpenChild(string id, string directory)
        {
            _selectedSessionId = id; // Real child navigation does not create a root-session tab.
            _agentSelectorSource = _source; _agentSelectorRevision = _sourceRevision; _agentSelectorSessionId = id;
            _agentSelector.CurrentSessionInfo = new(id, "root", new(directory));
            EnsureBrowserPeers();
            OnBrowserEventStreamHealthChanged(true);
        }
        internal void TrackRoot(string id, string child, string directory)
        {
            _selectedSessionId = id; _treeBinding = new(id);
            _treeChildren[child] = new(child, id, new(directory));
            _treeStatuses[child] = OpenCode.OpenCodeTreeStatus.Running;
            EnsureBrowserPeers();
        }
        internal void FinishChild(string id)
        { _treeStatuses[id] = OpenCode.OpenCodeTreeStatus.Succeeded; OnBrowserAgentTreeChanged(); }
        internal void UntrackRoot()
        { _treeChildren.Clear(); _treeBinding = null; OnBrowserAgentTreeChanged(); }
        private sealed record Source { public CancellationToken RevocationToken => CancellationToken.None; }
        private sealed record Location(string Directory);
        private sealed record Info(string Id, string? ParentId, Location? Location);
        private sealed class Selector { public Info? CurrentSessionInfo { get; set; } }
        private sealed record Binding(string SessionId);
        private sealed record MessageBinding(string SessionId, string? Directory);
    }
}
