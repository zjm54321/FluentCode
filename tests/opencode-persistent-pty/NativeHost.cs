// Only the unrelated application style lookup is stubbed. Controls, native terminal,
// properties, dialogs and event APIs compile against the cached real WinUI refs.
using Microsoft.UI.Xaml;
namespace AIFrontend.Pages.OpenCode;
internal static class OpenCodeInteractionUi
{
    internal const string CaptionSubtleStyleKey = "CaptionSubtle";
    internal const string CodeStyleKey = "Code";
    internal const string BodyStrongStyleKey = "BodyStrong";
    internal static Style? TryStyle(string key) { _ = key; return null; }
}
internal static class OpenCodeInsightsTheme
{
    internal static Style DockCard() => new() { TargetType = typeof(Microsoft.UI.Xaml.Controls.Border) };
}
