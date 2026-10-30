using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
namespace AIFrontend.Models
{
    public enum OpenCodeConnectionKind { Wsl, Remote }
    public static class OpenCodeSettings { public static bool IsValidWslDistributionName(string? name) => !string.IsNullOrWhiteSpace(name); }
}
namespace AIFrontend.Services.OpenCode
{
    // Inert metadata boundary. Fixture never creates a client or reads a user session.
}
namespace AIFrontend.Pages.OpenCode
{
    internal sealed record OpenCodeAgentPickerRow(string Id, string Name, string? Color, bool Selectable);
    internal static class OpenCodeAgentIcons { internal static string DisplayName(string id) => id; internal static string Glyph(string id) => "\uE77B"; }
    internal static class OpenCodeAgentPresentation { internal static Windows.UI.Color? Color(string? value) => null; }
    internal static class OpenCodeModelPresentation
    {
        internal static string VariantLabel(string? value) => value ?? "默认";
        internal static System.Collections.Generic.IEnumerable<OpenCodeModelPickerRow> Order(System.Collections.Generic.IReadOnlyList<OpenCodeModelPickerRow> rows, string? source) => rows;
    }
    // Favorite persistence is outside this native click fixture; no user settings or RPCs.
    internal sealed class OpenCodeModelFavoriteStore(Services.OpenCode.OpenCodeClient? client, string? directory, System.Threading.CancellationToken ct)
    {
        internal OpenCodeModelPickerFavorites Favorites { get; } = new();
        internal bool Available => false;
        internal string? Status => null;
        internal System.Threading.Tasks.Task LoadAsync() { _ = (client, directory, ct); return System.Threading.Tasks.Task.CompletedTask; }
        internal System.Threading.Tasks.Task SaveAsync(OpenCodeModelPickerRow row, bool value) => System.Threading.Tasks.Task.CompletedTask;
    }
    internal static class OpenCodeModelLogo { internal static FrameworkElement Label(string? id, string name) => new Microsoft.UI.Xaml.Controls.TextBlock { Text = name }; }
    internal static class OpenCodeInteractionUi
    {
        internal const string CaptionSubtleStyleKey = "CaptionTextBlockStyle", SubtitleStyleKey = "SubtitleTextBlockStyle", CodeStyleKey = "BodyTextBlockStyle", BodyStrongStyleKey = "BodyStrongTextBlockStyle";
        internal static Style? TryStyle(string key) => Application.Current.Resources.TryGetValue(key, out var value) ? value as Style : null;
        internal static Microsoft.UI.Xaml.Controls.TextBlock StyledText(string text, string key) => new() { Text = text, Style = TryStyle(key) };
        internal static Microsoft.UI.Xaml.Controls.TextBlock Caption(string text, double opacity) => new() { Text = text, Opacity = opacity };
    }
    internal static class OpenCodeThemedXaml
    {
        internal static Style BrushStyle(string type, params (string Property, string Resource)[] values)
        {
            var setters = string.Join("", System.Array.ConvertAll(values, value => $"<Setter Property=\"{value.Property}\" Value=\"{{ThemeResource {value.Resource}}}\"/>"));
            return (Style)XamlReader.Load($"<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"{type}\">{setters}</Style>");
        }
    }
    internal static class OpenCodeInsightsTheme
    {
        internal const string Success = "SystemFillColorSuccessBrush";
        internal static Style InsetBox() => new() { TargetType = typeof(Microsoft.UI.Xaml.Controls.Border) };
        internal static Style Dot(string resource) => OpenCodeThemedXaml.BrushStyle("Ellipse", ("Fill", resource));
    }
}
