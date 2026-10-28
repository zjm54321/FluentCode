using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode.Browser;

public sealed partial class OpenCodeBrowserSessionBackend
{
    // Selection and protocol actions use one document registry; neither can invent a second ref lifetime.
    private static readonly ConditionalWeakTable<OpenCodeBrowserPage, PageContext> PickerPages = new();
    public static async Task<(OpenCodeBrowserFrames Frames, OpenCodeBrowserFrameGeometry Geometry)> PickerContextAsync(OpenCodeBrowserPage page, CancellationToken ct = default)
    {
        if (!PickerPages.TryGetValue(page, out var context)) throw new OpenCodeBrowserException("not_ready", "此页面的浏览器选择引擎尚未就绪");
        await context.InitializeAsync(); await context.RefreshFramesAsync(ct);
        return (context.Frames, context.Geometry);
    }
    public static void CheckPickedRef(OpenCodeBrowserPage page, string reference)
    {
        if (!PickerPages.TryGetValue(page, out var context)) throw new OpenCodeBrowserException("stale_ref", "此页面已关闭");
        context.Resolve(reference);
    }
    public static string? PickerFrame(OpenCodeBrowserPage page, string? session, int contextId)
        => PickerPages.TryGetValue(page, out var context) ? context.Frames.ContextFrame(session, contextId) : null;
}
