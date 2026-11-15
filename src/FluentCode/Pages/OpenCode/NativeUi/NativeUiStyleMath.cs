// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FluentCode.Pages.OpenCode;

// WinUI-free colour, length, flex and icon rules, and the flashcard's turn (NativeUiFlipState and
// NativeUiFlipDriver, below), shared by the renderer and the native-ui test runner (tests/native-ui/run.ps1
// compiles this file directly).
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
    [Flags] internal enum InlineStyle { None = 0, Bold = 1, Italic = 2, Strike = 4, Code = 8 }
    internal sealed record InlineSpan(string Text, InlineStyle Style);
    // OIU markdown.tsx `inline`: `code`, **bold**/__bold__, ~~strike~~, *italic*/_italic_ and [label](url).
    // Links keep their label only; external opening stays with Link/@OpenUrl and its confirmation.
    private static readonly System.Text.RegularExpressions.Regex InlinePattern = new(
        @"(`[^`\n]+`)|(\*\*[^*\n]+?\*\*|__[^_\n]+?__)|(~~[^~\n]+?~~)|(\*[^*\s][^*\n]*?\*|_[^_\s][^_\n]*?_)|(\[[^\]\n]+\]\([^)\s]+\))",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    internal static IReadOnlyList<InlineSpan> Inline(string text, InlineStyle style = InlineStyle.None)
    {
        var spans = new List<InlineSpan>(); var rest = text;
        while (rest.Length > 0)
        {
            var match = InlinePattern.Match(rest);
            if (!match.Success) { spans.Add(new(rest, style)); break; }
            if (match.Index > 0) spans.Add(new(rest[..match.Index], style));
            var token = match.Value;
            if (match.Groups[1].Success) spans.Add(new(token[1..^1], style | InlineStyle.Code));
            else if (match.Groups[2].Success) spans.AddRange(Inline(token[2..^2], style | InlineStyle.Bold));
            else if (match.Groups[3].Success) spans.AddRange(Inline(token[2..^2], style | InlineStyle.Strike));
            else if (match.Groups[4].Success) spans.AddRange(Inline(token[1..^1], style | InlineStyle.Italic));
            else spans.AddRange(Inline(token[1..token.IndexOf("](", StringComparison.Ordinal)], style));
            rest = rest[(match.Index + match.Length)..];
        }
        return spans;
    }
    // CSS border-radius scales down when it exceeds the box (a "full" radius becomes a pill); before layout keep the request.
    internal static double FitRadius(double radius, double width, double height)
        => width > 0 && height > 0 ? Math.Min(Math.Max(0, radius), Math.Min(width, height) / 2) : Math.Max(0, radius);
    // C06: the theme-resource keys DevWinUI's IssueLabel paints its categorical severity states from. The library
    // defines twelve — four tones (Gray, Blue, Purple, Pink) by three roles (fill, ring, text) — and the shared
    // badge adapter uses them for its Neutral, Attention, Purple and Pink tones, so a badge that carries an icon
    // reads the same twelve and matches a text-only badge. Names verified in DevWinUI 10.9.1 Themes/Generic.xaml
    // (each defined in its Default, Light and HighContrast dictionaries). The three status tones are absent on
    // purpose: both badge forms already paint them from the same SystemFillColor* keys.
    internal static (string Background, string Border, string Foreground)? BadgeLibraryTone(string tone) => tone switch
    {
        "muted" => ("IssueLabelGrayBackgroundBrush", "IssueLabelGrayBorderBrush", "IssueLabelGrayForegroundBrush"),
        "info" => ("IssueLabelBlueBackgroundBrush", "IssueLabelBlueBorderBrush", "IssueLabelBlueForegroundBrush"),
        "purple" => ("IssueLabelPurpleBackgroundBrush", "IssueLabelPurpleBorderBrush", "IssueLabelPurpleForegroundBrush"),
        "pink" => ("IssueLabelPinkBackgroundBrush", "IssueLabelPinkBorderBrush", "IssueLabelPinkForegroundBrush"),
        _ => null,
    };
    // A badge is painted from one palette as a whole: the library's box, glyph and label styles are used only when
    // all three resolved; if any is missing the app's three are used together, so a badge is never half and half.
    internal static (T? Box, T? Glyph, T? Label) PickBadgePalette<T>((T? Box, T? Glyph, T? Label) app, (T? Box, T? Glyph, T? Label) library) where T : class
        => library.Box is not null && library.Glyph is not null && library.Label is not null ? library : app;
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

/// <summary>
/// C12: which face a flashcard shows and where its turn is. It lives in this file because the native-ui runner
/// compiles this file, so the order of a turn is executed by tests instead of being read from source.
/// A turn has two legs: away to edge-on with the old face still showing, then back with the new one. The face
/// changes between the legs and never during one.
/// </summary>
internal sealed class NativeUiFlipState
{
    internal enum Leg { Rest, Away, Back }
    /// <summary>What a report asks of the caller, besides painting <see cref="Shown"/>.</summary>
    internal enum Step { Paint, TurnAway, TurnBack, Settle }
    private bool? _shown; // Null until the first report: a card's first face is painted without a turn.
    /// <summary>The face on screen. False is the front, which is what a fresh card shows.</summary>
    internal bool Shown => _shown is true;
    /// <summary>The face the model asked for last.</summary>
    internal bool Wanted { get; private set; }
    internal Leg Phase { get; private set; }
    /// <summary>
    /// Names the leg in flight. Every start, reversal, midpoint, landing and halt takes a new one, so a completion
    /// that arrives late matches nothing.
    /// </summary>
    internal int Generation { get; private set; }

    /// <summary>
    /// The model reports the face it wants. An unchanged report only repaints: mid-turn that is still the old face,
    /// which keeps it alive until the midpoint. A changed one turns the card, or settles on the new face at once when
    /// <paramref name="animate"/> is false. A report that takes back a turn still on its way out turns the card back
    /// with the face it never stopped showing.
    /// </summary>
    internal Step Request(bool wanted, bool animate)
    {
        if (_shown is null) { _shown = Wanted = wanted; return Step.Paint; }
        if (wanted == Wanted) return Step.Paint;
        Wanted = wanted; Generation++;
        if (!animate) { _shown = wanted; Phase = Leg.Rest; return Step.Settle; }
        Phase = wanted == _shown ? Leg.Back : Leg.Away;
        return Phase == Leg.Back ? Step.TurnBack : Step.TurnAway;
    }

    /// <summary>The away leg reached edge-on: the wanted face becomes the shown one. False when that leg was superseded.</summary>
    internal bool Midpoint(int generation)
    {
        if (generation != Generation || Phase != Leg.Away) return false;
        _shown = Wanted; Phase = Leg.Back; Generation++;
        return true;
    }

    /// <summary>The back leg reached face-on. False when that leg was superseded.</summary>
    internal bool Landed(int generation)
    {
        if (generation != Generation || Phase != Leg.Back) return false;
        Phase = Leg.Rest; Generation++;
        return true;
    }

    /// <summary>
    /// Ends a turn where it is: the wanted face shows and nothing is in flight. True when that changed the face on
    /// screen, which the caller then has to paint.
    /// </summary>
    internal bool Halt()
    {
        if (Phase == Leg.Rest) return false;
        var changed = _shown != Wanted;
        _shown = Wanted; Phase = Leg.Rest; Generation++;
        return changed;
    }
}

/// <summary>
/// C12: what a flashcard's turn needs from the UI. The effects adapter implements it with the animation library;
/// the native-ui tests implement it with a script, so the order below runs in tests.
/// </summary>
internal interface INativeUiFlipSurface
{
    /// <summary>Whether a turn can be animated now: motion is allowed and the card is on screen with a size.</summary>
    bool CanTurn { get; }
    /// <summary>
    /// Starts one leg from wherever the card is: to edge-on when <paramref name="away"/>, to face-on otherwise.
    /// <paramref name="landed"/> is called when the leg ends: later, never from inside this call, at most once
    /// and possibly never. False when no leg started.
    /// </summary>
    bool Turn(bool away, Action landed);
    /// <summary>Stops any leg and leaves the card face-on.</summary>
    void Rest();
    /// <summary>Paints one face. Only ever called inside a render pass.</summary>
    void Paint(bool back);
    /// <summary>Runs a render pass, which reports again and so paints the face that shows now.</summary>
    void Repaint();
}

/// <summary>
/// C12: the end of one leg. An animation library keeps its completion handler for as long as its own batch lives,
/// which this code cannot bound, so that handler is given this object and nothing else. Once the leg has ended or
/// was dropped it holds nothing, and whatever still holds it reaches no card.
/// </summary>
internal sealed class NativeUiFlipLeg(int generation, Action<int> end)
{
    private Action<int>? _end = end;
    /// <summary>False once the leg has ended or was dropped: nothing is reachable through it any more.</summary>
    internal bool Live => _end is not null;
    internal void Drop() => _end = null;
    internal void Land() { var call = _end; _end = null; call?.Invoke(generation); }
}

/// <summary>
/// C12: runs a flashcard's turn on a surface. A report arrives inside a render pass and paints there. A leg's end
/// arrives outside one, so the change of face at edge-on asks for a pass instead of painting: only a pass prunes
/// the elements of the face that left.
/// </summary>
internal sealed class NativeUiFlipDriver(INativeUiFlipSurface owner)
{
    private readonly NativeUiFlipState _state = new();
    private INativeUiFlipSurface? _surface = owner; // Null once disposed: nothing that arrives later does anything.
    private NativeUiFlipLeg? _leg;
    private bool _starting; // Inside Turn. A leg that reports its end from there did not run.

    internal bool Shown => _state.Shown;
    internal NativeUiFlipState.Leg Phase => _state.Phase;

    /// <summary>The model reports the face it wants, inside a render pass. Paints the face that shows.</summary>
    internal void Request(bool wanted)
    {
        if (_surface is not { } surface) return;
        switch (_state.Request(wanted, surface.CanTurn))
        {
            case NativeUiFlipState.Step.TurnAway: Start(surface, away: true); break;
            case NativeUiFlipState.Step.TurnBack: Start(surface, away: false); break;
            case NativeUiFlipState.Step.Settle: Stop(surface); break;
        }
        surface.Paint(_state.Shown);
    }

    /// <summary>
    /// Ends a turn where it is, with the wanted face showing: motion was turned off, the card left the screen, or
    /// a leg's end never arrived. Called outside a render pass. Does nothing to a card that is not turning.
    /// </summary>
    internal void Halt()
    {
        if (_surface is not { } surface || _state.Phase == NativeUiFlipState.Leg.Rest) return;
        var changed = _state.Halt();
        Stop(surface);
        if (changed) surface.Repaint();
    }

    /// <summary>The card is gone: drops the leg in flight, so its end reaches nothing, and rests the surface.</summary>
    internal void Dispose()
    {
        if (_surface is not { } surface) return;
        _surface = null;
        Stop(surface);
    }

    /// <summary>
    /// A leg that cannot start, or that reports its end before it was started, ends the turn on the wanted face
    /// instead of leaving the card part-turned. The caller paints: a report does so itself, and after the middle
    /// of a turn the wanted face is already the one on screen.
    /// </summary>
    private void Start(INativeUiFlipSurface surface, bool away)
    {
        _leg?.Drop();
        var leg = _leg = new NativeUiFlipLeg(_state.Generation, away ? EdgeOn : FaceOn);
        bool started;
        _starting = true;
        try { started = surface.Turn(away, leg.Land); }
        finally { _starting = false; }
        if (started && leg.Live) return;
        _state.Halt(); Stop(surface);
    }

    private void Stop(INativeUiFlipSurface surface) { _leg?.Drop(); _leg = null; surface.Rest(); }

    private void EdgeOn(int generation)
    {
        if (_starting || _surface is not { } surface || !_state.Midpoint(generation)) return;
        var back = _state.Generation;
        surface.Repaint();
        if (_surface is null || _state.Generation != back) return; // That pass ended the turn, or the card.
        // Edge-on the card has no width, so it is never left here: without a back leg it rests face-on at once.
        if (surface.CanTurn) Start(surface, away: false);
        else { _state.Halt(); Stop(surface); }
    }

    private void FaceOn(int generation)
    {
        if (_starting || _surface is not { } surface || !_state.Landed(generation)) return;
        Stop(surface);
    }
}
