// Compile-only stand-ins for unrelated composition helpers; no production replacements or runtime claims.
using System;
using System.Threading.Tasks;
using AIFrontend.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace AIFrontend.Pages.Settings
{
    public sealed partial class SettingsView : UserControl
    {
        private bool _suppressUiEvents = false;
        private AppSettings? _settings = new();
        private static TextBlock CreateSubtleText(string text) => new() { Text = text };
        private static void ShowStatus(InfoBarSeverity severity, string title, string? message) { }
        private static Task<bool> SaveAsync(Func<AppSettings, AppSettings> transform, string message) => Task.FromResult(true);
    }
}
namespace AIFrontend.Services.Appearance
{
    internal static class AppFonts { internal static void Apply(FrameworkElement element, AppFontSlot slot) { } }
}
namespace AIFrontend.Pages.OpenCode
{
    internal static class OpenCodeInteractionUi { internal const string CodeStyleKey = ""; internal static Style? TryStyle(string key) => null; }
    internal static class OpenCodeToolDetailPopup { internal static FlyoutPresenter CreatePresenter(UIElement element, Thickness padding, int z) => new() { Content = element }; }
    internal static class OpenCodeToolTheme
    {
        internal static void Apply(FrameworkElement element, object style) { }
        internal static object Text(string key, string? style = null) => new();
        internal static object Icon(string key) => new();
    }
    internal static class OpenCodeAgentIcons { internal static string Glyph(string name) => ""; }
    internal static class OpenCodeCodePalette { internal static void BindText(TextBlock text, string value, string language) { } }
    internal static class OpenCodeFileDetailPolicy { internal static string Language(string path) => "text"; }
    internal enum OpenCodeRichTextStyle { Body }
    internal sealed class OpenCodeRichTextPresenter { internal FrameworkElement Element { get; } = new Grid(); internal void Render(string? source, OpenCodeRichTextStyle style) { } }
    internal static class OpenCodeToolDetailContent
    {
        private sealed class Scope : IDisposable { public void Dispose() { } }
        internal static IDisposable DetailBuildScope(bool detail) => new Scope();
    }
}
