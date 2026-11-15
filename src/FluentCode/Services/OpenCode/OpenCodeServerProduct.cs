using System;
using System.Collections.Generic;
using System.Threading;

namespace FluentCode.Services.OpenCode;

/// <summary>Which server product the client is talking to. Unknown is its own state: never shown as "not QoL".</summary>
internal enum OpenCodeServerProductKind { Unknown, OpenCode, OpenCodeQol }

/// <summary>
/// Product identity of the connected server, for labels and guidance text only. Feature visibility stays on each
/// feature's own probe: a QoL build may lack a plugin and an upstream server may carry one added by hand.
/// </summary>
/// <remarks>
/// GET api/info reports the upstream version only (the fork's "-qol.N" suffix is added at its CLI/TUI display sites),
/// so the product is read from GET api/plugin: the fork compiles its plugins into the server, where they appear with
/// <c>source.type == "builtin"</c> and an id under <c>opencode.qol.</c>. A plugin with such an id that was installed
/// by hand (package / local) does not make the server an OpenCode QoL build.
/// </remarks>
internal static class OpenCodeServerProduct
{
    internal const string UpstreamName = "OpenCode";
    internal const string QolName = "OpenCode QoL";
    internal const string QolProjectUrl = "https://github.com/zjm54321/opencode-qol";
    internal const string QolPluginPrefix = "opencode.qol.";
    /// <summary>The plugin that registers the <c>native_ui</c> tool (kernel/plugins/native-ui in the fork).</summary>
    internal const string NativeCardsPluginId = "opencode.qol.native-ui";

    private const string ShortQolTag = "QoL";
    private static int _nativeCards; // 0 = no answer in this run, 1 = offered, 2 = not offered

    /// <summary>null (list not read, or the read failed) stays Unknown.</summary>
    internal static OpenCodeServerProductKind Identify(IReadOnlyList<OpenCodePluginInfo>? plugins)
    {
        if (plugins is null) return OpenCodeServerProductKind.Unknown;
        foreach (var plugin in plugins)
        {
            if (plugin?.Id is { } id && id.StartsWith(QolPluginPrefix, StringComparison.Ordinal)
                && string.Equals(plugin.Source?.Type, "builtin", StringComparison.Ordinal))
                return OpenCodeServerProductKind.OpenCodeQol;
        }
        return OpenCodeServerProductKind.OpenCode;
    }

    /// <summary>Full product name. Unknown keeps the plain name: the client only ever connects to OpenCode servers.</summary>
    internal static string Name(OpenCodeServerProductKind kind)
        => kind == OpenCodeServerProductKind.OpenCodeQol ? QolName : UpstreamName;

    /// <summary>Label of the version row in the connection details.</summary>
    internal static string VersionRowLabel(OpenCodeServerProductKind kind) => Name(kind) + " 版本";

    /// <summary>
    /// Text beside the OpenCode wordmark (right-panel footer): the wordmark already says "OpenCode", so only the
    /// short tag is added. A missing version stays as it is, without a product tag in front of it.
    /// </summary>
    internal static string BesideWordmark(OpenCodeServerProductKind kind, string? version)
        => string.IsNullOrWhiteSpace(version) ? string.Empty
            : kind == OpenCodeServerProductKind.OpenCodeQol ? ShortQolTag + " " + version : version;

    /// <summary>Home connection card, under the wordmark: "v2.2.5" or "QoL v2.2.5"; null when the version is unknown.</summary>
    internal static string? HomeVersion(OpenCodeServerProductKind kind, string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var text = version.StartsWith('v') ? version : "v" + version;
        return kind == OpenCodeServerProductKind.OpenCodeQol ? ShortQolTag + " " + text : text;
    }

    /// <summary>
    /// Whether the server offers native cards: the plugin that registers the tool is listed and active, whatever its
    /// source. null when the list is unknown.
    /// </summary>
    internal static bool? OffersNativeCards(IReadOnlyList<OpenCodePluginInfo>? plugins)
    {
        if (plugins is null) return null;
        foreach (var plugin in plugins)
        {
            if (string.Equals(plugin?.Id, NativeCardsPluginId, StringComparison.Ordinal)
                && string.Equals(plugin!.State?.Status, "active", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The page records each definite answer; Settings (a separate page, which may be open while the OpenCode page
    /// is not) reads the last one. An unknown answer changes nothing. Plain value: no event, nothing to hold on to.
    /// </summary>
    internal static void RecordNativeCards(bool? offered)
    {
        if (offered is { } value) Volatile.Write(ref _nativeCards, value ? 1 : 2);
    }

    internal static bool? LastKnownNativeCards => Volatile.Read(ref _nativeCards) switch { 1 => true, 2 => false, _ => null };

    /// <summary>
    /// Settings shows the native-card category only after a server said it offers the cards. Before any answer
    /// (never connected in this run) and after a "no" it is hidden; a disconnect keeps the last answer.
    /// </summary>
    internal static bool ShowNativeCardSettings(bool? lastKnown) => lastKnown == true;
}
