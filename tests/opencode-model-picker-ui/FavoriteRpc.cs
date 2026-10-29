using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

internal static class FavoriteRpcChecks
{
    internal static async Task Run(Action<bool, string> check, string root, string evidence)
    {
        HttpResponseMessage Reply(string json, int status = 200) => new((HttpStatusCode)status) { Content = new StringContent(json) };
        string Fixture(string name) => File.ReadAllText(Path.Combine(root, "tests/desktop-bridge/fixtures", name + ".json"));
        var hello = Fixture("hello-v1");
        var get = Fixture("favorites-get");
        foreach (var kind in new[] { "WSL", "Remote" })
        {
            var hellos = 0; var gets = 0; var sets = 0;
            var client = new OpenCodeClient();
            client.Send = (method, path, query, body, ct) =>
            {
                check(method == HttpMethod.Post && query!.Single().Key == "location[directory]" && query!.Single().Value == "/verified project", "same captured location for " + kind);
                using var input = JsonDocument.Parse(body);
                check(input.RootElement.EnumerateObject().Count() == 1 && input.RootElement.TryGetProperty("input", out _), "exact input envelope");
                if (path.EndsWith("/hello"))
                {
                    hellos++;
                    check(path == "api/rpc/opencode.qol.desktop-bridge/hello" && body == "{\"input\":{\"protocolVersions\":[1]}}", "canonical QOL identity + hello first");
                    return Task.FromResult(Reply(hello));
                }
                if (path.EndsWith("/model.favorites.get"))
                {
                    gets++;
                    check(path == "api/rpc/opencode.qol.desktop-bridge/model.favorites.get" && hellos == 1
                        && !input.RootElement.GetProperty("input").EnumerateObject().Any(), "canonical get is capability gated and uses empty object");
                    return Task.FromResult(Reply(get));
                }
                check(path == "api/rpc/opencode.qol.desktop-bridge/model.favorites.set", "only canonical QOL plugin set endpoint");
                sets++;
                var entries = input.RootElement.GetProperty("input").GetProperty("favorites");
                check(entries.GetArrayLength() == 2 && entries[0].GetProperty("providerID").GetString() == "new-provider"
                    && entries[0].GetProperty("modelID").GetString() == "new-model", "set preserves wire identity and merges fresh favorites");
                return Task.FromResult(Reply(Fixture("favorites-set")));
            };
            var store = new OpenCodeModelFavoriteStore(client, "/verified project");
            await store.LoadAsync();
            await new OpenCodeModelFavoriteStore(client, "/verified project").LoadAsync();
            await store.SaveAsync(new("new-provider", "new-model", "New", null, true, Array.Empty<string>(), null), true);
            check(store.Available && hellos == 1 && gets == 3 && sets == 1, "shared handshake and fresh toggle merge for " + kind);
            check(store.Favorites.Keys.Single() == ("provider", "vendor/model:latest"), "set reply authoritative");
        }

        foreach (var variant in new[] { "present", "missing", "unknown-id", "old", "origin-only", "favorites-only", "empty" })
        {
            var calls = 0;
            var source = new OpenCodePageSource("source-" + variant, 1, () => new());
            var first = source.CreateClient(); var second = source.CreateClient();
            check(ReferenceEquals(first.DesktopBridge, second.DesktopBridge), "one service per source, not per feature/client");
            first.Send = (_, path, query, body, _) =>
            {
                calls++;
                check(path.EndsWith("/hello") && query is null, "source connect hello uses default server location");
                return Task.FromResult(variant switch
                {
                    "missing" => Reply("private", 404),
                    "unknown-id" => Reply("{\"type\":\"rpc.method_not_found\",\"message\":\"private\"}", 400),
                    "old" => Reply(Fixture("hello-unsupported")),
                    "origin-only" => Reply("{\"output\":{\"protocolVersion\":1,\"capabilities\":[\"billion.origin.v1\"]}}"),
                    "favorites-only" => Reply("{\"output\":{\"protocolVersion\":1,\"capabilities\":[\"model.favorites.v1\"]}}"),
                    "empty" => Reply("{\"output\":{\"protocolVersion\":1,\"capabilities\":[]}}"),
                    _ => Reply(hello),
                });
            };
            var state = await first.DesktopBridge.NegotiateAsync(first, null, default);
            var cached = await second.DesktopBridge.NegotiateAsync(second, "/other", default);
            check(calls == 1 && ReferenceEquals(state, cached), "one hello per source across clients and feature locations");
            check(state.Supports("model.favorites.v1") == (variant is "present" or "favorites-only")
                && state.Supports("billion.origin.v1") == (variant is "present" or "origin-only"), "capability subset gates each feature independently");
            var notice = first.DesktopBridge.TakeNotice(state);
            check(notice == (variant == "present" ? null : variant is "missing" or "unknown-id" ? "未安装 Desktop Bridge 插件，部分功能暂不可用" : "Desktop Bridge 插件版本过旧，部分功能暂不可用"), "exact source-scoped missing/old notice");
            check(second.DesktopBridge.TakeNotice(state) is null, "notice shown at most once per source, including after dismissal");
            if (!state.Supports("model.favorites.v1"))
            {
                var store = new OpenCodeModelFavoriteStore(second, "/project");
                await store.LoadAsync();
                check(!store.Available && !store.Favorites.Keys.Any() && calls == 1, "unavailable means no favorites requests or fallback");
                try { await store.SaveAsync(new("p", "m", "M", null, true, Array.Empty<string>(), null), true); check(false, "unavailable save must fail"); }
                catch (InvalidOperationException) { check(calls == 1, "unavailable saves never access another persistence route"); }
            }
        }
        foreach (var status in new[] { 401, 403, 500 })
        {
            var client = new OpenCodeClient { Send = (_, _, _, _, _) => Task.FromResult(Reply("{\"type\":\"rpc.internal\",\"message\":\"private\"}", status)) };
            try { await client.DesktopBridge.NegotiateAsync(client, null, default); check(false, "actual handshake error expected"); }
            catch (OpenCodePluginRpcException ex) { check(ex.StatusCode == status && !ex.IsUnsupported && !ex.Message.Contains("private"), "auth/server errors are not missing plugin"); }
        }
        foreach (var malformed in new[] { "[]", "{}", "{\"output\":null}", "{\"output\":{\"protocolVersion\":\"1\",\"capabilities\":[]}}", "not-json" })
        {
            var client = new OpenCodeClient { Send = (_, _, _, _, _) => Task.FromResult(Reply(malformed)) };
            try { await client.DesktopBridge.NegotiateAsync(client, null, default); check(false, "invalid hello expected"); }
            catch (OpenCodeProtocolException) { check(true, "malformed hello maps to protocol error"); }
        }
        foreach (var status in new[] { 401, 404, 500 })
        {
            var calls = 0;
            var client = new OpenCodeClient { Send = (_, path, _, _, _) =>
            {
                calls++;
                return Task.FromResult(Reply(path.EndsWith("/hello") ? hello : path.EndsWith("/model.favorites.get") ? get : "private",
                    path.EndsWith("/model.favorites.set") ? status : 200));
            } };
            var store = new OpenCodeModelFavoriteStore(client, "/project"); await store.LoadAsync();
            try { await store.SaveAsync(new("p", "m", "M", null, true, Array.Empty<string>(), null), true); check(false, "set failure expected"); }
            catch (OpenCodePluginRpcException ex) { check(ex.StatusCode == status && calls == 4 && !ex.Message.Contains("private"), "failed set is not retried or saved elsewhere"); }
        }
        foreach (var error in new Exception[] { new HttpRequestException("private"), new IOException("private"), new OperationCanceledException("private") })
        {
            var client = new OpenCodeClient { Send = (_, _, _, _, _) => throw error };
            try { await client.PluginRpcAsync("hello", null, new { protocolVersions = new[] { 1 } }, default); check(false, "transport failure expected"); }
            catch (OpenCodeException ex) { check((error is OperationCanceledException ? ex is OpenCodeTimeoutException : ex is OpenCodeTransportException) && !ex.Message.Contains("private"), "sanitized timeout/transport mapping"); }
        }
        foreach (var invalid in new[] { "{\"favorites\":[{}]}", "{\"favorites\":[{\"providerID\":\"p\",\"modelID\":\"bad id\"}]}", "{\"favorites\":[],\"extra\":true}" })
        {
            using var json = JsonDocument.Parse(invalid);
            try { OpenCodeModelFavoritesRpc.Read(json.RootElement); check(false, "invalid favorites rejected"); }
            catch (OpenCodeProtocolException) { check(true, "strict favorites schema"); }
        }
        var storeSource = File.ReadAllText(Path.Combine(root, "src/AIFrontend/Pages/OpenCode/OpenCodeModelFavoriteStore.cs"));
        check(new[] { "System.IO", "File.", "Directory.", "SettingsService", "wsl.exe", @"\\wsl.localhost", "settings.OpenCodeModelFavorites", "OpenCodeModelFavoriteJson" }.All(text => !storeSource.Contains(text)), "favorites source contract: no file/settings/UNC fallback");
        var settingsSource = File.ReadAllText(Path.Combine(root, "src/AIFrontend/Models/AppSettings.cs"));
        check(!settingsSource.Contains("OpenCodeModelFavorite"), "removed app-settings favorites setting and DTO");
        var dialog = File.ReadAllText(Path.Combine(root, "src/AIFrontend/Pages/OpenCode/OpenCodeComposerNativeDialogs.cs"));
        check(dialog.Contains("favoriteSection.Visibility = favoriteStore.Available ? Visibility.Visible : Visibility.Collapsed")
            && dialog.Contains("Visibility = showFavorite ? Visibility.Visible : Visibility.Collapsed"), "unavailable favorites section and stars hidden");
        check(File.ReadAllText(Path.Combine(root, "src/AIFrontend/Pages/OpenCodePage.xaml.cs")).Contains("_ = InitializeDesktopBridgeAsync(source, client)")
            && File.ReadAllText(Path.Combine(root, "src/AIFrontend/Pages/OpenCodePage.DesktopBridge.cs")).Contains("MessageBar.Message = notice"), "source connect negotiates and uses existing notice surface");
    }
}
