using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentCode.Pages.OpenCode;

/// <summary>Shared agent-glyph foreground; follows the element's actual theme, not a cached app theme.</summary>
internal static class OpenCodeAgentIconTheme
{
    internal static FontIcon Create(string? agentName, string? runtimeColor = null, double fontSize = 14)
    {
        var icon = new FontIcon { Glyph = OpenCodeAgentIcons.Glyph(agentName), FontSize = fontSize };
        void Refresh() => icon.Foreground = new SolidColorBrush(OpenCodeAgentPresentation.Color(
            OpenCodeAgentIcons.Color(agentName, runtimeColor, icon.ActualTheme == ElementTheme.Dark))!.Value);
        Services.Appearance.ThemeResources.Observe(icon, Refresh);
        return icon;
    }
}
