using System;
using System.Collections.Generic;
using System.Linq;
using FluentCode.Services.OpenCode;

namespace FluentCode.Pages.OpenCode;

internal sealed partial class OpenCodeComposerController
{
    // Atomic image+context staging: one removable chip owns both parts and the existing slot/send path.
    internal bool TryAddCapturedImages(OpenCodeComposerToken token, IReadOnlyList<OpenCodeComposerAttachmentPresentation> captures)
    {
        lock (_sync)
        {
            if (!Current(token) || Busy || HasClientAction || IsUnbound(_binding)) return false;
            var staged = captures.Select(c =>
            {
                var bytes = c.Thumbnail.ToArray();
                var name = "browser-" + Guid.NewGuid().ToString("N") + ".png";
                return new StoredFile(Guid.NewGuid(), name, "image/png", bytes,
                    OpenCodeAttachment.Prepare(new[] { new OpenCodeAttachmentDraft(name, "image/png", bytes) }))
                    { Presentation = c with { Thumbnail = bytes.ToArray() } };
            }).ToArray();
            var combined = OpenCodeAttachment.Combine(_files.Concat(staged).Select(f => f.Encoded).ToArray());
            var context = string.Join("\n\n", _files.Concat(staged).Select(f => f.Presentation?.Context).Where(s => !string.IsNullOrWhiteSpace(s)));
            OpenCodeAttachment.ValidateRequestSize(new OpenCodeAttachedPromptBody(_text + "\n\n" + context, combined));
            if (staged.Length == 0) return false;
            _origin ??= _binding;
            _files.AddRange(staged); Edited();
        }
        Publish(); return true;
    }
}
