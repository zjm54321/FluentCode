using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode.Browser;

// Shared contract for the built-in browser (m03489 BR1–BR7; decisions BQ1–BQ13).
// Wire shapes follow OpenCode experimental.browser protocol version 4
// (evidence: /tmp/opencode/fc-browser-protocol-20261023.md §2–§3). Lanes extend
// behaviour in their own files; change this file only together with every consumer.

/// <summary>Protocol 4 <c>Browser.Tab</c>. Id matches <c>^tab_[a-f0-9-]{36}$</c>.</summary>
public sealed record OpenCodeBrowserTab(
    string Id,
    string Url,
    string Title,
    bool Loading,
    string? LoadError,
    bool CanGoBack,
    bool CanGoForward,
    long Generation);

/// <summary>Protocol 4 <c>Browser.State</c>: the server's only tab inventory for one session.</summary>
public sealed record OpenCodeBrowserState(IReadOnlyList<OpenCodeBrowserTab> Tabs, string? FocusedTabId)
{
    public static OpenCodeBrowserState Empty { get; } = new(Array.Empty<OpenCodeBrowserTab>(), null);
}

/// <summary>Protocol 4 <c>Browser.File</c>; <see cref="Data"/> is base64 on the wire, at most 5 MiB.</summary>
public sealed record OpenCodeBrowserFile(string Id, string Name, string Mime, byte[] Data);

/// <summary>Protocol 4 <c>Browser.Command</c>. <see cref="Action"/> keeps the exact action object (<c>type</c> + fields).</summary>
public sealed record OpenCodeBrowserCommand(
    JsonObject Action,
    long? Generation,
    IReadOnlyList<OpenCodeBrowserFile> Files,
    bool Inspect,
    JsonObject? Target)
{
    public string ActionType => Action["type"]?.GetValue<string>() ?? string.Empty;
}

/// <summary>Protocol 4 <c>Browser.Outcome</c>.</summary>
public abstract record OpenCodeBrowserOutcome
{
    private OpenCodeBrowserOutcome() { }

    /// <summary><c>{type:"success", result:{value, files}}</c>; value is required JSON (may be null).</summary>
    public sealed record Success(JsonNode? Value, IReadOnlyList<OpenCodeBrowserFile> Files) : OpenCodeBrowserOutcome;

    /// <summary><c>{type:"failure", code, message}</c>; strings at most 2048 characters.</summary>
    public sealed record Failure(string Code, string Message) : OpenCodeBrowserOutcome;
}

/// <summary>
/// Desktop side of one session's browser attachment. The engine implements it; the protocol peer
/// posts <see cref="State"/> whenever <see cref="StateChanged"/> fires and routes commands to
/// <see cref="ExecuteAsync"/>. Cancellation = control event <c>cancel</c> for that request.
/// </summary>
public interface IOpenCodeBrowserSessionBackend
{
    string SessionId { get; }
    OpenCodeBrowserState State { get; }
    event EventHandler? StateChanged;
    Task<OpenCodeBrowserOutcome> ExecuteAsync(OpenCodeBrowserCommand command, CancellationToken cancellationToken);
}

/// <summary>BR7 availability of the agent browser for one session (shown only on the browser button, BQ10).</summary>
public enum OpenCodeBrowserAvailability
{
    /// <summary>Not attached yet or the session is not open in a tab.</summary>
    Detached,
    Attaching,
    /// <summary>Attached (control event <c>attached</c> received); the agent can use browser tools.</summary>
    Available,
    /// <summary>Event stream or attach request dropped; reattaching.</summary>
    Reconnecting,
    /// <summary>Another desktop peer attached the same session (<c>replaced</c>).</summary>
    Replaced,
    /// <summary>This OpenCode server has no <c>experimental.browser</c> RPC.</summary>
    Unsupported,
}

/// <summary>One page (= one protocol tab) as the UI sees it. Implemented by the engine.</summary>
public interface IOpenCodeBrowserPage : INotifyPropertyChanged
{
    string Id { get; }
    string Url { get; }
    string Title { get; }
    bool Loading { get; }
    string? LoadError { get; }
    bool CanGoBack { get; }
    bool CanGoForward { get; }
    /// <summary>True when the agent opened the page (strip menu 「关闭智能体打开的」).</summary>
    bool OpenedByAgent { get; }
    /// <summary>Non-null while a browser action runs on this page, e.g. "填写「团队名称」（browser.fill · ref e12）".</summary>
    string? AgentActivity { get; }
    /// <summary>BQ6 接管: while true, agent actions on this page fail with 「用户已接管此页面」.</summary>
    bool UserTakeover { get; set; }
    /// <summary>BQ12: server-local path shown in 「预览 ·」 mode; null for normal web pages.</summary>
    string? PreviewPath { get; }

    void Navigate(string url);
    void GoBack();
    void GoForward();
    void Reload();
    void Stop();
    void Close();
}

/// <summary>All pages of one session plus its availability. Implemented by the engine/peer composition.</summary>
public interface IOpenCodeBrowserWorkspace
{
    string SessionId { get; }
    /// <summary>Most recently activated first.</summary>
    IReadOnlyList<IOpenCodeBrowserPage> Pages { get; }
    IOpenCodeBrowserPage? Shown { get; }
    OpenCodeBrowserAvailability Availability { get; }
    /// <summary>Human reason for the button tooltip when not <see cref="OpenCodeBrowserAvailability.Available"/>.</summary>
    string? AvailabilityReason { get; }
    event EventHandler? PagesChanged;
    event EventHandler? AvailabilityChanged;

    /// <summary>User-opened page (button / <c>+</c>); shows it in the overlay.</summary>
    IOpenCodeBrowserPage OpenPage(string url);
    /// <summary>Show the page in the overlay (strip click).</summary>
    void Show(IOpenCodeBrowserPage page);
    /// <summary>Close the overlay; pages keep running (BQ5).</summary>
    void HideOverlay();
}
