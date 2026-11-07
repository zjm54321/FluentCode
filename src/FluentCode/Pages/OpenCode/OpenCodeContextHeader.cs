using System.Globalization;

namespace FluentCode.Pages.OpenCode;

/// <summary>Shared by the selected-session card and its narrow/child-session flyout.</summary>
internal static class OpenCodeContextHeader
{
    internal const int TitleRow = 0;
    internal const int ContextRow = 1;
    internal const string TurnGlyph = "\uE72C";
    internal static string TurnNumber(int? turns, bool exact = true)
        => turns is null ? string.Empty : (exact ? string.Empty : "≥") + turns.Value.ToString(CultureInfo.InvariantCulture);
    internal static string TurnTooltip(int? turns, bool exact = true)
        => turns is null ? string.Empty : exact ? "第 " + TurnNumber(turns) + " 轮" : "历史尚未全部加载";
}
