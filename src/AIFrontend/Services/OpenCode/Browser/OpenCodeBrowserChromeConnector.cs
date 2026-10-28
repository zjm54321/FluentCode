using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode.Browser;

internal static class OpenCodeBrowserChromeConnector
{
    internal static Uri ValidateEndpoint(string value, bool webSocketOnly = false)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != "ws" && endpoint.Scheme != "wss" && (webSocketOnly || (endpoint.Scheme != "http" && endpoint.Scheme != "https")))
            || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment)
            || !IsLoopback(endpoint.Host))
            throw new ArgumentException("Chrome debugging endpoint must be an absolute, credential-free loopback HTTP(S) or WS(S) URL.");
        return endpoint;
    }

    internal static bool IsLoopback(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
           || (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));

    internal static async Task<Uri> ResolveEndpointAsync(string value, CancellationToken cancellationToken)
    {
        var endpoint = ValidateEndpoint(value);
        // Resolve localhost ourselves. DNS must not turn an apparently local endpoint into a remote connection.
        if (string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            endpoint = new UriBuilder(endpoint) { Host = "127.0.0.1" }.Uri;
        if (endpoint.Scheme is "ws" or "wss")
        {
            if (!endpoint.AbsolutePath.StartsWith("/devtools/browser/", StringComparison.Ordinal))
                throw new IOException("Chrome attach requires the browser-level WebSocket endpoint, not an existing page.");
            return endpoint;
        }
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var version = new UriBuilder(endpoint) { Path = "/json/version", Query = "" }.Uri;
        using var response = await client.GetAsync(version, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new IOException("Chrome debugging discovery redirects are not allowed.");
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var result = ValidateEndpoint(json?["webSocketDebuggerUrl"]?.GetValue<string>()
            ?? throw new IOException("Chrome did not return a browser WebSocket endpoint."), true);
        if (!result.AbsolutePath.StartsWith("/devtools/browser/", StringComparison.Ordinal))
            throw new IOException("Chrome attach requires the browser-level WebSocket endpoint, not an existing page.");
        return string.Equals(result.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            ? new UriBuilder(result) { Host = "127.0.0.1" }.Uri : result;
    }

    internal static string FindExecutable(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Path.IsPathFullyQualified(configured) || !File.Exists(configured))
                throw new FileNotFoundException("The configured Chrome executable does not exist at an absolute path.", configured);
            return configured;
        }
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
        };
        return paths.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("Chrome is not installed in a usual location. Select the installed Chrome executable in browser settings.");
    }

    internal static void ValidateProfile(string profile)
    {
        if (!Path.IsPathFullyQualified(profile)) throw new ArgumentException("Chrome workspace profile must be an absolute directory.");
        var normal = Path.GetFullPath(profile).TrimEnd(Path.DirectorySeparatorChar);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var vendor in new[] { "Google/Chrome", "Google/Chrome Beta", "Google/Chrome Dev", "Google/Chrome SxS", "Chromium" })
        {
            var userProfile = Path.GetFullPath(Path.Combine(local, vendor.Replace('/', Path.DirectorySeparatorChar), "User Data"));
            if (normal.Equals(userProfile, StringComparison.OrdinalIgnoreCase)
                || normal.StartsWith(userProfile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Chrome must use an app-owned profile, never an installed browser's user profile.");
        }
    }

    internal static Process Launch(string executable, string profile, OpenCodeBrowserProxyOptions proxy)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in new[]
        {
            "--user-data-dir=" + profile, "--remote-debugging-port=0", "--remote-debugging-address=127.0.0.1",
            "--enable-automation", "--no-first-run", "--no-default-browser-check", "--no-startup-window",
            "--disable-background-networking", "--disable-component-update", "--disable-sync", "--disable-extensions", "--disable-quic",
            "--force-webrtc-ip-handling-policy=disable_non_proxied_udp", "--proxy-server=" + proxy.Address.GetLeftPart(UriPartial.Authority),
            "--proxy-bypass-list=<-loopback>"
        }) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("Chrome did not start.");
    }

    internal static async Task<Uri> WaitForEndpointAsync(Process process, string profile, CancellationToken cancellationToken)
    {
        var portFile = Path.Combine(profile, "DevToolsActivePort");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited) throw new IOException("Chrome exited before opening its debugging endpoint (exit " + process.ExitCode + ").");
            try
            {
                var lines = await File.ReadAllLinesAsync(portFile, cancellationToken).ConfigureAwait(false);
                if (lines.Length >= 2 && int.TryParse(lines[0], out var port) && port is > 0 and <= 65535
                    && lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal))
                    return ValidateEndpoint("ws://127.0.0.1:" + port + lines[1], true);
            }
            catch (IOException) { /* Chrome writes the file after its listener is ready. */ }
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }
}
