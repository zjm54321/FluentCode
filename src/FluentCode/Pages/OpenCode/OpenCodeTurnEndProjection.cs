using System.Collections.Generic;
using FluentCode.Services.OpenCode;

namespace FluentCode.Pages.OpenCode;

/// <summary>Presentation-only turn ends; raw history and completion watermarks remain untouched.</summary>
internal static class OpenCodeTurnEndProjection
{
    // Keep the first marker's slot/key. Only a later failure or interruption replaces its caption data.
    // Visibility comes from logical timeline entries, not viewport realization or measured heights.
    internal static IReadOnlyDictionary<int, OpenCodeMessage> Collapse(
        IEnumerable<(OpenCodeMessage? Message, bool Visible)> items)
    {
        var dividers = new Dictionary<int, OpenCodeMessage>();
        var first = -1;
        var index = 0;
        foreach (var (message, visible) in items)
        {
            if (message?.Type == OpenCodeMessageTypes.Idle)
            {
                if (first < 0)
                {
                    first = index;
                    dividers.Add(first, message);
                }
                else if (message.Outcome is OpenCodeSessionOutcomes.Failed or OpenCodeSessionOutcomes.Interrupted)
                {
                    dividers[first] = message;
                }
            }
            else if (visible)
            {
                first = -1;
            }
            index++;
        }
        return dividers;
    }
}
