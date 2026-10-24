// Explicit test doubles: only the favorite store and plugin partial are production code.
// No real settings, WSL processes, HTTP socket, credentials, or native UI are accessed.
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode
{
    public sealed partial class OpenCodeClient
    {
        private readonly TimeSpan _timeout = TimeSpan.FromMilliseconds(100);
        internal Func<HttpMethod, string, IReadOnlyList<KeyValuePair<string, string>>?, string, CancellationToken, Task<HttpResponseMessage>> Send = null!;
        private static HttpContent CreateJsonContent(object value) => new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
        private static IReadOnlyList<KeyValuePair<string, string>> LocationQuery(string directory)
            => new[] { new KeyValuePair<string, string>("location[directory]", directory) };
        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, IReadOnlyList<KeyValuePair<string, string>>? query, HttpContent content, CancellationToken ct)
            => await Send(method, path, query, await content.ReadAsStringAsync(ct), ct);
        private static async Task<ReadOnlyMemory<byte>> ReadResponseBodyAsync(HttpContent content, CancellationToken ct)
            => await content.ReadAsByteArrayAsync(ct);
    }
}
namespace AIFrontend.Models
{
    public enum OpenCodeConnectionKind { Remote = 1, Wsl = 2 }
    public sealed record OpenCodeModelFavorite(string SourceId, string ProviderId, string ModelId);
    public sealed record OpenCodeSettings(OpenCodeConnectionKind Kind, string? WslDistribution);
    public sealed record AppSettings
    {
        public OpenCodeSettings? OpenCode { get; init; }
        public OpenCodeModelFavorite[]? OpenCodeModelFavorites { get; init; }
    }
}
namespace AIFrontend.Services.Configuration
{
    public sealed class SettingsService
    {
        internal static AIFrontend.Models.AppSettings State = new();
        internal static int Writes;
        public Task<AIFrontend.Models.AppSettings> LoadAsync() => Task.FromResult(State);
        public Task UpdateAsync(Func<AIFrontend.Models.AppSettings, AIFrontend.Models.AppSettings> update)
        { Writes++; State = update(State); return Task.CompletedTask; }
    }
}
namespace AIFrontend.Pages.OpenCode
{
    internal sealed record WslQuickAccessTarget(string Distribution, string LinuxDirectory, string HostPath);
    internal sealed record WslQuickAccessState(WslQuickAccessTarget? Target);
    internal sealed record FavoriteTestCommand(string Executable, string[] Arguments);
    internal sealed class WslHomeCommandRunner
    {
        internal Task<string?> ReadAsync(FavoriteTestCommand command, CancellationToken ct)
            => throw new Exception("Unexpected WSL process in offline favorites tests");
    }
    internal static class WslQuickAccessHome
    {
        internal static int Reads;
        internal static Task<string> ResolveAsync(string distribution, string wsl, WslHomeCommandRunner runner, CancellationToken ct)
        { Reads++; return Task.FromResult("/synthetic/home"); }
    }
    internal static class OpenCodeWslQuickAccess
    {
        internal static string FixturePath = "";
        internal static WslQuickAccessState Resolve(AIFrontend.Models.OpenCodeConnectionKind kind, bool connected,
            string distribution, string directory, string home) => new(new(distribution, directory, FixturePath));
    }
}
