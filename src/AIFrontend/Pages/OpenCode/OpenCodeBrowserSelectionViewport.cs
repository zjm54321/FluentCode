using System;
using System.Text.Json.Nodes;
using AIFrontend.Services.OpenCode.Browser;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Immutable capture viewport; region snapshots never silently become current-viewport coordinates.</summary>
internal readonly record struct OpenCodeBrowserSelectionViewport(double PageX, double PageY, double Width, double Height)
{
    internal static OpenCodeBrowserSelectionViewport Read(JsonNode viewport) => new(
        viewport["pageX"]!.GetValue<double>(), viewport["pageY"]!.GetValue<double>(),
        viewport["clientWidth"]!.GetValue<double>(), viewport["clientHeight"]!.GetValue<double>());
    internal bool SameSize(OpenCodeBrowserSelectionViewport other) => Math.Abs(Width - other.Width) < .5 && Math.Abs(Height - other.Height) < .5;
    internal bool SameView(OpenCodeBrowserSelectionViewport other) => SameSize(other) && Math.Abs(PageX - other.PageX) < .5 && Math.Abs(PageY - other.PageY) < .5;
    internal BrowserRect DocumentBox(BrowserRect box) => new(box.X + PageX, box.Y + PageY, box.Width, box.Height);
    internal BrowserRect AtViewport(BrowserRect box, OpenCodeBrowserSelectionViewport current) => new(box.X + PageX - current.PageX, box.Y + PageY - current.PageY, box.Width, box.Height);
    internal BrowserSelectionAttachment Describe(BrowserSelectionAttachment attachment, BrowserRect box)
    {
        var details = JsonNode.Parse(attachment.Details)!.AsObject(); var document = DocumentBox(box);
        details["coordinateSpace"] = "viewport-at-capture";
        details["viewportAtCapture"] = new JsonObject { ["pageX"] = PageX, ["pageY"] = PageY, ["width"] = Width, ["height"] = Height };
        details["documentBox"] = new JsonObject { ["x"] = document.X, ["y"] = document.Y, ["width"] = document.Width, ["height"] = document.Height };
        var text = details.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        return attachment with { Details = text, Context = attachment.Context.Replace(attachment.Details, text, StringComparison.Ordinal) };
    }
}
