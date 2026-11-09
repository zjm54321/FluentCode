using System;
using FluentCode.Services.Configuration;
using FluentCode.Services.LinkChips;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// Live issue / PR / MR state on link chips (setting “Issue 与 PR 状态”). The chip keeps its exact size: the 14 px site mark
/// is swapped in place for a 14 px state glyph, so a message never reflows when a state arrives, and a failure leaves
/// today's chip untouched. The title appears in the hover card and in the Narrator name. Each state has its own shape,
/// so colour is never the only signal; Monochrome / High Contrast use the text colour.
/// </summary>
internal static partial class OpenCodeLinkChipView
{
    // Octicons 16 px (MIT, © GitHub): issue-opened, issue-closed, skip, git-pull-request, git-pull-request-draft,
    // git-merge, git-pull-request-closed. Disjoint sub-paths; the default even-odd fill keeps the rings hollow.
    private const string IssueOpenedMark = "M8 9.5a1.5 1.5 0 1 0 0-3a1.5 1.5 0 0 0 0 3M8 0a8 8 0 1 1 0 16A8 8 0 0 1 8 0M1.5 8a6.5 6.5 0 1 0 13 0a6.5 6.5 0 0 0-13 0";
    private const string IssueClosedMark = "M11.28 6.78a.75.75 0 0 0-1.06-1.06L7.25 8.69L5.78 7.22a.75.75 0 0 0-1.06 1.06l2 2a.75.75 0 0 0 1.06 0zM16 8A8 8 0 1 1 0 8a8 8 0 0 1 16 0m-1.5 0a6.5 6.5 0 1 0-13 0a6.5 6.5 0 0 0 13 0";
    private const string NotPlannedMark = "M8 0a8 8 0 1 1 0 16A8 8 0 0 1 8 0M1.5 8a6.5 6.5 0 1 0 13 0a6.5 6.5 0 0 0-13 0m9.78-2.22l-5.5 5.5a.749.749 0 0 1-1.275-.326a.75.75 0 0 1 .215-.734l5.5-5.5a.75.75 0 0 1 1.042.018a.75.75 0 0 1 .018 1.042";
    private const string PullOpenMark = "M1.5 3.25a2.25 2.25 0 1 1 3 2.122v5.256a2.251 2.251 0 1 1-1.5 0V5.372A2.25 2.25 0 0 1 1.5 3.25m5.677-.177L9.573.677A.25.25 0 0 1 10 .854V2.5h1A2.5 2.5 0 0 1 13.5 5v5.628a2.251 2.251 0 1 1-1.5 0V5a1 1 0 0 0-1-1h-1v1.646a.25.25 0 0 1-.427.177L7.177 3.427a.25.25 0 0 1 0-.354M3.75 2.5a.75.75 0 1 0 0 1.5a.75.75 0 0 0 0-1.5m0 9.5a.75.75 0 1 0 0 1.5a.75.75 0 0 0 0-1.5m8.25.75a.75.75 0 1 0 1.5 0a.75.75 0 0 0-1.5 0";
    private const string PullDraftMark = "M3.25 1A2.25 2.25 0 0 1 4 5.372v5.256a2.251 2.251 0 1 1-1.5 0V5.372A2.251 2.251 0 0 1 3.25 1m9.5 14a2.25 2.25 0 1 1 0-4.5a2.25 2.25 0 0 1 0 4.5M2.5 3.25a.75.75 0 1 0 1.5 0a.75.75 0 0 0-1.5 0M3.25 12a.75.75 0 1 0 0 1.5a.75.75 0 0 0 0-1.5m9.5 0a.75.75 0 1 0 0 1.5a.75.75 0 0 0 0-1.5M14 7.5a1.25 1.25 0 1 1-2.5 0a1.25 1.25 0 0 1 2.5 0m0-4.25a1.25 1.25 0 1 1-2.5 0a1.25 1.25 0 0 1 2.5 0";
    private const string MergedMark = "M5.45 5.154A4.25 4.25 0 0 0 9.25 7.5h1.378a2.251 2.251 0 1 1 0 1.5H9.25A5.73 5.73 0 0 1 5 7.123v3.505a2.25 2.25 0 1 1-1.5 0V5.372a2.25 2.25 0 1 1 1.95-.218M4.25 13.5a.75.75 0 1 0 0-1.5a.75.75 0 0 0 0 1.5m8.5-4.5a.75.75 0 1 0 0-1.5a.75.75 0 0 0 0 1.5M5 3.25a.75.75 0 1 0 0 .005z";
    private const string PullClosedMark = "M3.25 1A2.25 2.25 0 0 1 4 5.372v5.256a2.251 2.251 0 1 1-1.5 0V5.372A2.251 2.251 0 0 1 3.25 1m9.5 5.5a.75.75 0 0 1 .75.75v3.378a2.251 2.251 0 1 1-1.5 0V7.25a.75.75 0 0 1 .75-.75m-2.03-5.273a.75.75 0 0 1 1.06 0l.97.97l.97-.97a.748.748 0 0 1 1.265.332a.75.75 0 0 1-.205.729l-.97.97l.97.97a.75.75 0 0 1-.018 1.042a.75.75 0 0 1-1.042.018l-.97-.97l-.97.97a.749.749 0 0 1-1.275-.326a.75.75 0 0 1 .215-.734l.97-.97l-.97-.97a.75.75 0 0 1 0-1.06ZM2.5 3.25a.75.75 0 1 0 1.5 0a.75.75 0 0 0-1.5 0M3.25 12a.75.75 0 1 0 0 1.5a.75.75 0 0 0 0-1.5m9.5 0a.75.75 0 1 0 0 1.5a.75.75 0 0 0 0-1.5";

    internal static bool IssueStatusEnabled => AppServices.Settings.Current?.LinkChips?.IssueStatus ?? true;

    /// <summary>Distinct shape per state; an open "/issues/N" keeps the issue ring, an open PR / MR the branch mark.</summary>
    internal static string StateMark(OpenCodeLinkState state, OpenCodeLinkKind kind) => state switch
    {
        OpenCodeLinkState.Open => kind == OpenCodeLinkKind.Issue ? IssueOpenedMark : PullOpenMark,
        OpenCodeLinkState.Draft => PullDraftMark,
        OpenCodeLinkState.Merged => MergedMark,
        OpenCodeLinkState.Closed => PullClosedMark,
        OpenCodeLinkState.Completed => IssueClosedMark,
        _ => NotPlannedMark,
    };

    /// <summary>
    /// Open green, merged / completed purple, closed-unmerged red, draft / not planned grey. Each value is ≥ 4:1 against its
    /// theme's chip fill over the page (non-text contrast needs 3:1); the glyph shape carries the meaning on its own.
    /// </summary>
    internal static Windows.UI.Color StateColor(OpenCodeLinkState state, bool dark) => state switch
    {
        OpenCodeLinkState.Open => dark ? Windows.UI.Color.FromArgb(255, 0x3F, 0xB9, 0x50) : Windows.UI.Color.FromArgb(255, 0x1A, 0x7F, 0x37),
        OpenCodeLinkState.Merged or OpenCodeLinkState.Completed
            => dark ? Windows.UI.Color.FromArgb(255, 0xAB, 0x7D, 0xF8) : Windows.UI.Color.FromArgb(255, 0x82, 0x50, 0xDF),
        OpenCodeLinkState.Closed => dark ? Windows.UI.Color.FromArgb(255, 0xF8, 0x51, 0x49) : Windows.UI.Color.FromArgb(255, 0xCF, 0x22, 0x2E),
        _ => dark ? Windows.UI.Color.FromArgb(255, 0x9E, 0xA7, 0xB3) : Windows.UI.Color.FromArgb(255, 0x59, 0x63, 0x6E),
    };

    internal static FrameworkElement CreateStateIcon(OpenCodeLinkState state, OpenCodeLinkKind kind, bool dark, double size = 14)
    {
        Brush fill = Monochrome
            ? ThemeBrush("TextFillColorPrimaryBrush", dark ? Windows.UI.Color.FromArgb(255, 255, 255, 255) : Windows.UI.Color.FromArgb(0xE4, 0, 0, 0))
            : new SolidColorBrush(StateColor(state, dark));
        var shape = new Microsoft.UI.Xaml.Shapes.Path { Fill = fill, Stretch = Stretch.Uniform, Width = size, Height = size };
        shape.Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), StateMark(state, kind));
        return shape;
    }

    static partial void AttachStatus(Button button, Viewbox icon, OpenCodeLinkChip chip, bool dark)
    {
        if (!OpenCodeLinkStatusService.IsEligible(chip)) return;
        void Apply(OpenCodeLinkStatus status)
        {
            // Same 14 px box inside the same Viewbox: the chip and the line it sits in keep their size.
            icon.Child = CreateStateIcon(status.State, chip.Kind, dark);
            AutomationProperties.SetName(button, chip.AccessibleNameWith(status.Spoken));
        }
        if (OpenCodeLinkStatusService.Shared.TryGetCached(chip, IssueStatusEnabled, out var cached))
        {
            if (cached is not null) Apply(cached);
            return;
        }
        // Lazy: only a realized chip asks, once per element. History never fetches in bulk.
        var asked = false;
        button.Loaded += async (_, _) =>
        {
            if (asked || !IssueStatusEnabled) return;
            asked = true;
            try
            {
                if (await OpenCodeLinkStatusService.Shared.GetAsync(chip, IssueStatusEnabled) is { } status) Apply(status);
            }
            catch (Exception) { /* Keep today's chip. */ }
        };
    }

    static partial void AddStatus(StackPanel panel, OpenCodeLinkChip chip)
    {
        if (!OpenCodeLinkStatusService.IsEligible(chip)) return;
        // Collapsed until known, so the card is exactly today's card when the state is unknown or unavailable.
        var slot = new Grid { ColumnSpacing = 6, Visibility = Visibility.Collapsed };
        slot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        slot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.Children.Insert(0, slot);
        void Fill(OpenCodeLinkStatus status, bool dark)
        {
            slot.Children.Clear();
            var glyph = CreateStateIcon(status.State, chip.Kind, dark, 12);
            glyph.VerticalAlignment = VerticalAlignment.Top;
            glyph.Margin = new Thickness(0, 3, 0, 0);
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock
            {
                Text = status.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 3,
                TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = false,
            });
            text.Children.Add(new TextBlock { Text = status.StateText, FontSize = 12, Opacity = 0.8 });
            Grid.SetColumn(text, 1);
            slot.Children.Add(glyph);
            slot.Children.Add(text);
            slot.Visibility = Visibility.Visible;
        }
        if (OpenCodeLinkStatusService.Shared.TryGetCached(chip, IssueStatusEnabled, out var cached) && cached is not null)
            Fill(cached, Application.Current.RequestedTheme == ApplicationTheme.Dark);
        // The card's content loads each time it is shown: refresh from the cache (theme-correct) or ask once.
        panel.Loaded += async (_, _) =>
        {
            var dark = panel.ActualTheme == ElementTheme.Dark;
            try
            {
                if (await OpenCodeLinkStatusService.Shared.GetAsync(chip, IssueStatusEnabled) is { } status) Fill(status, dark);
            }
            catch (Exception) { /* Keep the address-only card. */ }
        };
    }
}
