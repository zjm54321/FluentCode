namespace WindowsTerminalControl.WinUI;

/// <summary>Windows Terminal built-in palettes, in ANSI index order.</summary>
public sealed record TerminalAppearance(string FontFamily = "Cascadia Mono", short FontSize = 12,
    string ColorScheme = "Default", string CursorShape = "Bar", bool CursorBlink = true)
{
    internal (uint Background, uint Foreground, uint Selection, uint[] Palette) Colors(bool light)
    {
        var name = ColorScheme == "Default" ? light ? "One Half Light" : "Campbell" : ColorScheme;
        var (bg, fg, selection, colors) = name switch
        {
            "Campbell Powershell" => ("012456", "cccccc", "ffffff", "0c0c0c c50f1f 13a10e c19c00 0037da 881798 3a96dd cccccc 767676 e74856 16c60c f9f1a5 3b78ff b4009e 61d6d6 f2f2f2"),
            "One Half Dark" => ("282c34", "dcdfe4", "ffffff", "282c34 e06c75 98c379 e5c07b 61afef c678dd 56b6c2 dcdfe4 5a6374 e06c75 98c379 e5c07b 61afef c678dd 56b6c2 dcdfe4"),
            "One Half Light" => ("fafafa", "383a42", "4f525d", "383a42 e45649 50a14f c18401 0184bc a626a4 0997b3 fafafa 4f525d e06c75 98c379 e5c07b 61afef c678dd 56b6c2 ffffff"),
            "Solarized Dark" => ("002b36", "839496", "ffffff", "073642 dc322f 859900 b58900 268bd2 d33682 2aa198 eee8d5 002b36 cb4b16 586e75 657b83 839496 6c71c4 93a1a1 fdf6e3"),
            "Solarized Light" => ("fdf6e3", "657b83", "ffffff", "073642 dc322f 859900 b58900 268bd2 d33682 2aa198 eee8d5 002b36 cb4b16 586e75 657b83 839496 6c71c4 93a1a1 fdf6e3"),
            "Tango Dark" => ("000000", "d3d7cf", "ffffff", "000000 cc0000 4e9a06 c4a000 3465a4 75507b 06989a d3d7cf 555753 ef2929 8ae234 fce94f 729fcf ad7fa8 34e2e2 eeeeec"),
            "Tango Light" => ("ffffff", "555753", "ffffff", "000000 cc0000 4e9a06 c4a000 3465a4 75507b 06989a d3d7cf 555753 ef2929 8ae234 fce94f 729fcf ad7fa8 34e2e2 eeeeec"),
            "Vintage" => ("000000", "c0c0c0", "ffffff", "000000 800000 008000 808000 000080 800080 008080 c0c0c0 808080 ff0000 00ff00 ffff00 0000ff ff00ff 00ffff ffffff"),
            _ => ("0c0c0c", "cccccc", "ffffff", "0c0c0c c50f1f 13a10e c19c00 0037da 881798 3a96dd cccccc 767676 e74856 16c60c f9f1a5 3b78ff b4009e 61d6d6 f2f2f2")
        };
        return (ColorRef(bg), ColorRef(fg), ColorRef(selection), colors.Split(' ').Select(ColorRef).ToArray());
    }

    private static uint ColorRef(string hex)
    {
        var rgb = Convert.ToUInt32(hex, 16);
        return ((rgb & 0xff0000) >> 16) | (rgb & 0x00ff00) | ((rgb & 0x0000ff) << 16);
    }
}
