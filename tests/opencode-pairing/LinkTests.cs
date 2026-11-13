using System;
using System.Collections.Generic;
using System.Net;
using FluentCode.Services.OpenCode;

// Link composition per connection kind, custom origins, and the round trip through the existing parser.
internal static partial class Program
{
    private static OpenCodePairingCode TestCode() => new(Code, TimeSpan.FromSeconds(300));

    private static void LinkComposition()
    {
        var code = TestCode();
        var lan = new List<IPAddress>
        {
            IPAddress.Parse("192.168.1.20"), IPAddress.Parse("127.0.0.1"), IPAddress.Parse("169.254.3.4"),
            IPAddress.Parse("10.0.0.7"), IPAddress.Parse("192.168.1.20"), IPAddress.Parse("fe80::1"),
            IPAddress.Parse("0.0.0.0"), IPAddress.Parse("224.0.0.1"), IPAddress.Parse("255.255.255.255"),
        };

        // Remote: the configured origin only; LAN addresses of this PC are irrelevant.
        var remote = OpenCodePairingLinks.Candidates(OpenCodePairingConnection.Remote, "https://Pair.Invalid:8443/", lan);
        Check(remote.Count == 1 && remote[0].Kind == OpenCodePairingAddressKind.Server
            && remote[0].Origin == "https://pair.invalid:8443" && !remote[0].IsLoopback, "remote origin");
        Check(OpenCodePairingLinks.DefaultIndex(remote) == 0, "remote default");
        RoundTrip(remote[0].Origin, code);

        // WSL: this PC first, then plausible LAN IPv4 addresses with the same port; default is the first LAN one.
        var wsl = OpenCodePairingLinks.Candidates(OpenCodePairingConnection.Wsl, "http://127.0.0.1:49375", lan);
        Check(wsl.Count == 3, "wsl candidate count");
        Check(wsl[0] == new OpenCodePairingAddress(OpenCodePairingAddressKind.ThisPc, "http://127.0.0.1:49375") && wsl[0].IsLoopback, "wsl this PC");
        Check(wsl[1] == new OpenCodePairingAddress(OpenCodePairingAddressKind.Lan, "http://192.168.1.20:49375") && !wsl[1].IsLoopback, "wsl lan 1");
        Check(wsl[2].Origin == "http://10.0.0.7:49375", "wsl lan 2");
        Check(OpenCodePairingLinks.DefaultIndex(wsl) == 1, "wsl default prefers LAN");
        foreach (var address in wsl) RoundTrip(address.Origin, code);
        var wslAlone = OpenCodePairingLinks.Candidates(OpenCodePairingConnection.Wsl, "http://127.0.0.1:49375", Array.Empty<IPAddress>());
        Check(wslAlone.Count == 1 && OpenCodePairingLinks.DefaultIndex(wslAlone) == 0, "wsl without LAN");
        var wslTls = OpenCodePairingLinks.Candidates(OpenCodePairingConnection.Wsl, "https://localhost:49375", lan);
        Check(wslTls.Count == 1 && wslTls[0].IsLoopback, "https WSL offers no IP substitutes");

        // SSH: only the tunnel's local port, never a LAN address.
        var ssh = OpenCodePairingLinks.Candidates(OpenCodePairingConnection.Ssh, "http://127.0.0.1:50122", lan);
        Check(ssh.Count == 1 && ssh[0].Kind == OpenCodePairingAddressKind.ThisPc && ssh[0].IsLoopback, "ssh this PC only");
        RoundTrip(ssh[0].Origin, code);

        // A path prefix cannot form a link the parser accepts: no candidates, custom address required.
        foreach (var kind in new[] { OpenCodePairingConnection.Remote, OpenCodePairingConnection.Wsl, OpenCodePairingConnection.Ssh })
        {
            var prefixed = OpenCodePairingLinks.Candidates(kind, "https://pair.invalid/opencode", lan);
            Check(prefixed.Count == 0 && OpenCodePairingLinks.DefaultIndex(prefixed) == -1, "path prefix has no candidates");
        }
        Check(OpenCodePairingLinks.Candidates(OpenCodePairingConnection.Remote, "not a url").Count == 0, "invalid base has no candidates");
    }
}
