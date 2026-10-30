// Native UI/CDP harness: production selection/controller/script/geometry/list are source-linked.
// Only the surrounding workspace/attachment transport is a fixture facade; no user backend is contacted.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace AIFrontend.Services.OpenCode.Browser
{
    public sealed class OpenCodeBrowserException(string code, string message) : Exception(message) { public string Code => code; }
    public static class OpenCodeBrowserPolicy { public static string NormalizeUrl(string value) => new Uri(value).AbsoluteUri; }
    public sealed record OpenCodeBrowserCdpEvent(string Method, JsonObject Parameters, string? SessionId);
    public sealed record OpenCodeBrowserPointer(double X,double Y,bool Click);
    public enum OpenCodeBrowserPresentation { Embedded, ExternalWindow }
    public sealed class OpenCodeBrowserWorkspace
    {
        public OpenCodeBrowserPage? Shown { get; set; }
        public event EventHandler? OverlayChanged;
        public void HideOverlay() { Shown=null;OverlayChanged?.Invoke(this,EventArgs.Empty); }
    }
    public sealed class OpenCodeBrowserPage
    {
        public string Id => "tab_controlled";
        public string Url => "about:controlled-selection";
        public long Generation => 1;
        public string? PreviewPath => null;
        public bool Closed { get; set; }
        public bool IsReady => true;
        public bool Loading => false;
        public bool CanGoBack => false;
        public bool CanGoForward => false;
        public string Title => "Controlled selection fixture";
        public string? LoadError => null;
        public string? AgentActivity => null;
        public string AgentName => "fixture";
        public bool UserTakeover { get; set; }
        public OpenCodeBrowserPresentation Presentation => OpenCodeBrowserPresentation.Embedded;
        public FrameworkElement EmbeddedView { get; }
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public event EventHandler<OpenCodeBrowserPointer>? AgentPointerMoved { add { } remove { } }
        public void GoBack() { } public void GoForward() { } public void Reload() { } public void Stop() { } public void Navigate(string url) { }
        public Task FocusAsync(CancellationToken token) { if(EmbeddedView is Control control)control.Focus(FocusState.Programmatic);return Task.CompletedTask; }
        public SemaphoreSlim ActionGate { get; } = new(1, 1);
        public CancellationToken Lifetime => CancellationToken.None;
        public event EventHandler? Committed { add { } remove { } }
        public event EventHandler? Closing { add { } remove { } }
        public readonly OpenCodeBrowserFrames Frames = new();
        public readonly OpenCodeBrowserFrameGeometry Geometry;
        private readonly CoreWebView2 _core;
        private readonly Func<string, JsonObject?, Task<JsonObject>> _send;
        public OpenCodeBrowserPage(WebView2 view, Func<string, JsonObject?, Task<JsonObject>> send)
        { _core = view.CoreWebView2;EmbeddedView=view; _send = send; Geometry = new(Frames, (m,p,s,c) => CdpAsync(m,p,s)); }
        public Task EnsureReadyAsync(CancellationToken token=default) => Task.CompletedTask;
        public Task<JsonObject> CdpAsync(string method, JsonObject? p = null, string? session = null)
            => session is null ? _send(method,p) : SessionAsync(method,p,session);
        private async Task<JsonObject> SessionAsync(string method, JsonObject? p, string session)
            => JsonNode.Parse(await _core.CallDevToolsProtocolMethodForSessionAsync(session,method,p?.ToJsonString()??"{}"))!.AsObject();
        public async Task<string> ExecuteScriptAsync(string script) => await _core.ExecuteScriptAsync(script);
        private sealed class Subscription(Action remove) : IAsyncDisposable { public ValueTask DisposeAsync() { remove(); return ValueTask.CompletedTask; } }
        public Task<IAsyncDisposable> SubscribeCdpAsync(string method, Action<OpenCodeBrowserCdpEvent> receive, CancellationToken token)
        {
            var receiver = _core.GetDevToolsProtocolEventReceiver(method);
            void Handler(CoreWebView2 sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
                => receive(new(method, JsonNode.Parse(e.ParameterObjectAsJson)!.AsObject(), string.IsNullOrEmpty(e.SessionId) ? null : e.SessionId));
            receiver.DevToolsProtocolEventReceived += Handler;
            return Task.FromResult<IAsyncDisposable>(new Subscription(() => receiver.DevToolsProtocolEventReceived -= Handler));
        }
    }
    public static class OpenCodeBrowserSessionBackend
    {
        public static async Task<(OpenCodeBrowserFrames Frames, OpenCodeBrowserFrameGeometry Geometry)> PickerContextAsync(OpenCodeBrowserPage page, CancellationToken token)
        { page.Frames.Tree((await page.CdpAsync("Page.getFrameTree"))["frameTree"]!.AsObject(),null); return (page.Frames,page.Geometry); }
        public static string? PickerFrame(OpenCodeBrowserPage page,string? session,int context) => page.Frames.ContextFrame(session,context);
        public static void CheckPickedRef(OpenCodeBrowserPage page,string reference) { if(!reference.StartsWith("fixture-ref-"))throw new InvalidOperationException("Unexpected fixture ref"); }
    }
}
namespace AIFrontend.Pages.OpenCode
{
    internal sealed class OpenCodeBrowserPreviewView : Grid
    {
        internal void Bind(AIFrontend.Services.OpenCode.Browser.OpenCodeBrowserWorkspace? workspace,AIFrontend.Services.OpenCode.Browser.OpenCodeBrowserPage? page) { }
        internal void UpdateActions() { } internal Task ReadAsync()=>Task.CompletedTask;
    }
    internal sealed class OpenCodeBrowserExternalView : Grid { internal Func<Task>? PickElementAsync {get;set;} internal void Bind(AIFrontend.Services.OpenCode.Browser.OpenCodeBrowserPage? page) { } }
    internal static class OpenCodeInteractionUi { internal const string CodeStyleKey="";internal static Style? TryStyle(string key)=>null; }
    internal static class OpenCodeToolTheme { internal static void Apply(FrameworkElement element,object style) { } internal static object Text(string key,string? style=null)=>new();internal static object Icon(string key)=>new(); }
    internal static class OpenCodeAgentIcons { internal static string Glyph(string name)=>"\uE774"; }
    internal static class OpenCodeToolDetailPopup
    {
        // Exact production CreatePresenter geometry/material setup, without unrelated tool detail surfaces.
        internal static FlyoutPresenter CreatePresenter(UIElement content,Thickness padding,float elevation)
        {
            var presenter=new FlyoutPresenter { Content=content,Padding=padding,MinWidth=0,MinHeight=0,MaxWidth=double.PositiveInfinity,MaxHeight=double.PositiveInfinity,IsTabStop=false,Translation=new(0,0,elevation),Shadow=new Microsoft.UI.Xaml.Media.ThemeShadow() };
            ScrollViewer.SetVerticalScrollMode(presenter,ScrollMode.Disabled);ScrollViewer.SetVerticalScrollBarVisibility(presenter,ScrollBarVisibility.Disabled);
            ScrollViewer.SetHorizontalScrollMode(presenter,ScrollMode.Disabled);ScrollViewer.SetHorizontalScrollBarVisibility(presenter,ScrollBarVisibility.Disabled);return presenter;
        }
    }
    internal static class OpenCodeBrowserSelectionFiles
    {
        internal static Task CopyAsync(byte[] bytes) => Task.CompletedTask;
        internal static Task SaveAsync(byte[] bytes, XamlRoot root) => Task.CompletedTask;
    }
}
