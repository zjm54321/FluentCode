// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FluentCode.Pages.OpenCode;

// WinUI-free colour, length, flex and icon rules shared by the renderer and the
// native-ui test runner (tests/native-ui/run.ps1 compiles this file directly).
internal static class NativeUiStyleMath
{
    // #rgb, #rrggbb or #aarrggbb; the catalog's only raw colour form (no CSS functions are parsed).
    internal static bool TryHex(string token, out byte a, out byte r, out byte g, out byte b)
    {
        a = r = g = b = 0;
        if (!token.StartsWith('#') || token.Length is not (4 or 7 or 9)) return false;
        var digits = token.Length == 4 ? string.Concat(token.Skip(1).Select(c => new string(c, 2))) : token[1..];
        if (!uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)) return false;
        (a, r, g, b) = (digits.Length == 8 ? (byte)(hex >> 24) : (byte)255, (byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);
        return true;
    }
    // OIU contrastOn: relative luminance above 0.36 takes dark text, otherwise light text.
    // null when the background is a theme token or mostly transparent (text follows the app theme).
    internal static bool? DarkTextOn(string background)
    {
        if (!TryHex(background, out var a, out var r, out var g, out var b) || a < 128) return null;
        static double Linear(byte channel) { var v = channel / 255d; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b) > 0.36;
    }
    // "50%" -> 0.5 (clamped to 0..1); anything else is not a percentage.
    internal static double? Percent(string text)
        => text.EndsWith('%') && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? Math.Clamp(value, 0, 100) / 100 : null;
    // CSS `flex: N 1 0%`: with basis 0 the free space is split in proportion to the flex weights.
    internal static double[] FlexShares(double free, IReadOnlyList<double> flex)
    {
        var total = flex.Where(weight => weight > 0).Sum();
        return flex.Select(weight => weight > 0 && total > 0 ? Math.Max(0, free) * weight / total : 0).ToArray();
    }
    internal static string? Glyph(string name) => name.Length > 0 && Glyphs.TryGetValue(name.Trim().ToLowerInvariant(), out var glyph) ? glyph : null;
    // Lucide names (the OIU icon set) mapped to Segoe Fluent Icons; unknown names render nothing, as upstream.
    private static readonly Dictionary<string, string> Glyphs = new(StringComparer.Ordinal)
    {
        ["plus"] = "\uE710", ["minus"] = "\uE738", ["x"] = "\uE711", ["close"] = "\uE711", ["check"] = "\uE73E", ["check-circle"] = "\uE930", ["circle-check"] = "\uE930",
        ["send"] = "\uE724", ["calculator"] = "\uE8EF", ["arrow-up-right"] = "\uE8A7", ["external-link"] = "\uE8A7", ["clock"] = "\uE823", ["timer"] = "\uE916",
        ["rotate-ccw"] = "\uE7A7", ["undo"] = "\uE7A7", ["rotate-cw"] = "\uE7A6", ["redo"] = "\uE7A6", ["refresh-cw"] = "\uE72C", ["refresh"] = "\uE72C",
        ["copy"] = "\uE8C8", ["trash"] = "\uE74D", ["trash-2"] = "\uE74D", ["download"] = "\uE896", ["upload"] = "\uE898", ["search"] = "\uE721",
        ["settings"] = "\uE713", ["info"] = "\uE946", ["alert-triangle"] = "\uE7BA", ["triangle-alert"] = "\uE7BA", ["alert-circle"] = "\uEA39", ["circle-alert"] = "\uEA39",
        ["help-circle"] = "\uE9CE", ["chevron-right"] = "\uE76C", ["chevron-left"] = "\uE76B", ["chevron-down"] = "\uE70D", ["chevron-up"] = "\uE70E",
        ["arrow-right"] = "\uE72A", ["arrow-left"] = "\uE72B", ["arrow-up"] = "\uE74A", ["arrow-down"] = "\uE74B", ["star"] = "\uE734", ["heart"] = "\uEB51",
        ["calendar"] = "\uE787", ["edit"] = "\uE70F", ["pencil"] = "\uE70F", ["save"] = "\uE74E", ["share"] = "\uE72D", ["link"] = "\uE71B", ["mail"] = "\uE715",
        ["user"] = "\uE77B", ["users"] = "\uE716", ["home"] = "\uE80F", ["play"] = "\uE768", ["pause"] = "\uE769", ["square"] = "\uE71A", ["stop"] = "\uE71A",
        ["filter"] = "\uE71C", ["sparkles"] = "\uE9F5", ["zap"] = "\uE945", ["lock"] = "\uE72E", ["unlock"] = "\uE785", ["eye"] = "\uE7B3", ["bell"] = "\uEA8F",
        ["file"] = "\uE8A5", ["file-text"] = "\uE8A5", ["folder"] = "\uE8B7", ["image"] = "\uEB9F", ["map-pin"] = "\uE707", ["globe"] = "\uE774",
        ["code"] = "\uE943", ["terminal"] = "\uE756", ["git-branch"] = "\uE8AD", ["bar-chart"] = "\uE9D2", ["chart-bar"] = "\uE9D2", ["bar-chart-2"] = "\uE9D2",
        ["dollar-sign"] = "\uE8C7", ["shopping-cart"] = "\uE7BF", ["thumbs-up"] = "\uE8E1", ["thumbs-down"] = "\uE8E0", ["more-horizontal"] = "\uE712", ["ellipsis"] = "\uE712",
        ["list"] = "\uEA37", ["grid"] = "\uE80A", ["layout-grid"] = "\uE80A", ["shuffle"] = "\uE8B1", ["volume-2"] = "\uE767", ["mic"] = "\uE720", ["camera"] = "\uE722",
        // Upstream gallery icons (Lucide names) mapped to the nearest Segoe Fluent glyph; "bike" has no Fluent counterpart.
        ["bookmark"] = "\uE8A4", ["lightbulb"] = "\uEA80", ["leaf"] = "\uE8BE", ["sprout"] = "\uE8BE", ["leafy-green"] = "\uED56", ["bean"] = "\uED56",
        ["cpu"] = "\uEC7A", ["ship"] = "\uE7E3", ["mouse-pointer-click"] = "\uE7C9", ["hand"] = "\uE7C9", ["navigation"] = "\uE816", ["shield-check"] = "\uEA18",
        ["sun"] = "\uE706", ["user-plus"] = "\uE8FA", ["calendar-plus"] = "\uE787", ["circle"] = "\uEA3A", ["dices"] = "\uE7FC", ["hourglass"] = "\uE916",
        ["newspaper"] = "\uE7BC", ["at-sign"] = "\uE715",
    };
}
