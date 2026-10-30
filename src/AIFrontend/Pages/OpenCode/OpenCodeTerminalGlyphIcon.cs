using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Keeps existing FontIcon styling/glyph models while rendering the terminal marker as the shared Fluent path.</summary>
internal sealed class OpenCodeTerminalGlyphIcon : Grid
{
    private readonly FontIcon _font;
    private PathIcon? _terminal;

    internal OpenCodeTerminalGlyphIcon(FontIcon font)
    {
        _font = font;
        Width = Height = font.FontSize;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Center;
        Children.Add(font);
        SetGlyph(font.Glyph);
    }

    internal void SetGlyph(string glyph)
    {
        _font.Glyph = glyph;
        // The legacy string is a presentation-model marker, not a font glyph we draw.
        var terminal = glyph == "\uE756";
        if (terminal && _terminal is null)
        {
            _terminal = OpenCodeQuickAccessStyle.TerminalIcon(_font.FontSize);
            _terminal.SetBinding(IconElement.ForegroundProperty, new Binding
            { Source = _font, Path = new PropertyPath(nameof(FontIcon.Foreground)), Mode = BindingMode.OneWay });
            Children.Add(_terminal);
        }
        _font.Visibility = terminal ? Visibility.Collapsed : Visibility.Visible;
        if (_terminal is not null) _terminal.Visibility = terminal ? Visibility.Visible : Visibility.Collapsed;
    }

    internal static OpenCodeTerminalGlyphIcon Create(string glyph, double size)
        => new(new FontIcon { Glyph = glyph, FontSize = size, VerticalAlignment = VerticalAlignment.Center });
}
