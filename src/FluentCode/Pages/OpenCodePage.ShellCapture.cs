using System;
using System.Linq;
using System.Threading.Tasks;

namespace FluentCode.Pages;

// Opt-in shell capture (FC_SHELL_CAPTURE_TEST, isolated startup-smoke profile only): read-only helpers.
public sealed partial class OpenCodePage
{
    internal int TestSessionCount => _sessionItems.Count;

    internal bool TestIsHomeActive => _homeActive;

    /// <summary>Open the most recently updated top-level session through the normal guarded switch.</summary>
    internal async Task<bool> TestOpenRecentSessionAsync()
    {
        var id = _sessionItems.Values
            .Where(static info => info.Id is not null && info.ParentId is null)
            .OrderByDescending(static info => info.Time?.Updated ?? 0)
            .Select(static info => info.Id!)
            .FirstOrDefault();
        if (id is null) return false;
        await SwitchToSessionAsync(id, onBound: () => MirrorSessionSelection(id, userInitiated: true));
        return string.Equals(_selectedSessionId, id, StringComparison.Ordinal);
    }
}
