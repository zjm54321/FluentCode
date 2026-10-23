
namespace AIFrontend.Pages.OpenCode;

internal enum OpenCodeSidebarMode { Narrow, Wide }
internal enum OpenCodeSidebarDrawer { None, Left, Right }

internal readonly record struct OpenCodeSidebarSurface(bool Resident, bool LeftOpen, bool RightOpen, bool ShowDrawerButtons);

/// <summary>Page-local effective pixels, not window pixels (the shell also consumes width).</summary>
internal readonly record struct OpenCodeSidebarLayout(
    OpenCodeSidebarMode Mode = OpenCodeSidebarMode.Narrow,
    OpenCodeSidebarDrawer Drawer = OpenCodeSidebarDrawer.None)
{
    // Layout D (aligned dock): floating cards of DockCardWidth, inset DockOuterMargin from the
    // page edge, plus DockInnerGap so the card shadow is not clipped by the pane. Wide mode
    // starts only when the reading column keeps its full OpenCodeTimelineMaxWidth (820) plus the
    // reading surface's 16px padding on either side. A 48px dead band prevents resize oscillation.
    internal const double DockCardWidth = 300;
    internal const double DockOuterMargin = 24;
    internal const double DockInnerGap = 8;
    internal const double LeftWidth = DockOuterMargin + DockCardWidth + DockInnerGap; // 332
    internal const double RightWidth = LeftWidth; // 332
    internal const double ReadingWidth = 820 + 32;
    internal const double EnterWideWidth = LeftWidth + RightWidth + ReadingWidth; // 1516
    internal const double LeaveWideWidth = EnterWideWidth - 48; // 1468

    internal bool IsWide => Mode == OpenCodeSidebarMode.Wide;
    internal bool LeftOpen => IsWide || Drawer == OpenCodeSidebarDrawer.Left;
    internal bool RightOpen => IsWide || Drawer == OpenCodeSidebarDrawer.Right;
    // Wide: no header buttons at all (cards are resident). Narrow: context % and 概况 only;
    // subagents and connection live in the composer's second row.
    internal bool ShowHeaderContext => !IsWide;
    internal bool ShowHeaderConnection => false;
    internal bool ShowHeaderOverview => !IsWide;
    internal bool AnimateDrawers(bool systemAnimationsEnabled) => !IsWide && systemAnimationsEnabled;

    /// <summary>Wide⇄narrow switches fold the cards in / pop them out, unless the system disables animations.</summary>
    internal static bool AnimateModeSwitch(OpenCodeSidebarLayout from, OpenCodeSidebarLayout to, bool systemAnimationsEnabled)
        => systemAnimationsEnabled && from.IsWide != to.IsWide;

    // Home and new-session pages use all available content width, even on a wide window.
    internal OpenCodeSidebarSurface ForSession(bool hasSessionSurface)
        => new(hasSessionSurface && IsWide, hasSessionSurface && LeftOpen,
            hasSessionSurface && RightOpen, hasSessionSurface && !IsWide);

    internal OpenCodeSidebarLayout Resize(double pageWidth)
    {
        if (!double.IsFinite(pageWidth) || pageWidth <= 0) return this;
        var wide = IsWide ? pageWidth >= LeaveWideWidth : pageWidth >= EnterWideWidth;
        return wide == IsWide ? this : new(wide ? OpenCodeSidebarMode.Wide : OpenCodeSidebarMode.Narrow);
    }

    internal OpenCodeSidebarLayout Toggle(OpenCodeSidebarDrawer drawer)
        => IsWide || drawer == OpenCodeSidebarDrawer.None ? this
            : this with { Drawer = Drawer == drawer ? OpenCodeSidebarDrawer.None : drawer };

    // Native light-dismiss callbacks can arrive after opening the opposite drawer.
    internal OpenCodeSidebarLayout Dismiss(OpenCodeSidebarDrawer drawer)
        => IsWide || Drawer != drawer ? this : this with { Drawer = OpenCodeSidebarDrawer.None };

    internal OpenCodeSidebarLayout Escape()
        => IsWide ? this : this with { Drawer = OpenCodeSidebarDrawer.None };
}
