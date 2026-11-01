namespace FluentCode.Models;

public sealed record TerminalAppearanceSettings
{
    public string FontFamily { get; init; } = "Cascadia Mono";
    public int FontSize { get; init; } = 12;
    public string ColorScheme { get; init; } = "Default";
    public string CursorShape { get; init; } = "Bar";
    public bool CursorBlink { get; init; } = true;

    public static readonly string[] ColorSchemes = ["Default", "Campbell", "Campbell Powershell", "One Half Dark", "One Half Light", "Solarized Dark", "Solarized Light", "Tango Dark", "Tango Light", "Vintage"];
    public static readonly string[] CursorShapes = ["Bar", "Block", "Underline"];

    public string? ValidationError()
    {
        if (string.IsNullOrWhiteSpace(FontFamily) || FontFamily.Length > 200) return "终端字体无效";
        if (FontSize is < 6 or > 72) return "终端字号须为 6–72";
        if (System.Array.IndexOf(ColorSchemes, ColorScheme) < 0) return "终端配色无效";
        if (System.Array.IndexOf(CursorShapes, CursorShape) < 0) return "终端光标无效";
        return null;
    }
}
