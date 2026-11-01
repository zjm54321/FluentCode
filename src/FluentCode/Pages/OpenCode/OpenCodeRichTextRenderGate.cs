using System;

namespace FluentCode.Pages.OpenCode;

/// <summary>Skips duplicate rich-text renders while retaining lifecycle and image-context fences.</summary>
internal sealed class OpenCodeRichTextRenderGate
{
    private string? _source;
    private object? _style;
    private object? _imageContext;
    private bool _hasRendered;

    /// <summary>
    /// Records a render when the source/style pair or its binding generation changed.
    /// A canceled generation always renders so unload/reload can create fresh image leases.
    /// </summary>
    internal bool ShouldRender(string? source, object style, object? imageContext, bool generationCanceled)
    {
        if (!generationCanceled && _hasRendered
            && string.Equals(_source, source, StringComparison.Ordinal)
            && Equals(_style, style)
            && ReferenceEquals(_imageContext, imageContext))
        {
            return false;
        }

        _source = source;
        _style = style;
        _imageContext = imageContext;
        _hasRendered = true;
        return true;
    }
}
