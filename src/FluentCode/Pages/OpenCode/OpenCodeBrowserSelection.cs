using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FluentCode.Services.OpenCode.Browser;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace FluentCode.Pages.OpenCode;

/// <summary>UI-thread selection owner. All DOM/ref/capture operations serialize with the agent's ActionGate.</summary>
internal sealed class OpenCodeBrowserSelection : IDisposable
{
    private readonly OpenCodeBrowserOverlay _overlay;
    private readonly Func<OpenCodeBrowserPage, int, long, string?, string> _register;
    private readonly Func<OpenCodeBrowserPage, IReadOnlyList<BrowserSelectionAttachment>, Task<bool>> _attach;
    private readonly OpenCodeBrowserSelectedList _list = new();
    private readonly Dictionary<OpenCodeBrowserSelectionTool, ToggleButton> _buttons = new();
    private readonly Border _pill;
    private readonly TextBlock _modeLabel = new();
    private readonly FontIcon _modeIcon = new() { FontSize = 14 };
    private readonly StackPanel _annotationTools = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly TextBox _note = new() { PlaceholderText = "批注文字", Width = 220, MaxLength = 2000 };
    private readonly Dictionary<OpenCodeBrowserPage, PageState> _states = new();
    private OpenCodeBrowserPage? _page;
    private OpenCodeBrowserSelectionTool _tool;
    private string _nonce = "", _pen = "pen", _color = "#F43F5E";
    private bool _disposed, _working;
    private long _activation;
    private sealed class PageState
    {
        internal readonly Dictionary<string, string> Scripts = new(StringComparer.Ordinal);
        internal readonly List<IAsyncDisposable> Subscriptions = new();
        internal readonly Dictionary<string, Task> Sessions = new(StringComparer.Ordinal);
        internal bool Subscribed, Multi;
        internal readonly OpenCodeBrowserSelectionList List = new();
        internal readonly Dictionary<Guid, OpenCodeBrowserSelectionViewport> Viewports = new();
        internal long Generation;
        internal string? DraftKey;
        internal Guid DraftItem;
    }
    internal OpenCodeBrowserSelection(OpenCodeBrowserOverlay overlay,
        Func<OpenCodeBrowserPage, int, long, string?, string> register,
        Func<OpenCodeBrowserPage, IReadOnlyList<BrowserSelectionAttachment>, Task<bool>> attach)
    {
        _overlay = overlay; _register = register; _attach = attach;
        overlay.PageTools.Children.Clear();
        foreach (var entry in new[] { (OpenCodeBrowserSelectionTool.Element, "选择元素并批注", "\uE8B0"), (OpenCodeBrowserSelectionTool.Region, "框选并批注", "\uEF20") })
        {
            var tool = entry.Item1;
            var button = new ToggleButton { Content = new FontIcon { Glyph = entry.Item3, FontSize = 16 }, MinWidth = 0 };
            AutomationProperties.SetName(button, entry.Item2); ToolTipService.SetToolTip(button, entry.Item2);
            button.Click += (_, _) => Run(() => ActivateAsync(button.IsChecked == true ? tool : OpenCodeBrowserSelectionTool.None));
            _buttons.Add(tool, button); overlay.PageTools.Children.Add(button);
        }
        overlay.RightPanelSlot.Children.Add(_list);
        _list.Removed += id => { State?.List.Remove(id); State?.Viewports.Remove(id); Render(); Run(SyncSelectedAsync); };
        _list.NoteChanged += (id, text) => State?.List.SetNote(id, text);
        _list.AddRequested += () => Run(AddToConversationAsync);
        _list.CopyRequested += bytes => Run(async () =>
        {
            await OpenCodeBrowserSelectionFiles.CopyAsync(bytes);
            FluentCode.Motion.ActionFeedback.Announce(overlay, "已复制图片"); // Context menu: no visible acknowledgement.
        });
        _list.SaveRequested += bytes => Run(async () =>
        {
            if (await OpenCodeBrowserSelectionFiles.SaveAsync(bytes, overlay.XamlRoot)) FluentCode.Motion.ActionFeedback.Announce(overlay, "已保存截图");
        });
        // Same floating-pill language as the agent activity pill: acrylic, hairline, elevation.
        var pillRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        _modeIcon.VerticalAlignment = VerticalAlignment.Center;
        _modeIcon.Style = OpenCodeToolTheme.Icon("AccentTextFillColorPrimaryBrush");
        pillRow.Children.Add(_modeIcon);
        _modeLabel.VerticalAlignment = VerticalAlignment.Center; _modeLabel.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; pillRow.Children.Add(_modeLabel);
        BuildAnnotationTools(); pillRow.Children.Add(_annotationTools);
        var done = new Button { Content = "完成", Height = 32, MinWidth = 72, CornerRadius = new(16), Margin = new(4, 0, 0, 0) };
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accent)) done.Style = (Style)accent;
        done.Click += (_, _) => Run(FinishAsync); pillRow.Children.Add(done);
        _pill = new Border { Child = pillRow, Height = 44, CornerRadius = new(22), Padding = new(16, 0, 6, 0), Margin = new(16), BorderThickness = new(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed,
            Shadow = new ThemeShadow(), Translation = new System.Numerics.Vector3(0, 0, 32) };
        _pill.Style = (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"Border\"><Setter Property=\"Background\" Value=\"{ThemeResource AcrylicInAppFillColorDefaultBrush}\"/><Setter Property=\"BorderBrush\" Value=\"{ThemeResource FlyoutBorderThemeBrush}\"/></Style>");
        overlay.PageToolSurface.Children.Add(_pill);
        overlay.SelectionBindingChanged += BindingChanged; overlay.AddPageRequested += AddPage;
        overlay.ExitPageTool = Exit;
        overlay.PickExternalElementAsync = () => ActivateAsync(OpenCodeBrowserSelectionTool.Element);
    }
    private PageState? State => _page is not null && _states.TryGetValue(_page, out var state) ? state : null;
    private void BuildAnnotationTools()
    {
        var pens = new ComboBox { Width = 88, SelectedIndex = 0 };
        foreach (var text in new[] { "画笔", "箭头", "矩形", "批注" }) pens.Items.Add(text);
        pens.SelectionChanged += (_, _) => { _pen = new[] { "pen", "arrow", "rectangle", "note" }[Math.Max(0, pens.SelectedIndex)]; _note.Visibility = _pen == "note" ? Visibility.Visible : Visibility.Collapsed; Run(SettingsAsync); };
        _annotationTools.Children.Add(pens);
        var colors = new ComboBox { Width = 72, SelectedIndex = 0 };
        foreach (var pair in new[] { ("红色", "#F43F5E"), ("黄色", "#F59E0B"), ("蓝色", "#3B82F6"), ("黑色", "#1F2937") })
            colors.Items.Add(new ComboBoxItem { Content = pair.Item1, Tag = pair.Item2 });
        colors.SelectionChanged += (_, _) => { _color = (colors.SelectedItem as ComboBoxItem)?.Tag as string ?? "#F43F5E"; Run(SettingsAsync); };
        _annotationTools.Children.Add(colors); _note.Visibility = Visibility.Collapsed; _note.TextChanged += (_, _) => Run(SettingsAsync); _annotationTools.Children.Add(_note);
        var undo = new Button { Content = "撤销" }; undo.Click += (_, _) => Run(() => ScriptAsync("undo()")); _annotationTools.Children.Add(undo);
    }
    private void BindingChanged(object? sender, EventArgs args)
    {
        if (_page == _overlay.Page) return;
        var previous = _page; _activation++; _tool = OpenCodeBrowserSelectionTool.None;
        if (previous is not null && !previous.Closed) Run(async () => { await previous.ActionGate.WaitAsync(previous.Lifetime); try { await SetInspectModeAsync(previous, false); await previous.ExecuteScriptAsync("window.__aifSelection?.activate('None','')"); } finally { previous.ActionGate.Release(); } });
        _page = _overlay.Page;
        if (_page is not null && !_states.ContainsKey(_page))
        {
            _states.Add(_page, new() { Generation = _page.Generation });
            _page.Committed += Committed; _page.Closing += PageClosing;
            Run(() => InstallAsync(_page));
        }
        Render();
    }
    private Task InstallAsync(OpenCodeBrowserPage? page)
        => page is null ? Task.CompletedTask : WithPageGateAsync(page, () => InstallCoreAsync(page));
    private static async Task WithPageGateAsync(OpenCodeBrowserPage page, Func<Task> action)
    {
        await page.ActionGate.WaitAsync(page.Lifetime);
        try { if (!page.Closed) await action(); }
        finally { page.ActionGate.Release(); }
    }
    private async Task InstallCoreAsync(OpenCodeBrowserPage page)
    {
        if (page.Closed || !_states.TryGetValue(page, out var state)) return;
        await page.EnsureReadyAsync();
        if (_disposed || page.Closed || !_states.ContainsKey(page)) return;
        var context = await OpenCodeBrowserSessionBackend.PickerContextAsync(page, page.Lifetime);
        if (!state.Subscribed)
        {
            state.Subscribed = true;
            state.Subscriptions.Add(await page.SubscribeCdpAsync("Runtime.bindingCalled", message =>
            {
                if (message.Parameters["name"]?.ToString() != "__aifSelectionSend") return;
                Run(() => MessageReceivedAsync(page, message));
            }, page.Lifetime));
            state.Subscriptions.Add(await page.SubscribeCdpAsync("Overlay.inspectNodeRequested", message => Run(() => InspectNodeAsync(page, message)), page.Lifetime));
            state.Subscriptions.Add(await page.SubscribeCdpAsync("Overlay.inspectModeCanceled", _ => { if (page == _page && _tool == OpenCodeBrowserSelectionTool.Element) Exit(); }, page.Lifetime));
            state.Subscriptions.Add(await page.SubscribeCdpAsync("Target.attachedToTarget", message =>
            {
                if (message.Parameters["targetInfo"]?["type"]?.ToString() == "iframe") Run(() => InstallAsync(page));
            }, page.Lifetime));
            state.Subscriptions.Add(await page.SubscribeCdpAsync("Target.detachedFromTarget", message =>
            {
                var session = message.Parameters["sessionId"]!.ToString(); state.Sessions.Remove(session); state.Scripts.Remove(session);
            }, page.Lifetime));
            state.Subscriptions.Add(await page.SubscribeCdpAsync("Runtime.executionContextCreated", message =>
            {
                var created = message.Parameters["context"]!;
                if (created["auxData"]?["isDefault"]?.GetValue<bool>() == true)
                    Run(() => WithPageGateAsync(page, () => InstallContextAsync(page, message.SessionId, created["id"]!.GetValue<int>())));
            }, page.Lifetime));
        }
        foreach (var session in context.Frames.Items.Select(f => f.RendererSessionId).Distinct()) await InstallSessionAsync(page, state, session);
        foreach (var frame in context.Frames.Items.ToArray())
            if (context.Frames.Context(frame.Id) is { } id) await InstallContextAsync(page, frame.RendererSessionId, id);
    }
    private Task InstallSessionAsync(OpenCodeBrowserPage page, PageState state, string? session)
        => state.Sessions.TryGetValue(session ?? "", out var task) ? task : state.Sessions[session ?? ""] = InstallSessionCoreAsync(page, state, session);
    private async Task InstallSessionCoreAsync(OpenCodeBrowserPage page, PageState state, string? session)
    {
        await page.CdpAsync("Runtime.addBinding", new() { ["name"] = "__aifSelectionSend" }, session);
        var script = await page.CdpAsync("Page.addScriptToEvaluateOnNewDocument", new() { ["source"] = OpenCodeBrowserSelectionScript.Source }, session);
        state.Scripts[session ?? ""] = script["identifier"]!.ToString();
        await page.CdpAsync("DOM.enable", null, session); await page.CdpAsync("Overlay.enable", null, session);
        await InspectModeAsync(page, session, page == _page && _tool == OpenCodeBrowserSelectionTool.Element);
    }
    private async Task InstallContextAsync(OpenCodeBrowserPage page, string? session, int id)
    {
        if (_disposed || page.Closed || !_states.TryGetValue(page, out var state)) return;
        await InstallSessionAsync(page, state, session);
        await page.CdpAsync("Runtime.evaluate", new() { ["expression"] = OpenCodeBrowserSelectionScript.Source, ["contextId"] = id }, session);
        if (page != _page || _tool == OpenCodeBrowserSelectionTool.None) return;
        var frames = (await OpenCodeBrowserSessionBackend.PickerContextAsync(page, page.Lifetime)).Frames;
        var frame = frames.ContextFrame(session, id);
        var mode = _tool == OpenCodeBrowserSelectionTool.Element || frame == frames.RootId ? _tool.ToString() : "None";
        await page.CdpAsync("Runtime.evaluate", new() { ["expression"] = $"window.__aifSelection.activate({JsonSerializer.Serialize(mode)},{JsonSerializer.Serialize(_nonce)})", ["contextId"] = id }, session);
        await InspectModeAsync(page, session, _tool == OpenCodeBrowserSelectionTool.Element);
    }
    private static Task<JsonObject> InspectModeAsync(OpenCodeBrowserPage page, string? session, bool enabled)
        => page.CdpAsync("Overlay.setInspectMode", new()
        {
            ["mode"] = enabled ? "searchForNode" : "none",
            ["highlightConfig"] = new JsonObject { ["showInfo"] = true, ["contentColor"] = new JsonObject { ["r"] = 56, ["g"] = 189, ["b"] = 248, ["a"] = .25 }, ["borderColor"] = new JsonObject { ["r"] = 56, ["g"] = 189, ["b"] = 248, ["a"] = 1 } }
        }, session);
    private async Task SetInspectModeAsync(OpenCodeBrowserPage page, bool enabled)
    {
        if (!_states.TryGetValue(page, out var state)) return;
        foreach (var session in state.Sessions.Keys.ToArray()) await InspectModeAsync(page, session.Length == 0 ? null : session, enabled);
    }
    private void Committed(object? sender, EventArgs args)
    {
        if (sender is not OpenCodeBrowserPage page || !_states.TryGetValue(page, out var state)) return;
        state.Generation = page.Generation; state.List.Clear(); state.Viewports.Clear();
        if (page == _page) { _activation++; _tool = OpenCodeBrowserSelectionTool.None; Render(); }
    }
    private void PageClosing(object? sender, EventArgs args)
    {
        if (sender is not OpenCodeBrowserPage page) return;
        page.Committed -= Committed; page.Closing -= PageClosing;
        if (_states.TryGetValue(page, out var state)) _ = DisposePageAsync(page, state);
        _states.Remove(page); if (_page == page) { _page = null; _tool = OpenCodeBrowserSelectionTool.None; Render(); }
    }
    private bool Exit()
    {
        if (!OpenCodeBrowserSelectionPolicy.EscapeExitsTool(_tool)) return false;
        Run(() => ActivateAsync(OpenCodeBrowserSelectionTool.None)); return true;
    }
    private async Task ActivateAsync(OpenCodeBrowserSelectionTool tool)
    {
        var page = _page; if (page is null || !OpenCodeBrowserSelectionPolicy.Offers(tool, page.PreviewPath is not null)) return;
        var activation = ++_activation; _tool = tool; _nonce = Guid.NewGuid().ToString("N"); Render();
        if (_states.TryGetValue(page, out var state)) state.Multi = false;
        await page.ActionGate.WaitAsync(page.Lifetime);
        try
        {
            if (_page != page || activation != _activation) return;
            await InstallCoreAsync(page);
            var frames = (await OpenCodeBrowserSessionBackend.PickerContextAsync(page, page.Lifetime)).Frames;
            foreach (var frame in frames.Items.ToArray())
                if (frames.Context(frame.Id) is { } id) await InstallContextAsync(page, frame.RendererSessionId, id);
            await SetInspectModeAsync(page, tool == OpenCodeBrowserSelectionTool.Element);
            if (tool == OpenCodeBrowserSelectionTool.None) await page.ExecuteScriptAsync("window.__aifSelection?.activate('None','')");
            await SyncSelectedCoreAsync(page); await SettingsCoreAsync(page);
        }
        finally { page.ActionGate.Release(); }
    }
    private void Render()
    {
        foreach (var pair in _buttons) { pair.Value.IsChecked = _tool == pair.Key; pair.Value.Visibility = OpenCodeBrowserSelectionPolicy.Offers(pair.Key, _page?.PreviewPath is not null) ? Visibility.Visible : Visibility.Collapsed; }
        _pill.Visibility = _tool == OpenCodeBrowserSelectionTool.None ? Visibility.Collapsed : Visibility.Visible;
        _annotationTools.Visibility = _tool == OpenCodeBrowserSelectionTool.Annotate ? Visibility.Visible : Visibility.Collapsed;
        _modeLabel.Text = _tool switch { OpenCodeBrowserSelectionTool.Element => "选择元素", OpenCodeBrowserSelectionTool.Region => "框选", OpenCodeBrowserSelectionTool.Annotate => "标注", _ => "截图" };
        _modeIcon.Glyph = _tool switch { OpenCodeBrowserSelectionTool.Element => "\uE8B0", OpenCodeBrowserSelectionTool.Region => "\uEF20", OpenCodeBrowserSelectionTool.Annotate => "\uE70F", _ => "\uE722" };
        if (State is { } state) _list.Render(state.List); else _list.Visibility = Visibility.Collapsed;
    }
    private async Task MessageReceivedAsync(OpenCodeBrowserPage page, OpenCodeBrowserCdpEvent args)
    {
        if (_disposed || page != _page || page.Closed || _tool == OpenCodeBrowserSelectionTool.None) return;
        try
        {
            var message = JsonNode.Parse(args.Parameters["payload"]!.GetValue<string>()) as JsonObject;
            if (message?["aifSelection"]?.GetValue<string>() != _nonce) return;
            var frame = OpenCodeBrowserSessionBackend.PickerFrame(page, args.SessionId, args.Parameters["executionContextId"]!.GetValue<int>());
            if (frame is null) return;
            var type = message["type"]?.GetValue<string>();
            if (type == "escape") { Exit(); return; }
            if (type == "modifier") { _states[page].Multi = message["multi"]?.GetValue<bool>() == true; return; }
            if (type is "scrolled" or "resized") { Run(SyncSelectedAsync); return; } // Only outlines move; captured metadata stays a snapshot.
            if (_working) return;
            _working = true; var activation = _activation; var generation = page.Generation;
            await page.ActionGate.WaitAsync(page.Lifetime);
            try
            {
                if (_page != page || activation != _activation || page.Generation != generation) return;
                var context = await OpenCodeBrowserSessionBackend.PickerContextAsync(page, page.Lifetime);
                if (_page != page || activation != _activation || page.Generation != generation) return;
                if (type == "region" && frame == context.Frames.RootId && _tool is OpenCodeBrowserSelectionTool.Region or OpenCodeBrowserSelectionTool.Screenshot) await SelectRegionAsync(page, message!, generation, activation);
            }
            finally { page.ActionGate.Release(); _working = false; }
            if (_page == page) { Render(); await SyncSelectedAsync(); }
        }
        catch (Exception ex) { _working = false; Error(ex); }
    }
    private async Task InspectNodeAsync(OpenCodeBrowserPage page, OpenCodeBrowserCdpEvent message)
    {
        if (_disposed || page != _page || page.Closed || _tool != OpenCodeBrowserSelectionTool.Element || _working) return;
        var activation = _activation; var generation = page.Generation; var multi = _states[page].Multi; _working = true;
        try { await page.ActionGate.WaitAsync(page.Lifetime); }
        catch { _working = false; throw; }
        try
        {
            if (page != _page || activation != _activation || page.Generation != generation) return;
            var backendId = message.Parameters["backendNodeId"]!.GetValue<int>();
            backendId = await ElementBackendAsync(page, backendId, message.SessionId);
            var context = await OpenCodeBrowserSessionBackend.PickerContextAsync(page, page.Lifetime);
            string? frameId = null;
            foreach (var frame in context.Frames.Items.Where(f => f.RendererSessionId == message.SessionId).ToArray())
            {
                if (context.Frames.Context(frame.Id) is not { } id) continue;
                var resolved = await page.CdpAsync("DOM.resolveNode", new() { ["backendNodeId"] = backendId, ["executionContextId"] = id }, message.SessionId);
                var objectId = resolved["object"]!["objectId"]!.ToString();
                try
                {
                    var owns = await page.CdpAsync("Runtime.callFunctionOn", new() { ["objectId"] = objectId, ["returnByValue"] = true, ["functionDeclaration"] = "function(){return this.ownerDocument===document}" }, message.SessionId);
                    if (owns["result"]?["value"]?.GetValue<bool>() == true) { frameId = frame.Id; break; }
                }
                finally { await page.CdpAsync("Runtime.releaseObject", new() { ["objectId"] = objectId }, message.SessionId); }
            }
            if (frameId is null) throw new InvalidOperationException("所选元素的框架已导航，请重新选择。");
            if (_page != page || activation != _activation || page.Generation != generation) return;
            await SelectElementAsync(page, backendId, frameId, multi, generation, activation);
        }
        finally { page.ActionGate.Release(); _working = false; }
        if (page == _page && activation == _activation) { Render(); await SyncSelectedAsync(); await SetInspectModeAsync(page, _tool == OpenCodeBrowserSelectionTool.Element); }
    }
    private static async Task<int> ElementBackendAsync(OpenCodeBrowserPage page, int backendId, string? session)
    {
        var node = await page.CdpAsync("DOM.describeNode", new() { ["backendNodeId"] = backendId }, session);
        if (node["node"]!["nodeType"]!.GetValue<int>() == 1) return backendId;
        var resolved = await page.CdpAsync("DOM.resolveNode", new() { ["backendNodeId"] = backendId }, session);
        var objectId = resolved["object"]!["objectId"]!.ToString();
        try
        {
            var element = await page.CdpAsync("Runtime.callFunctionOn", new() { ["objectId"] = objectId, ["functionDeclaration"] = "function(){return this.parentElement}" }, session);
            var parentId = element["result"]?["objectId"]?.ToString() ?? throw new InvalidOperationException("此节点不是可选择的页面元素。");
            try { return (await page.CdpAsync("DOM.describeNode", new() { ["objectId"] = parentId }, session))["node"]!["backendNodeId"]!.GetValue<int>(); }
            finally { await page.CdpAsync("Runtime.releaseObject", new() { ["objectId"] = parentId }, session); }
        }
        finally { await page.CdpAsync("Runtime.releaseObject", new() { ["objectId"] = objectId }, session); }
    }
    private async Task SelectElementAsync(OpenCodeBrowserPage page, int backendId, string frame, bool multi, long generation, long activation)
    {
        var state = _states[page]; var list = state.List;
        var old = list.Items.FirstOrDefault(i => i.Kind == OpenCodeBrowserSelectionKind.Element && i.Elements.Any(e => e.BackendNodeId == backendId && e.FrameId == frame));
        if (old is not null) { list.Remove(old.Id); state.Viewports.Remove(old.Id); return; }
        var viewport = await ReadViewportAsync(page);
        var element = await ReadElementAsync(page, backendId, generation, frame);
        await CheckViewportAsync(page, viewport);
        var image = await CaptureAsync(page, element.Box);
        await CheckViewportAsync(page, viewport);
        OpenCodeBrowserSessionBackend.CheckPickedRef(page, element.Ref);
        if (page.Generation != generation) throw new InvalidOperationException("页面已跳转，请重新选择元素。");
        if (_page != page || activation != _activation) return;
        if (!multi)
            foreach (var item in list.Items.Where(i => i.Kind == OpenCodeBrowserSelectionKind.Element).ToArray()) { list.Remove(item.Id); state.Viewports.Remove(item.Id); }
        var added = list.Add(OpenCodeBrowserSelectionKind.Element, element.Selector, element.Box, image, new[] { element });
        state.Viewports[added.Id] = viewport;
    }
    private async Task SelectRegionAsync(OpenCodeBrowserPage page, JsonObject message, long generation, long activation)
    {
        var kind = _tool == OpenCodeBrowserSelectionTool.Screenshot ? OpenCodeBrowserSelectionKind.Screenshot : OpenCodeBrowserSelectionKind.Region;
        var b = message["box"]!; var box = new BrowserRect(N(b, "x"), N(b, "y"), N(b, "width"), N(b, "height"));
        var captureViewport = await ReadViewportAsync(page);
        if (message["viewport"] is JsonObject sent && !captureViewport.SameView(OpenCodeBrowserSelectionViewport.Read(sent)))
            throw new InvalidOperationException("页面已滚动或改变大小，请重新框选。");
        var elements = new List<BrowserSelectedElement>();
        var context = await OpenCodeBrowserSessionBackend.PickerContextAsync(page, page.Lifetime);
        foreach (var frame in context.Frames.Items.ToArray())
        {
            if (context.Frames.Context(frame.Id) is not { } executionContext) throw new InvalidOperationException("区域中的框架尚未就绪，请稍后重试。");
            var candidates = await page.CdpAsync("Runtime.evaluate", new() { ["expression"] = OpenCodeBrowserSelectionScript.Candidates, ["contextId"] = executionContext, ["returnByValue"] = true }, frame.RendererSessionId);
            context.Frames.Check(new(1, frame.Id, frame.RendererSessionId, frame.DocumentEpoch));
            var values = candidates["result"]?["value"]?.AsArray() ?? throw new InvalidOperationException("无法枚举区域中的元素。");
            var viewport = await context.Geometry.ViewportAsync(frame.Id, page.Lifetime);
            var document = await page.CdpAsync("Runtime.evaluate", new() { ["expression"] = "document", ["contextId"] = executionContext }, frame.RendererSessionId);
            var objectId = document["result"]!["objectId"]!.ToString();
            try
            {
                foreach (var candidate in values.OfType<JsonObject>())
                {
                    var local = new OpenCodeBrowserBox(N(candidate, "x"), N(candidate, "y"), N(candidate, "width"), N(candidate, "height"));
                    var projected = OpenCodeBrowserGeometry.Bounds(OpenCodeBrowserGeometry.ProjectQuad(OpenCodeBrowserGeometry.Quad(local), viewport.Quad, viewport.Width, viewport.Height));
                    if (!OpenCodeBrowserSelectionPolicy.ContainsElement(box, new(projected.X, projected.Y, projected.Width, projected.Height))) continue;
                    if (elements.Count >= 200) throw new InvalidOperationException("此区域包含超过 200 个元素，请缩小选择范围。");
                    // DOM.requestNode(document) can return frontend nodeId 0 until DOM.getDocument
                    // has populated that renderer's frontend tree. Use the already frame-scoped
                    // document object instead; backend IDs do not depend on frontend tree discovery.
                    var found = await page.CdpAsync("Runtime.callFunctionOn", new() { ["objectId"] = objectId,
                        ["functionDeclaration"] = "function(selector){return this.querySelector(selector)}",
                        ["arguments"] = new JsonArray(new JsonObject { ["value"] = candidate["selector"]!.ToString() }) }, frame.RendererSessionId);
                    if (found["result"]?["objectId"]?.GetValue<string>() is not { } elementObject) continue;
                    try
                    {
                        var described = await page.CdpAsync("DOM.describeNode", new() { ["objectId"] = elementObject }, frame.RendererSessionId);
                        elements.Add(await ReadElementAsync(page, described["node"]!["backendNodeId"]!.GetValue<int>(), generation, frame.Id));
                    }
                    finally { await page.CdpAsync("Runtime.releaseObject", new() { ["objectId"] = elementObject }, frame.RendererSessionId); }
                }
            }
            finally { await page.CdpAsync("Runtime.releaseObject", new() { ["objectId"] = objectId }, frame.RendererSessionId); }
        }
        await CheckViewportAsync(page, captureViewport);
        var image = await CaptureAsync(page, box);
        await CheckViewportAsync(page, captureViewport);
        foreach (var element in elements) OpenCodeBrowserSessionBackend.CheckPickedRef(page, element.Ref);
        if (page.Generation != generation) throw new InvalidOperationException("页面已跳转，请重新选择区域。");
        if (_page != page || activation != _activation) return;
        var state = _states[page]; var key = message["key"]?.GetValue<string>();
        var description = $"{box.Width:0} × {box.Height:0} · 包含 {elements.Count} 个元素";
        if (key is not null && key == state.DraftKey && state.List.Items.Any(i => i.Id == state.DraftItem))
            state.List.Update(state.DraftItem, box, image, elements, description);
        else { var item = state.List.Add(kind, description, box, image, elements); state.DraftKey = key; state.DraftItem = item.Id; }
        state.Viewports[state.DraftItem] = captureViewport;
    }
    private async Task<BrowserSelectedElement> ReadElementAsync(OpenCodeBrowserPage page, int backendId, long generation, string? frame)
    {
        var context = await OpenCodeBrowserSessionBackend.PickerContextAsync(page, page.Lifetime);
        var identity = context.Frames.Element(backendId, frame);
        var projected = await context.Geometry.BoxAsync(identity, true, page.Lifetime);
        var box = new BrowserRect(projected.X, projected.Y, projected.Width, projected.Height);
        var parameters = new JsonObject { ["backendNodeId"] = backendId };
        if (context.Frames.Context(identity.FrameId) is { } id) parameters["executionContextId"] = id;
        var resolved = await page.CdpAsync("DOM.resolveNode", parameters, identity.RendererSessionId); var objectId = resolved["object"]!["objectId"]!.GetValue<string>();
        try
        {
            var result = await page.CdpAsync("Runtime.callFunctionOn", new() { ["objectId"] = objectId, ["returnByValue"] = true, ["functionDeclaration"] = "function(){let s=getComputedStyle(this);let parts=[],e=this;while(e&&e!==e.ownerDocument.documentElement){let n=e.localName;if(e.id){parts.unshift('#'+CSS.escape(e.id));break}let p=e.parentElement;if(p){let peers=[...p.children].filter(x=>x.localName===n);if(peers.length>1)n+=':nth-of-type('+(peers.indexOf(e)+1)+')'}parts.unshift(n);e=p;}return {selector:parts.join(' > '),text:(this.innerText||this.textContent||'').slice(0,4000),value:this.type==='password'?'[密码已隐藏]':typeof this.value==='string'?this.value.slice(0,4000):null,styles:['font','color','background-color','padding','margin','border-radius','display','position'].map(k=>k+': '+s.getPropertyValue(k)).join('; ')}}" }, identity.RendererSessionId);
            var value = result["result"]?["value"] ?? throw new InvalidOperationException("无法读取此元素的信息。");
            context.Frames.Check(identity);
            var reference = _register(page, backendId, generation, frame);
            return new(value["selector"]?.GetValue<string>() ?? "element", value["text"]?.GetValue<string>() ?? "", value["value"]?.GetValue<string>(), box, value["styles"]?.GetValue<string>() ?? "", reference, frame, backendId);
        }
        finally { await page.CdpAsync("Runtime.releaseObject", new() { ["objectId"] = objectId }, identity.RendererSessionId); }
    }
    private async Task<byte[]> CaptureAsync(OpenCodeBrowserPage page, BrowserRect? region = null, bool fullPage = false, bool annotations = false)
    {
        await SetInspectModeAsync(page, false);
        await page.ExecuteScriptAsync($"window.__aifSelection?.capture({(annotations ? "true" : "false")})");
        try
        {
            var metrics = await page.CdpAsync("Page.getLayoutMetrics"); var viewport = metrics["cssVisualViewport"]!; var content = metrics["cssContentSize"]!;
            var box = region ?? (fullPage ? new(0, 0, N(content, "width"), N(content, "height")) : new BrowserRect(0, 0, N(viewport, "clientWidth"), N(viewport, "clientHeight")));
            var clip = fullPage ? new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = box.Width, ["height"] = box.Height, ["scale"] = 1 }
                : OpenCodeBrowserGeometry.ScreenshotClip(new(box.X, box.Y, box.Width, box.Height), metrics);
            if (N(clip, "width") <= 0 || N(clip, "height") <= 0 || N(clip, "width") * N(clip, "height") > 64000000) throw new InvalidOperationException("截图区域为空或超过 6400 万像素，请缩小区域。");
            // Beyond-viewport capture hides Chromium scrollbars and can reflow the live page.
            // Ordinary selection crops must stay within the unchanged viewport (also for oversized elements).
            var result = await page.CdpAsync("Page.captureScreenshot", new() { ["format"] = "png", ["captureBeyondViewport"] = fullPage, ["clip"] = clip });
            return Convert.FromBase64String(result["data"]!.GetValue<string>());
        }
        finally
        {
            if (!page.Closed)
            {
                await page.ExecuteScriptAsync("window.__aifSelection?.restore()");
                await page.CdpAsync("Runtime.evaluate", new() { ["expression"] = "document.hidden ? null : new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))", ["awaitPromise"] = true });
                if (page == _page) await SetInspectModeAsync(page, _tool == OpenCodeBrowserSelectionTool.Element);
            }
        }
    }
    private async Task CaptureStandaloneAsync(bool fullPage)
    {
        await ActivateAsync(OpenCodeBrowserSelectionTool.None);
        var page = _page; if (page is null) return; var generation = page.Generation; await page.ActionGate.WaitAsync(page.Lifetime);
        try { var box = await CaptureBoxAsync(page, fullPage); var image = await CaptureAsync(page, fullPage: fullPage); if (page.Generation == generation && _states.TryGetValue(page, out var state)) state.List.Add(OpenCodeBrowserSelectionKind.Screenshot, fullPage ? "整个页面" : "可见区域", box, image, Array.Empty<BrowserSelectedElement>()); }
        finally { page.ActionGate.Release(); }
        if (_page == page) Render();
    }
    private async Task CaptureSelectedElementsAsync()
    {
        var page = _page; if (page is null || State is not { } state) return;
        var elements = state.List.Items.SelectMany(i => i.Elements).DistinctBy(e => e.Ref).ToArray();
        if (elements.Length == 0) throw new InvalidOperationException("请先选择元素。");
        var generation = page.Generation;
        await page.ActionGate.WaitAsync(page.Lifetime);
        try
        {
            // Resolve current boxes after scrolling/reflow rather than clipping at old snapshot coordinates.
            var fresh = new List<BrowserSelectedElement>();
            foreach (var element in elements)
            {
                OpenCodeBrowserSessionBackend.CheckPickedRef(page, element.Ref);
                fresh.Add(await ReadElementAsync(page, element.BackendNodeId, generation, element.FrameId));
            }
            var x = fresh.Min(e => e.Box.X); var y = fresh.Min(e => e.Box.Y);
            var box = new BrowserRect(x, y, fresh.Max(e => e.Box.X + e.Box.Width) - x, fresh.Max(e => e.Box.Y + e.Box.Height) - y);
            var image = await CaptureAsync(page, box);
            if (page.Generation == generation && _states.ContainsKey(page)) state.List.Add(OpenCodeBrowserSelectionKind.Screenshot, "选中的元素", box, image, fresh);
        }
        finally { page.ActionGate.Release(); }
        if (_page == page) Render();
    }
    private async Task FinishAsync()
    {
        var page = _page; if (page is null) return;
        if (_tool == OpenCodeBrowserSelectionTool.Annotate)
        {
            await page.ActionGate.WaitAsync(page.Lifetime);
            try
            {
                var text = JsonSerializer.Deserialize<string>(await page.ExecuteScriptAsync("window.__aifSelection.finish()")) ?? "";
                var box = await CaptureBoxAsync(page, false); var generation = page.Generation;
                var image = await CaptureAsync(page, annotations: true);
                if (page.Generation == generation && _states.TryGetValue(page, out var state)) state.List.Add(OpenCodeBrowserSelectionKind.Annotation, "标注", box, image, Array.Empty<BrowserSelectedElement>(), text);
            }
            finally { page.ActionGate.Release(); }
        }
        if (_page == page) await ActivateAsync(OpenCodeBrowserSelectionTool.None);
    }
    private async void AddPage(object? sender, OpenCodeBrowserPage page) { if (_page == page) await RunTask(() => AddPageAsync(page)); }
    private async Task AddPageAsync(OpenCodeBrowserPage page)
    {
        var generation = page.Generation; byte[] image;
        await page.ActionGate.WaitAsync(page.Lifetime);
        try { image = await CaptureAsync(page, fullPage: true); } finally { page.ActionGate.Release(); }
        if (_page != page || page.Generation != generation) return;
        var item = new BrowserSelectionItem(Guid.NewGuid(), 0, OpenCodeBrowserSelectionKind.Page, page.Url, new(), image, Array.Empty<BrowserSelectedElement>());
        await _attach(page, new[] { OpenCodeBrowserSelectionPolicy.Attachment(page.Url, page.Id, generation, item, false) });
    }
    private async Task AddToConversationAsync()
    {
        var page = _page; var state = State; if (page is null || state is null || state.List.Items.Count == 0) return;
        var generation = page.Generation; var items = state.List.Items.ToArray(); var attachments = new List<BrowserSelectionAttachment>();
        if (_list.FullPage.IsChecked == true)
        {
            await page.ActionGate.WaitAsync(page.Lifetime);
            try
            {
                var image = await CaptureAsync(page, fullPage: true);
                var item = new BrowserSelectionItem(Guid.NewGuid(), 0, OpenCodeBrowserSelectionKind.Page, page.Url, new(), image, Array.Empty<BrowserSelectedElement>());
                attachments.Add(OpenCodeBrowserSelectionPolicy.Attachment(page.Url, page.Id, generation, item, false));
            }
            finally { page.ActionGate.Release(); }
        }
        if (_page != page || page.Generation != generation) return;
        foreach (var element in items.SelectMany(item => item.Elements)) OpenCodeBrowserSessionBackend.CheckPickedRef(page, element.Ref);
        attachments.AddRange(items.Select(item =>
        {
            var attachment = OpenCodeBrowserSelectionPolicy.Attachment(page.Url, page.Id, generation, item, _list.ElementInfo.IsChecked == true);
            return state.Viewports.TryGetValue(item.Id, out var viewport) ? viewport.Describe(attachment, item.Box) : attachment;
        }));
        if (await _attach(page, attachments)) { state.List.Clear(); state.Viewports.Clear(); Render(); }
        else _list.Status("未添加：当前会话草稿暂不可编辑，请稍后重试。");
    }
    private Task SettingsAsync() => ScriptAsync($"settings({JsonSerializer.Serialize(_pen)},{JsonSerializer.Serialize(_color)},{JsonSerializer.Serialize(_note.Text)})");
    private Task SettingsCoreAsync(OpenCodeBrowserPage page) => page.ExecuteScriptAsync($"window.__aifSelection?.settings({JsonSerializer.Serialize(_pen)},{JsonSerializer.Serialize(_color)},{JsonSerializer.Serialize(_note.Text)})");
    private async Task SyncSelectedAsync()
    {
        var page = _page; if (page is null || page.Closed) return;
        await page.ActionGate.WaitAsync(page.Lifetime);
        try { if (page == _page) await SyncSelectedCoreAsync(page); }
        finally { page.ActionGate.Release(); }
    }
    private async Task SyncSelectedCoreAsync(OpenCodeBrowserPage page)
    {
        if (!_states.TryGetValue(page, out var state)) return;
        var context = await OpenCodeBrowserSessionBackend.PickerContextAsync(page, page.Lifetime);
        var viewport = await ReadViewportAsync(page);
        var selected = new List<object>();
        foreach (var item in state.List.Items)
        {
            var box = item.Box;
            object? documentBox = null;
            if (item.Kind is OpenCodeBrowserSelectionKind.Region or OpenCodeBrowserSelectionKind.Screenshot && state.Viewports.TryGetValue(item.Id, out var captured))
            {
                if (!captured.SameSize(viewport)) { _list.Status("页面大小已改变 · 框选轮廓已隐藏"); continue; }
                box = captured.AtViewport(item.Box, viewport);
                var document = captured.DocumentBox(item.Box);
                documentBox = new { x = document.X, y = document.Y, width = document.Width, height = document.Height };
            }
            if (item.Kind == OpenCodeBrowserSelectionKind.Element)
            {
                var element = item.Elements.Single();
                try
                {
                    OpenCodeBrowserSessionBackend.CheckPickedRef(page, element.Ref);
                    var fresh = await context.Geometry.BoxAsync(context.Frames.Element(element.BackendNodeId, element.FrameId), true, page.Lifetime);
                    box = new(fresh.X, fresh.Y, fresh.Width, fresh.Height);
                }
                catch (OpenCodeBrowserException ex) when (ex.Code is "stale_ref" or "not_interactable") { continue; }
            }
            selected.Add(new { number = item.Number, selector = (string?)null, color = OpenCodeBrowserSelectionPolicy.Color(item.Kind), documentBox, box = new { x = box.X, y = box.Y, width = box.Width, height = box.Height } });
        }
        await page.ExecuteScriptAsync("window.__aifSelection?.selected(" + JsonSerializer.Serialize(selected) + ")");
    }
    private async Task ScriptAsync(string expression)
    {
        var page = _page; if (page is null || page.Closed) return;
        await page.ActionGate.WaitAsync(page.Lifetime); try { if (_page == page) await page.ExecuteScriptAsync("window.__aifSelection?." + expression); } finally { page.ActionGate.Release(); }
    }
    private static double N(JsonNode node, string name) => node[name]!.GetValue<double>();
    private static async Task<OpenCodeBrowserSelectionViewport> ReadViewportAsync(OpenCodeBrowserPage page)
        => OpenCodeBrowserSelectionViewport.Read((await page.CdpAsync("Page.getLayoutMetrics"))["cssVisualViewport"]!);
    private static async Task CheckViewportAsync(OpenCodeBrowserPage page, OpenCodeBrowserSelectionViewport captured)
    {
        if (!captured.SameView(await ReadViewportAsync(page))) throw new InvalidOperationException("页面已滚动或改变大小，请重新选择。");
    }
    private static async Task<BrowserRect> CaptureBoxAsync(OpenCodeBrowserPage page, bool fullPage)
    {
        var metrics = await page.CdpAsync("Page.getLayoutMetrics");
        var box = metrics[fullPage ? "cssContentSize" : "cssVisualViewport"]!;
        return new(0, 0, N(box, fullPage ? "width" : "clientWidth"), N(box, fullPage ? "height" : "clientHeight"));
    }
    private void Run(Func<Task> action) => _ = RunTask(action);
    private async Task RunTask(Func<Task> action) { try { if (!_disposed) await action(); } catch (OperationCanceledException) { } catch (Exception ex) { Error(ex); } }
    private void Error(Exception ex) { _list.Status("无法完成操作：" + ex.Message); _list.Visibility = Visibility.Visible; }
    public void Dispose()
    {
        _disposed = true; _activation++; _overlay.SelectionBindingChanged -= BindingChanged; _overlay.AddPageRequested -= AddPage; _overlay.ExitPageTool = null;
        _overlay.PickExternalElementAsync = null;
        foreach (var page in _states.Keys.ToArray())
        {
            page.Committed -= Committed; page.Closing -= PageClosing;
            _ = DisposePageAsync(page, _states[page]);
        }
        _states.Clear(); _page = null;
    }
    private static async Task DisposePageAsync(OpenCodeBrowserPage page, PageState state)
    {
        foreach (var subscription in state.Subscriptions) await subscription.DisposeAsync();
        state.Subscriptions.Clear();
        if (page.Closed) return;
        foreach (var script in state.Scripts)
        {
            var session = script.Key.Length == 0 ? null : script.Key;
            await InspectModeAsync(page, session, false);
            await page.CdpAsync("Page.removeScriptToEvaluateOnNewDocument", new() { ["identifier"] = script.Value }, session);
            await page.CdpAsync("Runtime.removeBinding", new() { ["name"] = "__aifSelectionSend" }, session);
        }
    }
}
