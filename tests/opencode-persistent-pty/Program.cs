using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIFrontend.Pages.OpenCode;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string name)
    { if (!value) throw new Exception(name); _checks++; Console.WriteLine("PASS " + name); }
    private static void Main(string[] args)
    {
        foreach (var file in new[] { "index.html", "terminal.js", "terminal.css", "xterm.js", "xterm.css", "addon-fit.js" })
            Check(OpenCodePersistentPtyRendererPolicy.AllowsResource(OpenCodePersistentPtyRendererPolicy.Origin + file), "allow exact offline asset " + file);
        foreach (var uri in new[] { "https://evil.invalid/xterm.js", "https://persistent-pty.aif.invalid:123/xterm.js",
            "https://user@persistent-pty.aif.invalid/xterm.js", "https://persistent-pty.aif.invalid/xterm.js?url=remote",
            "https://persistent-pty.aif.invalid/xterm.js#script", "http://persistent-pty.aif.invalid/index.html",
            "https://persistent-pty.aif.invalid/other.html", "file:///tmp/index.html", "javascript:alert(1)", "data:text/html,hello" })
            Check(!OpenCodePersistentPtyRendererPolicy.AllowsResource(uri), "reject non-asset navigation/resource " + uri);
        var context = new OpenCodePersistentPtyViewContext(Guid.NewGuid(), 4, "session", "pty-opaque");
        foreach (var phase in Enum.GetValues<OpenCodePersistentPtyPhase>())
        foreach (var role in Enum.GetValues<OpenCodePersistentPtyRole>())
        foreach (var authorized in new[] { false, true })
        {
            var state = new OpenCodePersistentPtyPresentation(context, "terminal", "/work", phase, role, "智能体", true, authorized);
            Check(state.Writable == (phase == OpenCodePersistentPtyPhase.Live && role == OpenCodePersistentPtyRole.Controller && authorized),
                $"input role/phase/authorization gate {phase}/{role}/{authorized}");
            Check(state.NeedsInputConsent == (phase == OpenCodePersistentPtyPhase.Live && (role == OpenCodePersistentPtyRole.Observer || !authorized)),
                $"explicit consent remains available without automatic promotion {phase}/{role}/{authorized}");
        }
        var promoted = new OpenCodePersistentPtyPresentation(context, "terminal", "/work", OpenCodePersistentPtyPhase.Live,
            OpenCodePersistentPtyRole.Controller, "当前连接", true, false);
        Check(!promoted.Writable && promoted.NeedsInputConsent && promoted.Status == "控制权已分配，输入未启用"
            && promoted.Role == OpenCodePersistentPtyRole.Controller, "automatic server promotion preserves actual role without claiming manual control");
        Check(promoted.ConsentAction == "启用输入" && !promoted.ConsentExplanation.Contains("转为只读")
            && !promoted.ConsentExplanation.Contains("当前控制者"), "existing-controller confirmation does not falsely promise another controller will lose control");
        Check((promoted with { Role = OpenCodePersistentPtyRole.Observer }).ConsentAction == "接管终端"
            && !(promoted with { CanTakeControl = false }).NeedsInputConsent, "takeover and enable-input respect explicit host capability separately");
        Check(context != context with { Generation = 5 } && context != context with { PtyId = "other" }
            && context != context with { SessionId = "other" }, "view events retain generation, PTY and session identity");
        Check(OpenCodePersistentPtyRendererPolicy.ValidGeometry(80, 24) && !OpenCodePersistentPtyRendererPolicy.ValidGeometry(0, 24)
            && !OpenCodePersistentPtyRendererPolicy.ValidGeometry(80, 1001), "renderer bounds resource geometry");
        Check(!OpenCodePluginActionsPolicy.CanSubmitExactName("") && !OpenCodePluginActionsPolicy.CanSubmitExactName(null), "empty names cannot submit");
        foreach (var name in new[] { "  Exact Key  ", "中文/键", " ", "A.B-v2" })
        {
            var request = new OpenCodePresetApplyRequest(new(Guid.NewGuid(), 1, "session", "/work"), name);
            Check(OpenCodePluginActionsPolicy.CanSubmitExactName(request.ExactName) && request.ExactName == name,
                "opaque preset key is accepted without trim or substitution");
        }
        Check(new OpenCodeClearEncryptedCounts(0, 0, 0).Summary.Contains("0 条消息")
            && new OpenCodeClearEncryptedCounts(2, 3, 5).Summary.Contains("5 个字段")
            && new OpenCodeClearEncryptedCounts(-1, 3, 5).Summary == "清除结果无法确认", "structured counts use fixed safe copy; invalid counts never imply success");
        var actionContext = new OpenCodePluginActionContext(Guid.NewGuid(), 4, "session", "/work");
        var protection = new OpenCodePluginUnknownProtection(Guid.NewGuid(), OpenCodePluginActionKind.ApplyPreset);
        var actions = new OpenCodePluginActionsPresentation(actionContext, "预设", true, true, false, UnknownProtection: protection);
        var acknowledgement = new OpenCodePluginProtectionAcknowledgement(actionContext, protection.OperationId);
        Check(OpenCodePluginActionsPolicy.CanAcknowledge(actions, acknowledgement), "manual acknowledgement targets exact current local operation token");
        Check(!OpenCodePluginActionsPolicy.CanAcknowledge(actions with { UnknownProtection = protection with { OperationId = Guid.NewGuid() } }, acknowledgement),
            "late confirmation cannot acknowledge a newer plugin protection");
        foreach (var changed in new[] { actionContext with { Generation = 5 }, actionContext with { SessionId = "other" },
            actionContext with { Directory = "/other" }, actionContext with { ViewId = Guid.NewGuid() } })
            Check(!OpenCodePluginActionsPolicy.CanAcknowledge(actions with { Context = changed }, acknowledgement), "acknowledgement cannot cross view/session/directory/generation ownership");
        Check(!OpenCodePluginActionsPolicy.CanAcknowledge(actions with { Pending = true }, acknowledgement)
            && !OpenCodePluginActionsPolicy.CanAcknowledge(actions with { UnknownProtection = null }, acknowledgement)
            && !OpenCodePluginActionsPolicy.CanAcknowledge(null, acknowledgement)
            && !OpenCodePluginActionsPolicy.CanAcknowledge(actions, acknowledgement with { OperationId = Guid.Empty }),
            "pending, removed, hidden or empty protection is never manually acknowledged");

        var assets = Path.Combine(args[0], "src/AIFrontend/Assets/OpenCode/PersistentPty");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(assets, "vendor-manifest.json")));
        foreach (var package in manifest.RootElement.GetProperty("packages").EnumerateArray())
        foreach (var entry in package.GetProperty("files").EnumerateObject())
            Check(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(assets, entry.Name)))) == entry.Value.GetString(),
                "unmodified vendor asset hash " + entry.Name);
        var html = File.ReadAllText(Path.Combine(assets, "index.html"));
        Check(html.Contains("connect-src 'none'") && html.Contains("frame-src 'none'") && !html.Contains("src=\"https://"), "renderer CSP and assets have no network or frame dependency");
        Console.WriteLine($"PASS {_checks} pure presentation/security/vendor checks; no backend or native execution.");
    }
}
