// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Text;

namespace FluentCode.Services.NativeUi;

/// <summary>
/// The rules of the Pressable tile that need no WinUI type, so the native-ui tests run them: how a hover or press
/// tint lands on the tile's own fill, which input belongs to the tile rather than to a control inside it, and the
/// name a screen reader reads for the tile.
/// </summary>
internal static class NativeUiPressable
{
    /// <summary>Name of a tile that shows no text of its own (only an icon or an unlabelled picture).</summary>
    internal const string Unnamed = "可点按项";
    internal const int NameLimit = 160;

    // What each component shows as text, in reading order. A component that is not listed adds nothing: controls
    // inside the tile keep their own names, and code, formulas and Markdown source are not read out as a name.
    private static readonly Dictionary<string, string[]> Shown = new(StringComparer.Ordinal)
    {
        ["Title"] = new[] { "text" }, ["Text"] = new[] { "text" }, ["Caption"] = new[] { "text" }, ["Badge"] = new[] { "text" },
        ["Label"] = new[] { "text" }, ["Quote"] = new[] { "text", "cite" }, ["Image"] = new[] { "alt" },
        ["ListItem"] = new[] { "title", "description", "children" }, ["List"] = new[] { "items" },
        ["Col"] = new[] { "children" }, ["Row"] = new[] { "children" }, ["Card"] = new[] { "children" },
        ["Box"] = new[] { "children" }, ["Grid"] = new[] { "children" },
    };

    /// <summary>
    /// The colour seen when <paramref name="tint"/> lies on top of <paramref name="rest"/> (source-over, straight
    /// alpha). The platform's hover and press tints are a few percent of black or white, so the tile keeps its own
    /// colour and only moves a step. A tint that hides what is under it (high contrast gives an opaque system
    /// colour) is not applied: the tile's text is coloured for the tile's own fill.
    /// </summary>
    internal static (byte A, byte R, byte G, byte B) Over((byte A, byte R, byte G, byte B) rest, (byte A, byte R, byte G, byte B) tint)
    {
        if (tint.A is 0 or 255) return rest;
        double top = tint.A / 255d, under = rest.A / 255d * (1 - top), alpha = top + under;
        byte Mix(byte upper, byte lower) => (byte)Math.Round((upper * top + lower * under) / alpha, MidpointRounding.AwayFromZero);
        return ((byte)Math.Round(alpha * 255, MidpointRounding.AwayFromZero), Mix(tint.R, rest.R), Mix(tint.G, rest.G), Mix(tint.B, rest.B));
    }

    /// <summary>
    /// What a tile's derived colours were last worked out from. Reading a theme colour costs a XAML parse, and a
    /// card applies every tile again on each update, so a tile asks here first and does the work only when an
    /// input changed. One per tile; it holds values only.
    /// </summary>
    internal sealed class Memo
    {
        private (byte A, byte R, byte G, byte B)? _fill;
        private string? _tintLook, _textLook;

        /// <summary>
        /// Whether the hover and press colours have to be worked out again: the fill, the theme the tile is drawn
        /// in or the contrast scheme differs from the last time. The answer is remembered.
        /// </summary>
        internal bool NeedsTints((byte A, byte R, byte G, byte B) fill, string theme, string contrast)
        {
            var look = theme + "|" + contrast;
            if (_fill == fill && _tintLook == look) return false;
            _fill = fill; _tintLook = look; return true;
        }

        /// <summary>
        /// Whether text colour and font have to be taken from the surroundings again: the page theme, the tile's
        /// own theme or the contrast scheme changed, or <see cref="Moved"/> was called since.
        /// </summary>
        internal bool NeedsText(string pageTheme, string ownTheme, string contrast)
        {
            var look = pageTheme + "|" + ownTheme + "|" + contrast;
            if (_textLook == look) return false;
            _textLook = look; return true;
        }

        /// <summary>The tile entered a tree: what is above it may be different, the colours are not.</summary>
        internal void Moved() => _textLook = null;
    }

    /// <summary>A key belongs to the tile only when the tile itself has the focus, not a control inside it.</summary>
    internal static bool OwnKey(object? originalSource, object tile) => ReferenceEquals(originalSource, tile);

    /// <summary>
    /// Whether a press that started at <paramref name="source"/> started inside a control nested in the tile; such
    /// a press is the nested control's and does not press the tile. The walk stops at the tile.
    /// </summary>
    internal static bool InsideNested<T>(T? source, T tile, Func<T, T?> parent, Func<T, bool> interactive) where T : class
    {
        for (var node = source; node is not null && !ReferenceEquals(node, tile); node = parent(node))
            if (interactive(node)) return true;
        return false;
    }

    /// <summary>
    /// The tile's name for a screen reader: the text it shows, in reading order, on one line. <paramref name="inline"/>
    /// turns the inline Markdown of a Text component into what is displayed.
    /// </summary>
    internal static string Name(NativeUiElement tile, Func<string, string> inline)
    {
        var name = new StringBuilder();
        void Say(string text)
        {
            foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (name.Length > 0) name.Append(' ');
                name.Append(word);
            }
        }
        void Read(object? value, bool marked)
        {
            if (value is null || name.Length > NameLimit) return;
            if (value is NativeUiElement element)
            {
                if (!Shown.TryGetValue(element.TypeName, out var props)) return;
                foreach (var prop in props) Read(element.Props.GetValueOrDefault(prop), element.TypeName == "Text" && prop == "text");
            }
            else if (value is IReadOnlyList<object?> list) foreach (var item in list) Read(item, marked);
            else if (value is string or double or int) Say(marked ? inline(NativeUiValues.Text(value)) : NativeUiValues.Text(value));
        }
        Read(tile.Props.GetValueOrDefault("children"), false);
        if (name.Length == 0) return Unnamed;
        if (name.Length <= NameLimit) return name.ToString();
        // Do not cut a character that takes two code units in half.
        return name.ToString(0, char.IsHighSurrogate(name[NameLimit - 1]) ? NameLimit - 1 : NameLimit) + "…";
    }
}
