using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode;

public sealed partial class OpenCodeClient
{
    /// <summary>Configured local first hop only; this cannot detect a user-managed tunnel/proxy behind loopback.</summary>
    public bool SupportsLocalBillionDiscovery => OpenCodeBillionDiscoveryJson.LoopbackHost(_baseUri);

    /// <summary>Frontend plugin metadata RPC, not assumed installed. Reports the OpenCode process-declared
    /// Billion address, not verified engine readiness or ownership. Uses this client's base URL/auth and the exact
    /// trusted session location. No engine request, session-content read, port scan, consent or retry.</summary>
    public async Task<OpenCodeBillionDiscoveryResult> DiscoverBillionOriginAsync(string directory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!SupportsLocalBillionDiscovery) return new(OpenCodeBillionDiscoveryStatus.UnsupportedSource);
        if (string.IsNullOrWhiteSpace(directory) || directory.Length > 32768)
            return new(OpenCodeBillionDiscoveryStatus.InvalidLocation);
        foreach (var ch in directory) if (char.IsControl(ch)) return new(OpenCodeBillionDiscoveryStatus.InvalidLocation);
        var bridge = await DesktopBridge.NegotiateAsync(this, directory, cancellationToken).ConfigureAwait(false);
        if (!bridge.Supports("billion.origin.v1")) return new(OpenCodeBillionDiscoveryStatus.UnsupportedProtocol);
        const string endpoint = "api/rpc/" + OpenCodeDesktopBridge.PluginId + "/billion.origin";
        var query = new[] { new KeyValuePair<string, string>("location[directory]", directory) };
        using var content = CreateJsonContent(new { input = new { } });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            using var response = await SendAsync(HttpMethod.Post, endpoint, query, content, timeout.Token).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
                return new(OpenCodeBillionDiscoveryStatus.HttpError, HttpStatus: status); // Never read or echo error bodies.
            if (response.StatusCode != HttpStatusCode.OK)
                return new(OpenCodeBillionDiscoveryStatus.Malformed, HttpStatus: status);
            const int maxBytes = 4096;
            if (response.Content.Headers.ContentLength is long length && length > maxBytes)
                return new(OpenCodeBillionDiscoveryStatus.Malformed);
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var bytes = new byte[1024];
            int count;
            while ((count = await stream.ReadAsync(bytes, timeout.Token).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + count > maxBytes) return new(OpenCodeBillionDiscoveryStatus.Malformed);
                buffer.Write(bytes, 0, count);
            }
            using var json = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            return OpenCodeBillionDiscoveryJson.Parse(json.RootElement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Billion discovery cancelled.", cancellationToken); }
        catch (OperationCanceledException) { return new(OpenCodeBillionDiscoveryStatus.Timeout); }
        catch (JsonException) { return new(OpenCodeBillionDiscoveryStatus.Malformed); }
        catch (HttpRequestException) { return new(OpenCodeBillionDiscoveryStatus.TransportUnavailable); }
        catch (IOException) { return new(OpenCodeBillionDiscoveryStatus.TransportUnavailable); }
    }
}
