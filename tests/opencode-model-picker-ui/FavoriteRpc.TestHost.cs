// Explicit transport seam: no HTTP socket, credential, file preference, WSL or native UI access.
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode;

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
