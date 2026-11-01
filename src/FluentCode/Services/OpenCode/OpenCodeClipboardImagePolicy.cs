using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace FluentCode.Services.OpenCode;

internal enum OpenCodeClipboardImageKind { DefaultText, Bitmap, ImageFiles }

internal static class OpenCodeClipboardImagePolicy
{
    // A bitmap wins over file/text representations of the same clipboard image: attach only once.
    internal static OpenCodeClipboardImageKind Classify(bool hasBitmap, IReadOnlyList<string> fileNames)
        => hasBitmap ? OpenCodeClipboardImageKind.Bitmap
            : fileNames.Any(IsImageFile) ? OpenCodeClipboardImageKind.ImageFiles
            : OpenCodeClipboardImageKind.DefaultText;

    internal static bool IsImageFile(string name) => Path.GetExtension(name).ToLowerInvariant()
        is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";

    internal static string PngName(DateTime now)
        => "粘贴的图片 " + now.ToString("HHmmss", CultureInfo.InvariantCulture) + ".png";
}
