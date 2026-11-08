using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using FluentCode.Models;
using FluentCode.Services.NativeUi;

internal static class Program
{
    private static int _checks;
    private sealed class TileHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply(request));
    }
    private static void Require(bool condition, string message) { _checks++; if (!condition) throw new InvalidOperationException(message); }
    private static void Same(JsonNode? actual, JsonNode? expected, string label)
    {
        if (!JsonNode.DeepEquals(actual, expected)) throw new InvalidOperationException(label + "\nEXPECTED " + expected?.ToJsonString() + "\nACTUAL " + actual?.ToJsonString());
        _checks++;
    }
    private static IEnumerable<NativeUiElement> Elements(object? value)
    {
        if (value is NativeUiElement element) { yield return element; foreach (var child in element.Props.Values.SelectMany(Elements)) yield return child; }
        else if (value is IReadOnlyList<object?> array) foreach (var child in array.SelectMany(Elements)) yield return child;
    }
    private static async Task Press(NativeUiRuntime runtime, string label)
    {
        var button = Elements(runtime.EvaluateRoot()).FirstOrDefault(e => e.TypeName == "Button" && NativeUiValues.Text(e.Props.GetValueOrDefault("label")) == label);
        Require(button is not null, "Button missing: " + label);
        Require(button!.Props.GetValueOrDefault("action") is NativeUiActionPlan, "Button has no action: " + label);
        await runtime.ExecuteAsync((NativeUiActionPlan)button.Props["action"]!);
    }
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var repo = args[0]; var fixtures = Path.Combine(repo, "tests/native-ui/fixtures");
            var catalog = NativeUiCatalog.Default; var parser = new NativeUiParser(catalog);
            var sourceFiles = Directory.GetFiles(fixtures, "*.oui").Order().ToArray();
            foreach (var file in sourceFiles)
            {
                var source = File.ReadAllText(file); var expected = JsonNode.Parse(File.ReadAllText(Path.ChangeExtension(file, ".expected.json")))!;
                Require(expected["parserVersion"]!.GetValue<string>() == "1.1.0", "Oracle version");
                Require(expected["sha256"]!.GetValue<string>() == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant(), "Stale oracle: " + file);
                var result = parser.Parse(source);
                Same(result.ToJson(), expected["parse"], Path.GetFileName(file) + " parse");
                using var runtime = new NativeUiRuntime(result, catalog);
                if (!result.Errors.Any(error => error.Code == "query-cycle")) Same(runtime.EvaluateRoot()?.ToJson(), expected["evaluated"], Path.GetFileName(file) + " runtime");
                var stream = new NativeUiStreamParser(parser);
                foreach (var snapshot in expected["streaming"]!.AsArray())
                {
                    var length = snapshot!["length"]!.GetValue<int>();
                    Same(stream.Set(source[..length], length == source.Length).ToJson(), snapshot["result"], Path.GetFileName(file) + " streaming " + length);
                }
            }
            Console.WriteLine($"PASS {sourceFiles.Length} official parse/runtime/streaming fixtures");

            NativeUiRuntime Demo(string name) => new(parser.Parse(File.ReadAllText(Path.Combine(fixtures, "demo-" + name + ".oui"))), catalog);
            using var calculator = Demo("calculator");
            foreach (var key in new[] { "1", "2", "+", "5", "=" }) await Press(calculator, key);
            Require(NativeUiValues.Number(calculator.Get("$entry")) == 17, "12 + 5 = 17");
            await Press(calculator, "M+"); await Press(calculator, "C");
            Require(NativeUiValues.Number(calculator.Get("$memory")) == 17, "C retains memory");
            foreach (var key in new[] { "8", "+", "MR", "=" }) await Press(calculator, key);
            Require(NativeUiValues.Number(calculator.Get("$entry")) == 25, "8 + MR = 25");
            await Press(calculator, "M+"); Require(NativeUiValues.Number(calculator.Get("$memory")) == 42, "M+ accumulates");
            await Press(calculator, "MC"); Require(NativeUiValues.Number(calculator.Get("$memory")) == 0, "MC clears");
            foreach (var (op, value) in new[] { ("−", 5d), ("×", 36d), ("÷", 2.25d) })
            {
                await Press(calculator, "C"); foreach (var key in new[] { "9", op, "4", "=" }) await Press(calculator, key);
                Require(NativeUiValues.Number(calculator.Get("$entry")) == value, "Arithmetic " + op);
            }
            using var budget = Demo("budget"); var changed = 0; budget.Changed += () => changed++;
            var slider = Elements(budget.EvaluateRoot()).First(e => e.TypeName == "Slider");
            budget.SetField("团队人数", slider.Props["value"], 10d);
            Require(changed == 1 && Elements(budget.EvaluateRoot()).Any(e => e.TypeName == "Title" && NativeUiValues.Text(e.Props.GetValueOrDefault("text")) == "¥290"), "Slider re-evaluates total");
            budget.Set("$annual", true);
            Require(Elements(budget.EvaluateRoot()).Any(e => e.TypeName == "Title" && NativeUiValues.Text(e.Props.GetValueOrDefault("text")) == "¥232"), "Checkbox discount");
            await Press(budget, "恢复默认"); Require(NativeUiValues.Number(budget.Get("$quantity")) == 5 && Equals(budget.Get("$annual"), false), "Reset defaults");
            using var assistant = Demo("assistant"); NativeUiAssistantRequest? request = null;
            assistant.ToAssistant = input => { request = input; return Task.FromResult(new NativeUiHostResult(true, "queued")); };
            var select = Elements(assistant.EvaluateRoot()).First(e => e.TypeName == "Select"); assistant.SetField("你的偏好", select.Props["value"], "careful");
            await Press(assistant, "将选择发给 AI");
            Require(request?.Message == "请按「优先质量」方案继续。", "Each/Select choice reaches host");
            Require(request!.Context is IReadOnlyDictionary<string, object?> context && Equals(context["choice"], "careful"), "Assistant context preserved");
            Require(Equals(request.FormState["$choice"], "careful"), "Form state snapshot");
            using var bindings = new NativeUiRuntime(parser.Parse("$x = 1\nroot = Input($x = $value * 2, \"Double\", \"number\")"), catalog);
            bindings.SetField("Double", bindings.EvaluateRoot()!.Props["value"], 3d); Require(NativeUiValues.Number(bindings.Get("$x")) == 6, "$value assignment");
            using var query = new NativeUiRuntime(parser.Parse("q = Query(\"read\", {}, {n:7})\nroot = Card([Text(\"n=\" + q.n), Button(\"Run\", @Run(q))])"), catalog);
            Require(query.Unsupported.Count == 1 && Elements(query.EvaluateRoot()).Any(e => Equals(e.Props.GetValueOrDefault("text"), "n=7")), "Query defaults without tool/network access");
            await Press(query, "Run"); Require(query.Status.Contains("未支持"), "@Run marked unsupported");
            Require(parser.Parse("root = Text(" + new string('!', 100) + "true)").Errors.Any(error => error.Code == "limit"), "Parser recursion bounded");
            Require(!NativeUiSafety.HttpUrl("file:///x") && !NativeUiSafety.HttpUrl("javascript:alert(1)") && NativeUiSafety.HttpUrl("https://example.org"), "URL allowlist");
            var rejected = false; try { budget.Set("$unit", new string('x', catalog.Limits.StringLength + 1)); } catch (NativeUiLimitException) { rejected = true; }
            Require(rejected, "Runtime string limit");
            var unsafeMarkdown = false; try { NativeUiSafety.Check("![image](file:///etc/passwd)", catalog.Limits); } catch (NativeUiLimitException) { unsafeMarkdown = true; }
            Require(unsafeMarkdown, "Embedded URLs use the same allowlist");
            using var urlInput = new NativeUiRuntime(parser.Parse("$url = \"https://example.org\"\nroot = Input($url, \"URL\")"), catalog);
            Require(!urlInput.TrySetField("URL", urlInput.EvaluateRoot()!.Props["value"], "file:///etc/passwd")
                && Equals(urlInput.Get("$url"), "https://example.org") && urlInput.Status.Contains("http(s)"), "Rejected native input preserves state and reports locally");
            Require(catalog.Components.Count == 57 && !catalog.Components.ContainsKey("HtmlView"), "Complete native catalog with native multimedia and without HTML");
            using var form = Demo("form"); form.Set("$period", "每月"); form.Set("$date", "2026-11-12");
            using var onboarding = Demo("showcase-onboarding"); await Press(onboarding, "下一步");
            Require(NativeUiValues.Number(onboarding.Get("$step")) == 2 && Elements(onboarding.EvaluateRoot()).Any(element => element.TypeName == "RadioGroup"), "Showcase multi-step form reveals described preference controls");
            onboarding.Set("$notes", "Keep this note"); await Press(onboarding, "下一步"); await Press(onboarding, "上一步");
            Require(NativeUiValues.Number(onboarding.Get("$step")) == 2 && Equals(onboarding.Get("$notes"), "Keep this note"), "Showcase form preserves state across steps");
            using var explorer = Demo("showcase-explorer"); explorer.Set("$choice", "高山"); await Press(explorer, "选择目的地"); explorer.Set("$tab", "我的计划"); explorer.Set("$faq", true);
            Require(Equals(explorer.Get("$choice"), "京都") && Elements(explorer.EvaluateRoot()).Any(element => element.TypeName == "List") && Elements(explorer.EvaluateRoot()).Any(element => element.TypeName == "Link"), "Showcase carousel selection, tabs and accordion share reactive state");
            using var planner = Demo("showcase-planner"); planner.Set("$days", 6d); planner.Set("$stay", "simple");
            Require(Elements(planner.EvaluateRoot()).Any(element => element.TypeName == "Title" && Equals(element.Props.GetValueOrDefault("text"), "¥3480")), "Showcase planner recomputes its complete budget");
            await Press(planner, "恢复默认"); Require(NativeUiValues.Number(planner.Get("$days")) == 4 && Equals(planner.Get("$stay"), "comfort"), "Showcase planner resets all inputs");
            using var journey = Demo("showcase-journey"); journey.Set("$done", true);
            using var keypad = Demo("calculator");
            Require(Elements(keypad.EvaluateRoot()).Where(element => element.TypeName == "Row" && element.Props.GetValueOrDefault("children") is IReadOnlyList<object?> children && children.Count > 0 && children.All(child => child is NativeUiElement { TypeName: "Button" })).All(element => ((IReadOnlyList<object?>)element.Props["children"]!).Count == 4), "Calculator keypad rows all align to four equal columns, including the sign/zero/equals/plus row");
            keypad.Set("$entry", 12d); await Press(keypad, "±"); Require(NativeUiValues.Number(keypad.Get("$entry")) == -12, "Calculator sign key preserves arithmetic state");
            Require(Elements(journey.EvaluateRoot()).Any(element => element.TypeName == "Title" && Equals(element.Props.GetValueOrDefault("text"), "3 / 4"))
                && Elements(journey.EvaluateRoot()).Any(element => element.TypeName == "Badge" && Equals(element.Props.GetValueOrDefault("text"), "进行中")), "Confirming a release checklist does not mark an in-progress milestone completed");
            using var sizing = Demo("sizing");
            Require(Elements(sizing.EvaluateRoot()).Where(element => element.TypeName == "IconButton").Select(element => element.Props.GetValueOrDefault("size")).SequenceEqual(new object?[] { "sm", "md", "lg" }), "IconButton size contract reaches native models");
            form.TrySetField("$ui:flash", null, true); NativeUiStateStore.Save("part-1", form.Program.Source, form.Snapshot());
            using var reopened = Demo("form"); reopened.Restore(NativeUiStateStore.Read("part-1", form.Program.Source)!);
            Require(Equals(reopened.Get("$period"), "每月") && Equals(reopened.Get("$date"), "2026-11-12") && Equals(reopened.Field("$ui:flash", false), true), "Part ID snapshot survives runtime disposal/recreation including local UI state");
            Require(NativeUiStateStore.Read("part-other", form.Program.Source) is null && NativeUiStateStore.Read("part-1", "different source") is null, "Part/source state isolation");
            using var external = new NativeUiRuntime(parser.Parse("$n = 0\nroot = Button(\"Open\", Action([@OpenUrl(\"https://example.org/full?query=1#anchor\"), @Set($n, 1)]))"), catalog);
            string? asked = null; external.OpenUrl = url => { asked = url; return Task.FromResult(new NativeUiHostResult(false, "cancelled")); };
            await Press(external, "Open"); Require(asked == "https://example.org/full?query=1#anchor" && NativeUiValues.Number(external.Get("$n")) == 0, "Cancelled URL preserves full URL and stops action sequence");
            external.OpenUrl = _ => Task.FromResult(new NativeUiHostResult(true, "opened")); await Press(external, "Open"); Require(NativeUiValues.Number(external.Get("$n")) == 1, "Accepted URL continues ordered actions");
            Require(NativeUiImages.Https("https://example.org/image.png") && !NativeUiImages.Https("http://example.org/image.png") && !NativeUiImages.Https("file:///image.png"), "Image HTTPS-only policy");
            var beijing = NativeUiCoordinates.WgsToGcj(new(39.908823, 116.397470));
            Require(Math.Abs(beijing.Latitude - 39.910226498) < 1e-8 && Math.Abs(beijing.Longitude - 116.403713582) < 1e-8, "Known Beijing WGS84→GCJ02 point");
            var shanghai = NativeUiCoordinates.WgsToGcj(new(31.2304, 121.4737));
            Require(Math.Abs(shanghai.Latitude - 31.228457738) < 1e-8 && Math.Abs(shanghai.Longitude - 121.478223059) < 1e-8, "Known Shanghai WGS84→GCJ02 point");
            foreach (var point in new[] { new NativeUiCoordinate(39.908823, 116.39747), new(31.2304, 121.4737), new(22.543096, 114.057865), new(30.572269, 104.066541) })
            {
                var back = NativeUiCoordinates.GcjToWgs(NativeUiCoordinates.BdToGcj(NativeUiCoordinates.GcjToBd(NativeUiCoordinates.WgsToGcj(point))));
                Require(Math.Abs(back.Latitude - point.Latitude) < 1e-5 && Math.Abs(back.Longitude - point.Longitude) < 1e-5, "WGS84→GCJ02→BD09→WGS84 round trip below 1e-5°");
            }
            var london = new NativeUiCoordinate(51.5074, -.1278);
            Require(NativeUiCoordinates.WgsToGcj(london) == london && NativeUiCoordinates.ForProvider(london, NativeUiMapProvider.OpenStreetMap) == london, "Coordinates outside China and OSM stay WGS84");
            Require(!NativeUiCoordinates.Valid(new(double.NaN, 0)) && !NativeUiCoordinates.Valid(new(91, 181)), "Invalid/overflow map coordinates rejected");
            var osm = new NativeUiMapSettings();
            Require(NativeUiMapProviders.TileUrl(osm, 2, 1, 3).AbsoluteUri == "https://tile.openstreetmap.org/2/1/3.png" && NativeUiMapProviders.UserAgent.StartsWith("FluentCode/"), "OSM URL and identifying User-Agent");
            Require(NativeUiMapProviders.TileUrl(osm with { Provider = NativeUiMapProvider.AMap }, 2, 1, 3).AbsoluteUri == "https://webrd01.is.autonavi.com/appmaptile?style=7&x=1&y=3&z=2", "AMap XYZ URL uses transformed GCJ02 coordinates");
            var custom = osm with { Provider = NativeUiMapProvider.Custom, CustomTileUrlTemplate = "https://tiles.example/{z}/{x}/{y}.png", CustomAttribution = "© Example" };
            Require(custom.ValidationError() is null && NativeUiMapProviders.TileUrl(custom, 2, 1, 3).AbsoluteUri == "https://tiles.example/2/1/3.png" && NativeUiMapProviders.Attribution(custom) == "© Example", "Custom HTTPS XYZ requires and preserves attribution");
            Require((custom with { CustomAttribution = "" }).ValidationError() is not null && (custom with { CustomTileUrlTemplate = "http://tiles.example/{z}/{x}/{y}" }).ValidationError() is not null, "Custom tile templates reject missing attribution and HTTP");
            var tdt = osm with { Provider = NativeUiMapProvider.TianDiTu };
            Require(NativeUiMapProviders.TileUrl(tdt, 2, 1, 3, "user+key").AbsoluteUri.Contains("TILEROW=3&TILECOL=1&tk=user%2Bkey"), "TianDiTu user key is escaped, never embedded");
            var noKey = false; try { NativeUiMapProviders.TileUrl(tdt, 2, 1, 3); } catch (InvalidOperationException) { noKey = true; }
            var baiduBlocked = false; try { NativeUiMapProviders.TileUrl(osm with { Provider = NativeUiMapProvider.Baidu }, 2, 1, 3); } catch (NotSupportedException) { baiduBlocked = true; }
            Require(noKey && baiduBlocked, "Missing user key and unsupported Baidu fail locally");
            foreach (var source in new[] { "http://example.org/a.mp4", "file:///a.mp4", "https://user:secret@example.org/a.mp4", "javascript:alert(1)" }) Require(!NativeUiMediaPolicy.Https(source), "Media disallows non-HTTPS and credential URLs");
            Require(NativeUiMediaPolicy.Https("https://example.org/full/video.mp4?token=1#track") && NativeUiMediaPolicy.YouTubeId("https://youtu.be/dQw4w9WgXcQ") == "dQw4w9WgXcQ" && NativeUiMediaPolicy.YouTubeId("https://youtube.com.evil.example/watch?v=dQw4w9WgXcQ") is null, "Full HTTPS media and allowlisted YouTube IDs");
            var gate = new NativeUiLoadGate(); Require(!gate.Request(), "Empty media/map source cannot be requested"); gate.Bind("https://example.org/a"); Require(!gate.Requested && gate.Request(), "Media/map source loads only after explicit request"); gate.Bind("https://example.org/a"); Require(gate.Requested, "Reactive refresh retains same-source consent"); gate.Bind("https://example.org/b"); Require(!gate.Requested, "Source change revokes load consent");
            var cacheDirectory = Path.Combine(Path.GetTempPath(), "native-ui-tile-test-" + Guid.NewGuid().ToString("N")); var requests = 0;
            try
            {
                using var client = new NativeUiTileClient(new(osm, null, cacheDirectory), new TileHandler(request =>
                {
                    requests++; Require(request.Headers.UserAgent.ToString().Contains("FluentCode/"), "Tile request sends identifying UA");
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
                    response.Content.Headers.ContentType = new("image/png"); response.Headers.CacheControl = new() { MaxAge = TimeSpan.FromDays(7) }; response.Headers.ETag = new("\"fixture\""); return response;
                }));
                var firstTile = await client.GetAsync(2, 1, 3); var cachedTile = await client.GetAsync(2, 1, 3);
                Require(firstTile.SequenceEqual(cachedTile) && requests == 1, "Fresh tile cache avoids a second network request and retains HTTP metadata");
                using var failedClient = new NativeUiTileClient(new(osm, null, cacheDirectory), new TileHandler(_ => new(HttpStatusCode.ServiceUnavailable))); var failed = false; failedClient.Failed += () => failed = true;
                try { await failedClient.GetAsync(2, 2, 3); } catch (InvalidOperationException) { }
                Require(failed, "First tile failure signals native static-list fallback");
            }
            finally { if (Directory.Exists(cacheDirectory)) Directory.Delete(cacheDirectory, true); }
            var payload = new NativeUiAssistantRequest("Continue with this option", new Dictionary<string, object?> { ["id"] = 1d }, new Dictionary<string, object?>());
            Require(NativeUiComposerPolicy.Create(payload, true, true, catalog.Limits).Delivery == "queue", "Busy assistant queues through composer policy");
            Require(!NativeUiComposerPolicy.Create(payload, true, false, catalog.Limits).Allowed, "Unsupported queue fails closed");
            Require(!NativeUiComposerPolicy.Create(payload with { Message = "!rm -rf /" }, false, true, catalog.Limits).Allowed
                && !NativeUiComposerPolicy.Create(payload with { Message = "/btw do this" }, false, true, catalog.Limits).Allowed, "Cards cannot enter command routes");
            var inputJson = JsonSerializer.Serialize(new { source = "root = Text(\"History\")", title = "Example" });
            var streamingState = JsonSerializer.SerializeToElement(new { status = "streaming", input = inputJson });
            Require(NativeUiToolInput.Read(streamingState, catalog.Limits).Streaming, "Complete-looking prefix remains a placeholder");
            var titledStreaming = NativeUiToolInput.Read(JsonSerializer.SerializeToElement(new { status = "streaming", input = new { title = "Meeting", source = "root = Text(\"Wait\")" } }), catalog.Limits);
            Require(titledStreaming.Streaming && titledStreaming.Title == "Meeting" && titledStreaming.Source is null, "Streaming title names the skeleton without releasing source");
            Require(NativeUiToolInput.Read(JsonSerializer.SerializeToElement(new { status = "streaming", input = new { title = new string('x', 121) } }), catalog.Limits).Title is null, "Streaming title respects the 120-character bound");
            var endedState = JsonSerializer.SerializeToElement(new { status = "streaming", input = inputJson, _inputComplete = true });
            Require(NativeUiToolInput.Read(endedState, catalog.Limits).Source == "root = Text(\"History\")", "Input ended releases JSON string source");
            var replayState = JsonSerializer.SerializeToElement(new { status = "completed", input = new { source = "root = Text(\"History\")" } });
            Require(!NativeUiToolInput.Read(replayState, catalog.Limits).Streaming && NativeUiToolInput.Read(replayState, catalog.Limits).Source == "root = Text(\"History\")", "History replay reads object input");
            var composerSource = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCodePage.NativeUi.cs"));
            Require(composerSource.Contains("RequestComposerSubmission(intent.Delivery)") && composerSource.Contains("OwnsComposerBinding(binding)")
                && composerSource.Contains("snapshot.HasDraft") && !composerSource.Contains("SendPromptGuardedAsync"), "Native UI only enters the normal guarded composer, preserving drafts");
            Require(composerSource.Contains("OnNativeUiBrowserRequested") && composerSource.Contains("IsSameMessageBinding(binding.SessionId, binding.Directory)") && composerSource.Contains("GetBrowserWorkspace(binding.SessionId)") && composerSource.Contains("workspace.OpenPage(args.Url)"), "YouTube requests enter the existing origin/session-guarded in-app browser workspace");
            var nativeMediaSource = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/NativeUi/NativeUiRenderer.Media.cs"));
            Require(nativeMediaSource.Contains("player.Pause()") && nativeMediaSource.Contains("AutoPlay = false") && nativeMediaSource.Contains("element.IsFullWindow = !element.IsFullWindow") && !nativeMediaSource.Contains("WebView2"), "Native media has explicit full-window support, pause/disposal and no embedded browser");
            Require(nativeMediaSource.Contains("new ContentDialog") && nativeMediaSource.Contains("Stretch = Stretch.None") && nativeMediaSource.Contains("terminal.SuppressAirspace(snapshot: true)"), "Full-size gallery viewer suppresses terminal airspace and retains original image size");
            var mapSettingsSource = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/Settings/SettingsView.NativeUiMap.cs"));
            Require(mapSettingsSource.Contains("IsEnabled = provider != NativeUiMapProvider.Baidu") && mapSettingsSource.Contains("_secretStore.SetAsync(created, _mapKey.Password") && mapSettingsSource.Contains("TianDiTuCredentialId = id"), "Provider settings disable Baidu and store TianDiTu only through DPAPI credential references");
            var markdownSource = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/OpenCodeRichTextPresenter.cs"));
            Require(markdownSource.Contains("_nativeUiSandbox ? null : OpenCodeImageContext.Find(_root)") && markdownSource.Contains("_nativeUiSandbox ? null : new ChipContext")
                && markdownSource.Contains("? nativeUiSandbox ? \"text\" : \"mermaid\""), "Native Markdown has no inherited files/network/browser capability");
            var confirmationSource = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/NativeUi/NativeUiRenderer.Components.cs"));
            Require(confirmationSource.Contains("NativeUiComponentTheme.Text(url)") && confirmationSource.Contains("await completion.Task") && confirmationSource.Contains("Launcher.LaunchUriAsync"), "Full URL confirmation precedes external launch");
            Require(confirmationSource.Contains("private void Slot(StackPanel panel, object? value, string path, int depth)")
                && !confirmationSource.Contains("private StackPanel Slot("), "Slot updates an existing host instead of allocating a parent during native layout refresh");
            var controlsSource = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/NativeUi/NativeUiRenderer.Controls.cs"));
            Require(controlsSource.Contains("Content = tileContent") && controlsSource.Contains("Slot(tileContent, P(node, \"children\")")
                && !controlsSource.Contains("tile.Content = Slot("), "Pressable retains its native slot host");
            var contentSource = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/NativeUi/NativeUiRenderer.Content.cs"));
            Require(contentSource.Contains("Slot(row.Body, values[i]") && contentSource.Contains("Content = flashContent")
                && contentSource.Contains("Slot(flashFace,") && !contentSource.Contains("flash.Content = face"), "List rows and Flashcard retain native slot hosts across refreshes");
            var dataSource = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/NativeUi/NativeUiRenderer.Data.cs"));
            Require(dataSource.Contains("row.Owners[index]") && dataSource.Contains("previous.Content = null")
                && dataSource.Contains("row.Owners[index] = cell"), "Component table cells clear the previous logical Content owner before virtualization/rebinding transfer");
            Require(dataSource.Contains("if (!ReferenceEquals(cell.Content, content)) cell.Content = content"), "Component table refresh does not reassign its already-owned native content");
            Require(dataSource.Contains("new PieChart { Background = ComponentBrush(\"surface\", true)")
                && dataSource.Contains("new CartesianChart { Background = ComponentBrush(\"surface\", true)"), "Native chart controls use the shared light/dark surface background");
            var themeSource = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/NativeUi/NativeUiTheme.cs"));
            Require(themeSource.Contains("background || token == \"surface\" ? Background(token)"), "Surface colour semantics remain a background brush for SVG fills");
            Require(themeSource.Contains("table.Margin = new Thickness(0)") && !themeSource.Contains("new Thickness(-16, 0, -16, 0)"), "Tables stay within their card content padding instead of bleeding into the outer frame");
            Require(themeSource.Contains("button.CornerRadius = Application.Current.Resources.TryGetValue(\"ControlCornerRadius\""), "Every Button variant explicitly uses the standard native control corner radius");
            var captureSource = File.ReadAllText(Path.Combine(repo, "src/FluentCode/App.NativeUiCapture.cs"));
            Require(captureSource.Contains("chart.UpdateFinished += Finished") && captureSource.Contains("Throttling = false")
                && captureSource.Contains("Native chart draw completed without any rendered series/legend pixels"), "Native chart capture waits for draw completion and verifies painted pixels, not only populated data");
            Console.WriteLine($"PASS native reactive runtime, calculator memory/arithmetic, slider/checkbox/select, actions and safety; {_checks} checks");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
