// Real BuiltinActions.HasPendingNativeAction, page Send/Stop routes, composer and HTTP guards.
// Plugin owner identities are a labelled boundary: this fixture does not simulate plugin RPC,
// confirmation or server outcomes. Dedicated plugin Page fixtures exercise those contracts.
using System;
using System.Threading.Tasks;
using AIFrontend.Pages;

internal static class PluginPendingGuardTests
{
    internal static async Task Run()
    {
        var checks = 0;
        void Check(bool value, string reason) { checks++; Program.Check(value, reason); }
        var states = new[] { "none", "owner-only", "pending-only", "matching", "equal-but-distinct" };
        foreach (var state in states)
        foreach (var legacy in new[] { false, true })
        {
            using var f = new Program.Fixture();
            f.Page.Text("retain while plugin write is pending");
            f.Page.SetPluginOwnerBoundary(state);
            var blocked = state == "matching";
            Check(f.Page.NativePending == blocked && f.Page.SendEnabled == !blocked,
                "only the current exact pending plugin owner blocks the real Send display: " + state);
            if (legacy) await f.Page.SendLegacyDirect(); else await f.Page.Send();
            Check(f.Http.Posts == (blocked ? 0 : 1) && f.Page.Begins == (blocked ? 0 : 1),
                "plugin owner guard protects actual routed/legacy submission before HTTP/admission: " + state);
            Check(f.Page.Snapshot.HasDraft == blocked,
                "blocked plugin-pending submission retains its exact draft; unprotected submission completes");
            if (blocked)
            {
                f.Page.SetPluginOwnerBoundary("owner-only");
                if (legacy) await f.Page.SendLegacyDirect(); else await f.Page.Send();
                Check(f.Http.Posts == 1 && f.Page.Begins == 1 && !f.Page.Snapshot.HasDraft,
                    "clearing pending ownership releases Send exactly once without losing the retained draft");
            }
        }
        foreach (var state in states)
        {
            using var f = new Program.Fixture();
            f.Page.SetWorking();
            f.Page.SetPluginOwnerBoundary(state);
            var blocked = state == "matching";
            Check(f.Page.ShouldStop() == !blocked && f.Page.SendEnabled == !blocked,
                "current pending plugin owner also protects the real Stop probe/display: " + state);
            await f.Page.StopDirect();
            Check(f.Http.Interrupts == (blocked ? 0 : 1),
                "actual interrupt route cannot cross matching plugin-pending ownership: " + state);
        }
        Console.WriteLine($"PASS pending-plugin native guard: {checks} assertions; real Send/Stop/display/HTTP routes, matched versus absent/retired/equal-but-distinct owner identities; plugin RPC boundary only.");
    }
}

namespace AIFrontend.Pages
{
    public sealed partial class OpenCodePage
    {
        // Only reference identity participates in the production native guard. Equal-valued
        // records deliberately distinguish a retired pending owner from the current owner.
        private sealed record PluginOwnerBoundary(int Generation);
        private PluginOwnerBoundary? _pluginActionOwner, _pluginPendingOwner;
        internal bool NativePending => HasPendingNativeAction();
        internal void SetPluginOwnerBoundary(string state)
        {
            var current = new PluginOwnerBoundary(1);
            _pluginActionOwner = state is "none" or "pending-only" ? null : current;
            _pluginPendingOwner = state switch
            {
                "matching" or "pending-only" => current,
                "equal-but-distinct" => new PluginOwnerBoundary(1),
                "none" or "owner-only" => null,
                _ => throw new ArgumentOutOfRangeException(nameof(state)),
            };
            UpdateComposerButtons();
        }
    }
}
