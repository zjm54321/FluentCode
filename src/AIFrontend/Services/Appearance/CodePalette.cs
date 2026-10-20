namespace AIFrontend.Services.Appearance;

/// <summary>Canonical Binaryify One Dark Pro colors, MIT (see ThirdPartyNotices/OneDarkPro.txt).
/// Native lexer roles are intentionally a subset, not VSCode/TextMate semantic highlighting.</summary>
public static class CodePalette
{
    public const string Background = "#282c34";
    public const string Foreground = "#abb2bf";
    public const string Border = "#3e4452";
    // VSCode #67769660 is RRGGBBAA; WinUI requires AARRGGBB.
    public const string Selection = "#60677696";
    public static string? TokenColor(string kind) => kind switch
    {
        "keyword" => "#c678dd", "string" => "#98c379", "comment" => "#5c6370",
        "number" => "#d19a66", "function" => "#61afef", "type" => "#e5c07b",
        "operator" => "#56b6c2", "variable" => "#e06c75", "error" => "#f44747",
        _ => null,
    };
}
