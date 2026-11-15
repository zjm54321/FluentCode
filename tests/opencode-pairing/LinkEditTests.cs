using System;
using FluentCode.Services.OpenCode;

// The editable link box: what the QR code encodes, which texts are usable, and how an edit survives a new code.
internal static partial class Program
{
    private const string Generated = "http://192.168.1.20:4096/auth/connect/" + Code;
    private const string NewCode = "renewed_CODE-456";

    private static void LinkEditEvaluation()
    {
        var same = OpenCodePairingLinkEdit.Evaluate(Generated, Generated, Code);
        Check(same.Kind == OpenCodePairingLinkEditKind.Generated && !same.IsEdited && !same.IsProblem, "generated link is unedited");
        Check(same.Link == Generated && same.ShowQr && same.QrNote.Length == 0, "generated link shows its QR code");
        Check(same.Address == "http://192.168.1.20:4096", "address part of the generated link");
        Check(same.Status.Length > 0, "hint under the box");
        var padded = OpenCodePairingLinkEdit.Evaluate("  " + Generated + " \t", Generated, Code);
        Check(padded.Kind == OpenCodePairingLinkEditKind.Generated && padded.Link == Generated, "outer whitespace is not an edit");

        // The QR code encodes exactly what is in the box: the address as typed, nothing normalised.
        foreach (var (typed, address) in new[]
        {
            ("http://100.64.0.5:4096/auth/connect/" + Code, "http://100.64.0.5:4096"),
            ("http://100.64.0.5/auth/connect/" + Code, "http://100.64.0.5"),
            ("https://my-pc.tail1234.ts.net/auth/connect/" + Code, "https://my-pc.tail1234.ts.net"),
            ("HTTP://My-PC.Lan:80/auth/connect/" + Code, "HTTP://My-PC.Lan:80"),
            ("http://[fd7a:115c:a1e0::1]:4096/auth/connect/" + Code, "http://[fd7a:115c:a1e0::1]:4096"),
        })
        {
            var state = OpenCodePairingLinkEdit.Evaluate(typed, Generated, Code);
            Check(state.Kind == OpenCodePairingLinkEditKind.Edited && state.IsEdited && !state.IsProblem, "edited address accepted: " + address);
            Check(state.Link == typed && state.ShowQr && state.Address == address, "QR text is the box text: " + address);
            Check(state.Status != same.Status && state.QrNote.Length == 0, "edited status: " + address);
            OpenCodePairingService.Parse(state.Link!);
            Redacted(state.ToString());
            Redacted(state.Status);
        }

        // Loopback: usable (copy, open on this PC) but no QR code for other devices.
        foreach (var typed in new[] { "http://127.0.0.1:4096/auth/connect/" + Code, "http://localhost/auth/connect/" + Code, "http://[::1]:4096/auth/connect/" + Code })
        {
            var state = OpenCodePairingLinkEdit.Evaluate(typed, Generated, Code);
            Check(state.Link == typed && state.IsLoopback && !state.ShowQr && !state.IsProblem && state.QrNote.Length > 0, "loopback has no QR: " + typed);
        }
        var loopbackGenerated = "http://127.0.0.1:50122/auth/connect/" + Code;
        var tunnel = OpenCodePairingLinkEdit.Evaluate(loopbackGenerated, loopbackGenerated, Code);
        Check(tunnel.Kind == OpenCodePairingLinkEditKind.Generated && !tunnel.ShowQr && tunnel.Link == loopbackGenerated, "generated loopback link");

        // Not a pairing link: no QR code, no link to copy, one line that says so, and the way back.
        foreach (var (typed, kind) in new[]
        {
            ("", OpenCodePairingLinkEditKind.Empty),
            ("   ", OpenCodePairingLinkEditKind.Empty),
            ("100.64.0.5:4096/auth/connect/" + Code, OpenCodePairingLinkEditKind.Invalid),
            ("http://100.64.0.5:4096", OpenCodePairingLinkEditKind.Invalid),
            ("http://100.64.0.5:4096/auth/connect/", OpenCodePairingLinkEditKind.Invalid),
            ("http://:4096/auth/connect/" + Code, OpenCodePairingLinkEditKind.Invalid),
            ("http://100.64.0.5:4096/prefix/auth/connect/" + Code, OpenCodePairingLinkEditKind.Invalid),
            ("http://100.64.0.5:4096/auth/connect/" + Code + "?x=1", OpenCodePairingLinkEditKind.Invalid),
            ("http://100.64.0.5:4096/auth/connect/" + Code + "/", OpenCodePairingLinkEditKind.Invalid),
            ("http://user:pw" + "@100.64.0.5:4096/auth/connect/" + Code, OpenCodePairingLinkEditKind.Invalid),
            ("http://100.64.0.5:99999/auth/connect/" + Code, OpenCodePairingLinkEditKind.Invalid),
            ("http://100.64 .0.5:4096/auth/connect/" + Code, OpenCodePairingLinkEditKind.Invalid),
            ("ftp://100.64.0.5/auth/connect/" + Code, OpenCodePairingLinkEditKind.Invalid),
            ("http://100.64.0.5:4096/auth/connect/" + Code + "，", OpenCodePairingLinkEditKind.Invalid),
            ("http://" + new string('a', OpenCodePairingLinkEdit.MaxLength) + ".invalid/auth/connect/" + Code, OpenCodePairingLinkEditKind.Invalid),
            // Right shape, wrong code: it would scan but could not pair.
            ("http://100.64.0.5:4096/auth/connect/" + Code[..^1], OpenCodePairingLinkEditKind.StaleCode),
            ("http://100.64.0.5:4096/auth/connect/" + Code + "x", OpenCodePairingLinkEditKind.StaleCode),
            ("http://192.168.1.20:4096/auth/connect/" + NewCode, OpenCodePairingLinkEditKind.StaleCode),
            ("http://192.168.1.20:4096/auth/connect/" + Code.ToUpperInvariant(), OpenCodePairingLinkEditKind.StaleCode),
        })
        {
            var state = OpenCodePairingLinkEdit.Evaluate(typed, Generated, Code);
            Check(state.Kind == kind, "kind of [" + typed.Length + " chars] " + kind);
            Check(state.IsProblem && state.Link is null && state.Address is null && !state.ShowQr && state.IsEdited, "unusable text has no link or QR: " + kind);
            Check(state.Status.Length > 0 && state.QrNote.Length > 0, "reason given: " + kind);
            Redacted(state.Status + state.QrNote + state);
        }

        // Without a current code nothing is usable, whatever the text says.
        Check(OpenCodePairingLinkEdit.Evaluate(Generated, Generated, null).IsProblem, "no current code");
        Check(OpenCodePairingLinkEdit.Evaluate(null, null, Code).Kind == OpenCodePairingLinkEditKind.Empty, "null text is empty");
        Check(!OpenCodePairingLinkEdit.Evaluate(null, null, Code).IsEdited, "empty box with nothing generated is not an edit");
    }

    private static void LinkEditRenewal()
    {
        var renewed = "http://192.168.1.20:4096/auth/connect/" + NewCode;

        // Untouched box: the new link simply replaces the old one.
        var none = OpenCodePairingLinkEdit.Capture(Generated, Generated, Code);
        Check(none.IsNone, "unedited box carries nothing");
        Check(OpenCodePairingLinkEdit.Apply(none, renewed, NewCode, out var kept) == renewed && !kept, "unedited box takes the new link");
        Check(OpenCodePairingLinkEdit.Capture("", Generated, Code).IsNone && OpenCodePairingLinkEdit.Capture(null, Generated, Code).IsNone, "an emptied box carries nothing");

        // Only the address was changed: it moves onto the new code, exactly as typed, and never keeps the old code.
        foreach (var address in new[] { "http://100.64.0.5:4096", "https://my-pc.tail1234.ts.net", "HTTP://My-PC.Lan:80", "http://127.0.0.1:4096" })
        {
            var carry = OpenCodePairingLinkEdit.Capture(" " + address + "/auth/connect/" + Code + " ", Generated, Code);
            Check(carry.Address == address && carry.Text is null && !carry.IsNone, "address carried: " + address);
            Redacted(carry.ToString());
            var text = OpenCodePairingLinkEdit.Apply(carry, renewed, NewCode, out kept);
            Check(text == address + "/auth/connect/" + NewCode && !kept, "address merged into the renewed link: " + address);
            Redacted(text);
            var state = OpenCodePairingLinkEdit.Evaluate(text, renewed, NewCode);
            Check(state.Kind == OpenCodePairingLinkEditKind.Edited && state.Link == text, "merged link is usable: " + address);
        }

        // Anything else is kept as typed and reported as renewed; it can never show a QR code.
        foreach (var typed in new[] { "http://100.64.0.5:4096/auth/connect/" + Code[..^1], "http://100.64", "http://100.64.0.5:4096/auth/connect/" + Code + "/" })
        {
            var carry = OpenCodePairingLinkEdit.Capture(typed, Generated, Code);
            Check(carry.Address is null && carry.Text == typed, "unmergeable text carried as is");
            Check(!carry.ToString().Contains("100.64", StringComparison.Ordinal), "carry is redacted");
            var text = OpenCodePairingLinkEdit.Apply(carry, renewed, NewCode, out kept);
            Check(text == typed && kept, "unmergeable text kept");
            var plain = OpenCodePairingLinkEdit.Evaluate(text, renewed, NewCode);
            var state = OpenCodePairingLinkEdit.Evaluate(text, renewed, NewCode, renewed: true);
            Check(state.IsProblem && !state.ShowQr && state.IsEdited && state.Kind == plain.Kind, "kept text is unusable and restorable");
            Check(state.Status != plain.Status && state.Status.Contains("重新生成", StringComparison.Ordinal), "kept text says the code was renewed");
            Redacted(state.Status);
        }

        // An edited link that still carries the superseded code is stale after renewal even though it was usable before.
        var old = "http://100.64.0.5:4096/auth/connect/" + Code;
        Check(OpenCodePairingLinkEdit.Evaluate(old, Generated, Code).ShowQr, "usable before renewal");
        var after = OpenCodePairingLinkEdit.Evaluate(old, renewed, NewCode, renewed: true);
        Check(after.Kind == OpenCodePairingLinkEditKind.StaleCode && !after.ShowQr && after.Link is null, "superseded code never reaches a QR code");

        // A carried address that cannot form a link (it never comes from Capture) falls back to the generated link.
        var bad = OpenCodePairingLinkCarry.OfAddress("http://100.64.0.5:4096/prefix");
        Check(OpenCodePairingLinkEdit.Apply(bad, renewed, NewCode, out kept) == renewed && !kept, "bad address falls back");
        Check(OpenCodePairingLinkEdit.Apply(OpenCodePairingLinkCarry.OfAddress("http://100.64.0.5:4096"), renewed, "bad code", out kept) == renewed && !kept, "bad code falls back");
        Check(OpenCodePairingLinkEdit.Apply(OpenCodePairingLinkCarry.OfText(""), renewed, NewCode, out kept) == renewed && !kept, "empty kept text falls back");
    }
}
