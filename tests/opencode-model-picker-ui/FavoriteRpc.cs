using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Models;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Configuration;
using AIFrontend.Services.OpenCode;

internal static class FavoriteRpcChecks
{
    internal static async Task Run(Action<bool, string> check, string root, string evidence)
    {
        HttpResponseMessage Reply(string json, int status = 200) => new((HttpStatusCode)status) { Content = new StringContent(json) };
        var hello = File.ReadAllText(Path.Combine(root, "plugins/opencode-windows-frontend/tests/fixtures/hello-v1.json"));
        var get = File.ReadAllText(Path.Combine(root, "plugins/opencode-windows-frontend/tests/fixtures/favorites-get.json"));
        var set = File.ReadAllText(Path.Combine(root, "plugins/opencode-windows-frontend/tests/fixtures/favorites-set.json"));
        foreach (var wsl in new[] { true, false })
        {
            SettingsService.State = new() { OpenCode = new(wsl ? OpenCodeConnectionKind.Wsl : OpenCodeConnectionKind.Remote, "synthetic"),
                OpenCodeModelFavorites = new[] { new OpenCodeModelFavorite("other", "keep", "m") } };
            SettingsService.Writes = 0;
            WslQuickAccessHome.Reads = 0;
            var hellos = 0;
            var gets = 0;
            var sets = 0;
            var client = new OpenCodeClient();
            client.Send = (method, path, query, body, ct) =>
            {
                check(method == HttpMethod.Post && query!.Single().Key == "location[directory]" && query!.Single().Value == "/verified project", "RPC uses POST and captured server location");
                using var input = JsonDocument.Parse(body);
                check(input.RootElement.EnumerateObject().Count() == 1 && input.RootElement.TryGetProperty("input", out _), "request has exactly input envelope");
                if (path.EndsWith("/hello"))
                {
                    hellos++;
                    check(input.RootElement.GetProperty("input").GetProperty("protocolVersions")[0].GetInt32() == 1, "hello v1 request");
                    return Task.FromResult(Reply(hello));
                }
                if (path.EndsWith("/model.favorites.get"))
                {
                    gets++;
                    check(!input.RootElement.GetProperty("input").EnumerateObject().Any(), "get uses empty input object");
                    return Task.FromResult(Reply(get));
                }
                check(path == "api/rpc/opencode.windows-frontend/model.favorites.set", "set exact plugin endpoint");
                sets++;
                var entries = input.RootElement.GetProperty("input").GetProperty("favorites");
                check(entries.GetArrayLength() == 2 && entries[0].GetProperty("providerID").GetString() == "new-provider"
                    && entries[0].GetProperty("modelID").GetString() == "new-model", "set envelope has wire IDs and merges latest favorites");
                return Task.FromResult(Reply(set));
            };
            var store = new OpenCodeModelFavoriteStore("source", client, "/verified project");
            await store.LoadAsync();
            await new OpenCodeModelFavoriteStore("source", client, "/verified project").LoadAsync();
            var row = new OpenCodeModelPickerRow("new-provider", "new-model", "New", null, true, Array.Empty<string>(), null);
            store.Favorites.Toggle(row);
            await store.SaveAsync(row, true);
            check(hellos == 1 && gets == 3 && sets == 1, "capability cached once per source/location, toggle freshly reads");
            check(SettingsService.Writes == 0 && WslQuickAccessHome.Reads == 0, "capable WSL and Remote both avoid fallback IO");
            check(store.Favorites.Keys.Single() == ("provider", "vendor/model:latest"), "set reply is authoritative");
            check(store.Status.Contains("服务器插件"), "server route notice");
            check(OpenCodeModelFavoritesRpc.Route(true, wsl) == OpenCodeModelFavoriteRoute.Rpc, "capability takes precedence for both source kinds");
        }

        foreach (var unsupported in new[] { "absent-capability", "404", "unknown-method", "old-version" })
        foreach (var wsl in new[] { true, false })
        {
            SettingsService.State = new() { OpenCode = new(wsl ? OpenCodeConnectionKind.Wsl : OpenCodeConnectionKind.Remote, "synthetic"),
                OpenCodeModelFavorites = new[] { new OpenCodeModelFavorite("source", "local", "m") } };
            WslQuickAccessHome.Reads = 0;
            OpenCodeWslQuickAccess.FixturePath = Path.Combine(evidence, "wsl-model-state.json");
            await File.WriteAllTextAsync(OpenCodeWslQuickAccess.FixturePath, "{\"favorite\":[{\"providerID\":\"file\",\"modelID\":\"m\"}]}");
            var calls = 0;
            var client = new OpenCodeClient();
            client.Send = (_, path, _, _, _) =>
            {
                calls++;
                check(path.EndsWith("/hello"), "unsupported plugin never calls favorites methods");
                return Task.FromResult(unsupported switch
                {
                    "404" => Reply("credential-looking body never echoed", 404),
                    "unknown-method" => Reply("{\"type\":\"rpc.method_not_found\",\"message\":\"sensitive\"}", 400),
                    "old-version" => Reply("{\"output\":{\"protocolVersion\":null,\"supportedVersions\":[1],\"capabilities\":[]}}"),
                    _ => Reply("{\"output\":{\"protocolVersion\":1,\"capabilities\":[\"billion.origin.v1\"]}}"),
                });
            };
            var store = new OpenCodeModelFavoriteStore("source", client, "/project");
            await store.LoadAsync();
            await new OpenCodeModelFavoriteStore("source", client, "/project").LoadAsync();
            check(calls == 1, "unsupported result is cached");
            check(WslQuickAccessHome.Reads == (wsl ? 2 : 0) && store.Favorites.Keys.Single().ProviderId == (wsl ? "file" : "local"), "absent capability uses WSL file or app settings");
            check(store.Status.Contains(wsl ? "WSL 文件" : "此应用"), "fallback notice names actual route");
            check(OpenCodeModelFavoritesRpc.Route(false, wsl) == (wsl ? OpenCodeModelFavoriteRoute.WslFile : OpenCodeModelFavoriteRoute.AppSettings), "legacy routing decision");
        }

        foreach (var status in new[] { 401, 403, 500 })
        {
            var client = new OpenCodeClient { Send = (_, _, _, _, _) => Task.FromResult(Reply("{\"type\":\"rpc.internal\",\"message\":\"sensitive\"}", status)) };
            var store = new OpenCodeModelFavoriteStore("source", client, "/project");
            try { await store.LoadAsync(); check(false, "auth/internal error must propagate"); }
            catch (OpenCodePluginRpcException ex) { check(ex.StatusCode == status && !ex.IsUnsupported && !ex.Message.Contains("sensitive"), "auth/internal errors are sanitized and never capability absence"); }
            var writes = SettingsService.Writes;
            try { await store.SaveAsync(new("p", "m", "M", null, true, Array.Empty<string>(), null), true); check(false, "failed negotiation cannot silently save locally"); }
            catch (IOException) { check(SettingsService.Writes == writes, "load error prevents local divergence"); }
        }
        foreach (var malformed in new[] { "[]", "{}", "{\"data\":{}}", "{\"output\":null}", "not-json" })
        {
            var client = new OpenCodeClient { Send = (_, _, _, _, _) => Task.FromResult(Reply(malformed)) };
            try { await client.SupportsModelFavoritesAsync("/project", default); check(false, "malformed output rejected"); }
            catch (OpenCodeProtocolException) { check(true, "malformed output maps to protocol error"); }
        }
        foreach (var status in new[] { 401, 404, 500 })
        {
            var writes = SettingsService.Writes;
            var calls = 0;
            var client = new OpenCodeClient();
            client.Send = (_, path, _, _, _) =>
            {
                calls++;
                return Task.FromResult(Reply(path.EndsWith("/hello") ? hello : path.EndsWith("/model.favorites.get") ? get : "{\"type\":\"rpc.internal\",\"message\":\"sensitive\"}",
                    path.EndsWith("/model.favorites.set") ? status : 200));
            };
            var store = new OpenCodeModelFavoriteStore("source", client, "/project");
            await store.LoadAsync();
            try { await store.SaveAsync(new("p", "m", "M", null, true, Array.Empty<string>(), null), true); check(false, "failed set expected"); }
            catch (OpenCodePluginRpcException ex)
            {
                check(ex.StatusCode == status && !ex.Message.Contains("sensitive") && SettingsService.Writes == writes && calls == 4,
                    "set errors propagate without retry or switching to local persistence");
            }
        }
        foreach (var error in new Exception[] { new HttpRequestException("sensitive"), new IOException("sensitive"), new OperationCanceledException("sensitive") })
        {
            var client = new OpenCodeClient { Send = (_, _, _, _, _) => throw error };
            try { await client.PluginRpcAsync("hello", "/project", new { protocolVersions = new[] { 1 } }, default); check(false, "transport error expected"); }
            catch (OpenCodeException ex) { check((error is OperationCanceledException ? ex is OpenCodeTimeoutException : ex is OpenCodeTransportException) && !ex.Message.Contains("sensitive"), "timeout/transport mapping"); }
        }
        var cancelled = new CancellationToken(true);
        var cancellingClient = new OpenCodeClient { Send = (_, _, _, _, ct) => { ct.ThrowIfCancellationRequested(); throw new Exception("unreachable"); } };
        try { await cancellingClient.PluginRpcAsync("hello", "/project", new { }, cancelled); check(false, "cancellation expected"); }
        catch (OperationCanceledException ex) { check(ex.CancellationToken == cancelled, "caller cancellation preserved"); }
        foreach (var invalid in new[] { "{\"favorites\":[{}]}", "{\"favorites\":[{\"providerID\":\"p\",\"modelID\":\"bad id\"}]}", "{\"favorites\":[],\"extra\":true}" })
        {
            using var json = JsonDocument.Parse(invalid);
            try { OpenCodeModelFavoritesRpc.Read(json.RootElement); check(false, "invalid favorites rejected"); }
            catch (OpenCodeProtocolException) { check(true, "strict favorite output validation"); }
        }
        check(File.ReadAllText(Path.Combine(root, "src/AIFrontend/Pages/OpenCodePage.BuiltinActions.cs")).Contains("favoriteClient: owner.Ticket.Binding.Client")
            && File.ReadAllText(Path.Combine(root, "src/AIFrontend/Pages/OpenCodePage.BlankConversation.cs")).Contains("favoriteClient: client, favoriteDirectory: basis.Directory"),
            "session and blank callers pass existing authenticated source client/location");
    }
}
