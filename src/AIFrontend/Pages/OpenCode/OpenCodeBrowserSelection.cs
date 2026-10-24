using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode.Browser;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;

namespace AIFrontend.Pages.OpenCode;

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
    private readonly StackPanel _annotationTools = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly TextBox _note = new() { PlaceholderText = "批注文字（先输入，再在页面放置）", Width = 220, MaxLength = 2000 };
    private readonly Dictionary<OpenCodeBrowserPage, PageState> _states = new();
    private OpenCodeBrowserPage? _page;
    private OpenCodeBrowserSelectionTool _tool;
    private string _nonce = "", _pen = "pen", _color = "#F43F5E";
    private bool _disposed, _working;
    private long _activation;
    private sealed class PageState
    {
        internal string? ScriptId;
        internal readonly OpenCodeBrowserSelectionList List = new();
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
        foreach (var entry in new[] { (OpenCodeBrowserSelectionTool.Element, "选择元素", "\uE8B0"), (OpenCodeBrowserSelectionTool.Region, "框选", "\uE7EF"), (OpenCodeBrowserSelectionTool.Lasso, "圈选", "\uED5C"), (OpenCodeBrowserSelectionTool.Annotate, "标注", "\uE70F") })
        {
            var tool = entry.Item1;
            var button = new ToggleButton { Content = new FontIcon { Glyph = entry.Item3, FontSize = 16 }, Width = 32, MinWidth = 0, Padding = new(0) };
            AutomationProperties.SetName(button, entry.Item2); ToolTipService.SetToolTip(button, entry.Item2);
            button.Click += (_, _) => Run(() => ActivateAsync(button.IsChecked == true ? tool : OpenCodeBrowserSelectionTool.None));
            _buttons.Add(tool, button); overlay.PageTools.Children.Add(button);
        }
        var screenshot = new SplitButton { Content = new FontIcon { Glyph = "\uE722", FontSize = 16 }, Padding = new(6, 0, 6, 0) };
        AutomationProperties.SetName(screenshot, "截图"); ToolTipService.SetToolTip(screenshot, "截图 · 可见区域");
        screenshot.Click += (_, _) => Run(() => CaptureStandaloneAsync(false));
        var menu = new MenuFlyout();
        void Item(string text, Func<Task> action) { var item = new MenuFlyoutItem { Text = text }; item.Click += (_, _) => Run(action); menu.Items.Add(item); }
        Item("整个页面", () => CaptureStandaloneAsync(true)); Item("选择区域", () => ActivateAsync(OpenCodeBrowserSelectionTool.Screenshot));
        Item("选中的元素", CaptureSelectedElementsAsync); screenshot.Flyout = menu; overlay.PageTools.Children.Add(screenshot);
        overlay.RightPanelSlot.Children.Add(_list);
        _list.Removed += id => { State?.List.Remove(id); Render(); Run(SyncSelectedAsync); };
        _list.NoteChanged += (id, text) => State?.List.SetNote(id, text);
        _list.AddRequested += () => Run(AddToConversationAsync);
        _list.CopyRequested += bytes => Run(() => OpenCodeBrowserSelectionFiles.CopyAsync(bytes));
        _list.SaveRequested += bytes => Run(() => OpenCodeBrowserSelectionFiles.SaveAsync(bytes, overlay.XamlRoot));
        var pillRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _modeLabel.VerticalAlignment = VerticalAlignment.Center; pillRow.Children.Add(_modeLabel);
        BuildAnnotationTools(); pillRow.Children.Add(_annotationTools);
        var done = new Button { Content = "完成" }; done.Click += (_, _) => Run(FinishAsync); pillRow.Children.Add(done);
        _pill = new Border { Child = pillRow, CornerRadius = new(24), Padding = new(12, 6, 6, 6), Margin = new(12), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed,
            Background = (Brush)Application.Current.Resources["AcrylicInAppFillColorDefaultBrush"] };
        overlay.PageToolSurface.Children.Add(_pill);
        overlay.SelectionBindingChanged += BindingChanged; overlay.AddPageRequested += AddPage;
        overlay.ExitPageTool = Exit;
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
        if (previous is not null && !previous.Closed) Run(async () => { await previous.ActionGate.WaitAsync(previous.Lifetime); try { await previous.ExecuteScriptAsync("window.__aifSelection?.activate('None','')"); } finally { previous.ActionGate.Release(); } });
        _page = _overlay.Page;
        if (_page is not null && !_states.ContainsKey(_page))
        {
            _states.Add(_page, new() { Generation = _page.Generation });
            _page.Committed += Committed; _page.Closing += PageClosing;
            Run(() => InstallAsync(_page));
        }
        Render();
    }
    private async Task InstallAsync(OpenCodeBrowserPage? page)
    {
        if (page is null || page.Closed || !_states.TryGetValue(page, out var state)) return;
        await page.EnsureReadyAsync();
        if (_disposed || page.Closed || !_states.ContainsKey(page)) return;
        page.Core.WebMessageReceived -= MessageReceived; page.Core.WebMessageReceived += MessageReceived;
        if (state.ScriptId is null) state.ScriptId = await page.Core.AddScriptToExecuteOnDocumentCreatedAsync(OpenCodeBrowserSelectionScript.Source);
    }
    private void Committed(object? sender, EventArgs args)
    {
        if (sender is not OpenCodeBrowserPage page || !_states.TryGetValue(page, out var state)) return;
        state.Generation = page.Generation; state.List.Clear();
        if (page == _page) { _activation++; _tool = OpenCodeBrowserSelectionTool.None; Render(); }
    }
    private void PageClosing(object? sender, EventArgs args)
    {
        if (sender is not OpenCodeBrowserPage page) return;
        page.Committed -= Committed; page.Closing -= PageClosing;
        if (page.View.CoreWebView2 is not null) page.Core.WebMessageReceived -= MessageReceived;
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
        await InstallAsync(page); await page.ActionGate.WaitAsync(page.Lifetime);
        try
        {
            if (_page != page || activation != _activation) return;
            await page.ExecuteScriptAsync(OpenCodeBrowserSelectionScript.Source);
            await page.ExecuteScriptAsync($"window.__aifSelection.activate({JsonSerializer.Serialize(tool.ToString())},{JsonSerializer.Serialize(_nonce)})");
            await SyncSelectedCoreAsync(page); await SettingsCoreAsync(page);
        }
        finally { page.ActionGate.Release(); }
    }
    private void Render()
    {
        foreach (var pair in _buttons) { pair.Value.IsChecked = _tool == pair.Key; pair.Value.Visibility = OpenCodeBrowserSelectionPolicy.Offers(pair.Key, _page?.PreviewPath is not null) ? Visibility.Visible : Visibility.Collapsed; }
        _pill.Visibility = _tool == OpenCodeBrowserSelectionTool.None ? Visibility.Collapsed : Visibility.Visible;
        _annotationTools.Visibility = _tool == OpenCodeBrowserSelectionTool.Annotate ? Visibility.Visible : Visibility.Collapsed;
        _modeLabel.Text = _tool switch { OpenCodeBrowserSelectionTool.Element => "选择元素 · Shift + 单击多选 · Esc 退出", OpenCodeBrowserSelectionTool.Region => "框选 · Shift 正方形 · Esc 退出", OpenCodeBrowserSelectionTool.Lasso => "圈选 · 拖动闭合 · Esc 退出", OpenCodeBrowserSelectionTool.Annotate => "标注 · Esc 退出", _ => "截图 · 拖动选择区域 · Esc 退出" };
        if (State is { } state) _list.Render(state.List); else _list.Visibility = Visibility.Collapsed;
    }
    private async void MessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        var page = _page;
        if (_disposed || page is null || page.Closed || page.View.CoreWebView2 != sender || _tool == OpenCodeBrowserSelectionTool.None) return;
        try
        {
            var message = JsonNode.Parse(args.WebMessageAsJson) as JsonObject;
            if (message?["aifSelection"]?.GetValue<string>() != _nonce) return;
            var type = message["type"]?.GetValue<string>();
            if (type == "escape") { Exit(); return; }
            if (type == "scrolled") return; // Captured items survive scrolling; their images and metadata are snapshots.
            if (_working) return;
            _working = true; var activation = _activation; var generation = page.Generation;
            await page.ActionGate.WaitAsync(page.Lifetime);
            try
            {
                if (_page != page || activation != _activation || page.Generation != generation) return;
                if (type == "element" && _tool == OpenCodeBrowserSelectionTool.Element) await SelectElementAsync(page, message!, generation);
                if (type == "region" && _tool is OpenCodeBrowserSelectionTool.Region or OpenCodeBrowserSelectionTool.Lasso or OpenCodeBrowserSelectionTool.Screenshot) await SelectRegionAsync(page, message!, generation);
            }
            finally { page.ActionGate.Release(); _working = false; }
            if (_page == page) { Render(); await SyncSelectedAsync(); }
        }
        catch (Exception ex) { _working = false; Error(ex); }
    }
    private async Task SelectElementAsync(OpenCodeBrowserPage page, JsonObject message, long generation)
    {
        var node = await page.CdpAsync("DOM.getNodeForLocation", new() { ["x"] = (int)message["x"]!.GetValue<double>(), ["y"] = (int)message["y"]!.GetValue<double>(), ["includeUserAgentShadowDOM"] = true, ["ignorePointerEventsNone"] = false });
        var backendId = node["backendNodeId"]!.GetValue<int>(); var frame = node["frameId"]?.GetValue<string>();
        var list = _states[page].List;
        var old = list.Items.FirstOrDefault(i => i.Kind == OpenCodeBrowserSelectionKind.Element && i.Elements.Any(e => e.BackendNodeId == backendId));
        if (old is not null) { list.Remove(old.Id); return; }
        var element = await ReadElementAsync(page, backendId, generation, frame);
        var image = await CaptureAsync(page, element.Box);
        if (page.Generation != generation) throw new InvalidOperationException("页面已跳转，请重新选择元素。");
        if (message["multi"]?.GetValue<bool>() != true)
            foreach (var item in list.Items.Where(i => i.Kind == OpenCodeBrowserSelectionKind.Element).ToArray()) list.Remove(item.Id);
        list.Add(OpenCodeBrowserSelectionKind.Element, element.Selector, element.Box, image, new[] { element });
    }
    private async Task SelectRegionAsync(OpenCodeBrowserPage page, JsonObject message, long generation)
    {
        var kind = _tool == OpenCodeBrowserSelectionTool.Lasso ? OpenCodeBrowserSelectionKind.Lasso : _tool == OpenCodeBrowserSelectionTool.Screenshot ? OpenCodeBrowserSelectionKind.Screenshot : OpenCodeBrowserSelectionKind.Region;
        var b = message["box"]!; var box = new BrowserRect(N(b, "x"), N(b, "y"), N(b, "width"), N(b, "height"));
        var selectors = message["selectors"]!.AsArray();
        if (selectors.Count > 200) throw new InvalidOperationException("此区域包含超过 200 个元素，请缩小选择范围。");
        var elements = new List<BrowserSelectedElement>();
        var root = await page.CdpAsync("DOM.getDocument", new() { ["depth"] = 0 });
        foreach (var selector in selectors)
        {
            var found = await page.CdpAsync("DOM.querySelector", new() { ["nodeId"] = root["root"]!["nodeId"]!.GetValue<int>(), ["selector"] = selector!.GetValue<string>() });
            var id = found["nodeId"]!.GetValue<int>(); if (id == 0) continue;
            var described = await page.CdpAsync("DOM.describeNode", new() { ["nodeId"] = id });
            var element = await ReadElementAsync(page, described["node"]!["backendNodeId"]!.GetValue<int>(), generation, null);
            if (message["polygon"] is JsonArray polygon)
            {
                var points = polygon.Select(p => new BrowserPoint(N(p!, "x"), N(p!, "y"))).ToArray();
                if (!OpenCodeBrowserSelectionPolicy.ContainsElement(points, element.Box)) continue;
            }
            else if (!OpenCodeBrowserSelectionPolicy.ContainsElement(box, element.Box)) continue;
            elements.Add(element);
        }
        var image = await CaptureAsync(page, box);
        if (page.Generation != generation) throw new InvalidOperationException("页面已跳转，请重新选择区域。");
        var state = _states[page]; var key = message["key"]?.GetValue<string>();
        var description = $"{box.Width:0} × {box.Height:0} · 包含 {elements.Count} 个元素";
        if (key is not null && key == state.DraftKey && state.List.Items.Any(i => i.Id == state.DraftItem))
            state.List.Update(state.DraftItem, box, image, elements, description);
        else { var item = state.List.Add(kind, description, box, image, elements); state.DraftKey = key; state.DraftItem = item.Id; }
    }
    private async Task<BrowserSelectedElement> ReadElementAsync(OpenCodeBrowserPage page, int backendId, long generation, string? frame)
    {
        var model = await page.CdpAsync("DOM.getBoxModel", new() { ["backendNodeId"] = backendId });
        var quad = model["model"]!["border"]!.AsArray().Select(n => n!.GetValue<double>()).ToArray();
        var xs = new[] { quad[0], quad[2], quad[4], quad[6] }; var ys = new[] { quad[1], quad[3], quad[5], quad[7] };
        var box = new BrowserRect(xs.Min(), ys.Min(), xs.Max() - xs.Min(), ys.Max() - ys.Min());
        var resolved = await page.CdpAsync("DOM.resolveNode", new() { ["backendNodeId"] = backendId }); var objectId = resolved["object"]!["objectId"]!.GetValue<string>();
        try
        {
            var result = await page.CdpAsync("Runtime.callFunctionOn", new() { ["objectId"] = objectId, ["returnByValue"] = true, ["functionDeclaration"] = "function(){let s=getComputedStyle(this);let parts=[],e=this;while(e&&e!==e.ownerDocument.documentElement){let n=e.localName;if(e.id){parts.unshift('#'+CSS.escape(e.id));break}let p=e.parentElement;if(p){let peers=[...p.children].filter(x=>x.localName===n);if(peers.length>1)n+=':nth-of-type('+(peers.indexOf(e)+1)+')'}parts.unshift(n);e=p;}return {selector:parts.join(' > '),text:(this.innerText||this.textContent||'').slice(0,4000),value:this.type==='password'?'[密码已隐藏]':typeof this.value==='string'?this.value.slice(0,4000):null,styles:['font','color','background-color','padding','margin','border-radius','display','position'].map(k=>k+': '+s.getPropertyValue(k)).join('; ')}}" });
            var value = result["result"]?["value"] ?? throw new InvalidOperationException("无法读取此元素的信息。");
            var reference = _register(page, backendId, generation, frame);
            return new(value["selector"]?.GetValue<string>() ?? "element", value["text"]?.GetValue<string>() ?? "", value["value"]?.GetValue<string>(), box, value["styles"]?.GetValue<string>() ?? "", reference, frame, backendId);
        }
        finally { await page.CdpAsync("Runtime.releaseObject", new() { ["objectId"] = objectId }); }
    }
    private async Task<byte[]> CaptureAsync(OpenCodeBrowserPage page, BrowserRect? region = null, bool fullPage = false, bool annotations = false)
    {
        await page.ExecuteScriptAsync($"window.__aifSelection?.capture({(annotations ? "true" : "false")})");
        try
        {
            var metrics = await page.CdpAsync("Page.getLayoutMetrics"); var viewport = metrics["cssVisualViewport"]!; var content = metrics["cssContentSize"]!;
            var clip = region ?? (fullPage ? new(0, 0, N(content, "width"), N(content, "height")) : new BrowserRect(0, 0, N(viewport, "clientWidth"), N(viewport, "clientHeight")));
            if (clip.Width <= 0 || clip.Height <= 0 || clip.Width * clip.Height > 64000000) throw new InvalidOperationException("截图区域为空或超过 6400 万像素，请缩小区域。");
            var result = await page.CdpAsync("Page.captureScreenshot", new() { ["format"] = "png", ["captureBeyondViewport"] = true, ["clip"] = new JsonObject { ["x"] = clip.X + (fullPage ? 0 : N(viewport, "pageX")), ["y"] = clip.Y + (fullPage ? 0 : N(viewport, "pageY")), ["width"] = clip.Width, ["height"] = clip.Height, ["scale"] = 1 } });
            return Convert.FromBase64String(result["data"]!.GetValue<string>());
        }
        finally { if (!page.Closed) await page.ExecuteScriptAsync("window.__aifSelection?.restore()"); }
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
            foreach (var element in elements) fresh.Add(await ReadElementAsync(page, element.BackendNodeId, generation, element.FrameId));
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
        attachments.AddRange(items.Select(item => OpenCodeBrowserSelectionPolicy.Attachment(page.Url, page.Id, generation, item, _list.ElementInfo.IsChecked == true)));
        if (await _attach(page, attachments)) { state.List.Clear(); Render(); }
        else _list.Status("未添加：当前会话草稿暂不可编辑，请稍后重试。");
    }
    private Task SettingsAsync() => ScriptAsync($"settings({JsonSerializer.Serialize(_pen)},{JsonSerializer.Serialize(_color)},{JsonSerializer.Serialize(_note.Text)})");
    private Task SettingsCoreAsync(OpenCodeBrowserPage page) => page.ExecuteScriptAsync($"window.__aifSelection?.settings({JsonSerializer.Serialize(_pen)},{JsonSerializer.Serialize(_color)},{JsonSerializer.Serialize(_note.Text)})");
    private Task SyncSelectedAsync() => ScriptAsync("selected(" + SelectedJson() + ")");
    private Task SyncSelectedCoreAsync(OpenCodeBrowserPage page) => page.ExecuteScriptAsync("window.__aifSelection?.selected(" + SelectedJson() + ")");
    private string SelectedJson() => JsonSerializer.Serialize(State?.List.Items.Select(i => new { number = i.Number, selector = i.Kind == OpenCodeBrowserSelectionKind.Element ? i.Selector : null, color = OpenCodeBrowserSelectionPolicy.Color(i.Kind), box = new { x = i.Box.X, y = i.Box.Y, width = i.Box.Width, height = i.Box.Height } }));
    private async Task ScriptAsync(string expression)
    {
        var page = _page; if (page is null || page.Closed) return;
        await page.ActionGate.WaitAsync(page.Lifetime); try { if (_page == page) await page.ExecuteScriptAsync("window.__aifSelection?." + expression); } finally { page.ActionGate.Release(); }
    }
    private static double N(JsonNode node, string name) => node[name]!.GetValue<double>();
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
        foreach (var page in _states.Keys.ToArray())
        {
            page.Committed -= Committed; page.Closing -= PageClosing;
            if (!page.Closed && page.View.CoreWebView2 is not null) { page.Core.WebMessageReceived -= MessageReceived; if (_states[page].ScriptId is { } id) page.Core.RemoveScriptToExecuteOnDocumentCreated(id); }
        }
        _states.Clear(); _page = null;
    }
}
