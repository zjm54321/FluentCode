// Only the controller-independent host boundary is supplied here. The document, receipts, actual
// RichEditBox adapter, bitmap cache, URL parser, favicon policy and agent glyphs are linked production code.
namespace FluentCode.Services.OpenCode
{
    internal sealed record OpenCodePromptContext(string Id, bool IsSkill, int Start)
    {
        internal bool Matches(string text) => Start >= 0 && Start + Id.Length + 1 <= text.Length
            && text.AsSpan(Start, Id.Length + 1).SequenceEqual(("@" + Id).AsSpan());
    }
}

namespace FluentCode.Pages.OpenCode
{
    internal readonly record struct OpenCodeComposerToken(Guid Draft);
    internal sealed record AdapterThumbnail(byte[]? Thumbnail);
    internal sealed record AdapterAttachment(Guid Id, AdapterThumbnail? Presentation);
    internal sealed record OpenCodeComposerSnapshot(OpenCodeComposerToken Token, OpenCodeComposerDocument Document,
        OpenCodeComposerSelection Selection, IReadOnlyList<AdapterAttachment> Attachments, bool CanUndo = false, bool CanRedo = false);
}

namespace FluentCode.Services.Configuration
{
    // Settings boundary only: the linked transcript chip keeps offline, brand-colour defaults.
    internal static class AppServices { internal static AdapterSettings Settings { get; } = new(); }
    internal sealed class AdapterSettings { internal AdapterPreferences? Current => null; }
    internal sealed class AdapterPreferences { internal AdapterLinkChips? LinkChips => null; }
    internal sealed class AdapterLinkChips { internal bool OnlineMetadata => false; internal bool MonochromeIcons => false; }
}
