using System.Globalization;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Shared by the selected-session card and its narrow/child-session flyout.</summary>
internal static class OpenCodeContextHeader
{
    internal const int TitleRow = 0;
    internal const int ContextRow = 1;
    internal const string TurnGlyph = "\uE72C";
    internal static string TurnNumber(int? turns) => turns?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    internal static string TurnTooltip(int? turns) => turns is null ? string.Empty : "第 " + TurnNumber(turns) + " 轮";
}
