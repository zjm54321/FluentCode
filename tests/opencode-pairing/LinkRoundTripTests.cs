using System;
using FluentCode.Services.OpenCode;

internal static partial class Program
{
    private static void RoundTrip(string origin, OpenCodePairingCode code)
    {
        var link = OpenCodePairingLinks.Compose(origin, code);
        Check(link == origin + "/auth/connect/" + code.Code, "link shape");
        var parsed = OpenCodePairingService.Parse(link);
        Check(parsed.BaseUrl == origin, "parser returns the same origin");
    }

    private static void CustomOrigins()
    {
        var code = TestCode();
        foreach (var (input, expected) in new[]
        {
            ("192.168.1.20:4096", "http://192.168.1.20:4096"),
            ("  my-pc.lan:4096  ", "http://my-pc.lan:4096"),
            ("https://Code.Example.Invalid/", "https://code.example.invalid"),
            ("http://[::1]:4096", "http://[::1]:4096"),
            ("http://host.invalid:80", "http://host.invalid"),
        })
        {
            Check(OpenCodePairingLinks.TryNormalizeOrigin(input, out var origin, out var error), "accepts " + input);
            Check(origin == expected && error.Length == 0, "normalises " + input);
            RoundTrip(origin, code);
        }

        var at = "user" + ":" + "pw" + "@";
        foreach (var input in new[]
        {
            "", "   ", "ftp://host.invalid", "http://" + at + "host.invalid", "http://host.invalid/prefix",
            "http://host.invalid/?a=1", "http://host.invalid/#x", "host.invalid/path", "http://", "a b", "http://host.invalid:99999",
        })
        {
            Check(!OpenCodePairingLinks.TryNormalizeOrigin(input, out var origin, out var error), "rejects " + input);
            Check(origin.Length == 0 && error.Length > 0, "reason given for " + input);
            try { OpenCodePairingLinks.Compose(input, code); throw new Exception("Expected compose rejection"); }
            catch (OpenCodeConfigurationException failure) { Redacted(failure); }
        }
    }

    private static void TimeText()
    {
        Check(OpenCodePairingLinks.FormatRemaining(TimeSpan.FromSeconds(300)) == "5 分 00 秒", "5 minutes");
        Check(OpenCodePairingLinks.FormatRemaining(TimeSpan.FromSeconds(244.2)) == "4 分 05 秒", "rounds up");
        Check(OpenCodePairingLinks.FormatRemaining(TimeSpan.FromSeconds(58)) == "58 秒", "seconds only");
        Check(OpenCodePairingLinks.FormatRemaining(TimeSpan.FromMilliseconds(1)) == "1 秒", "last second");
        Check(OpenCodePairingLinks.FormatRemaining(TimeSpan.FromSeconds(-3)) == "0 秒", "never negative");
        Check(OpenCodePairingLinks.DescribeLifetime(TimeSpan.FromSeconds(300)) == "5 分钟", "minutes");
        Check(OpenCodePairingLinks.DescribeLifetime(TimeSpan.FromSeconds(90)) == "90 秒", "seconds");
        Check(OpenCodePairingLinks.IsLoopbackOrigin("http://localhost:1") && OpenCodePairingLinks.IsLoopbackOrigin("http://[::1]:1")
            && !OpenCodePairingLinks.IsLoopbackOrigin("http://192.168.1.20:1"), "loopback detection");
    }
}
