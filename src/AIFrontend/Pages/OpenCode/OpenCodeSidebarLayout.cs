
namespace AIFrontend.Pages.OpenCode;

internal enum OpenCodeSidebarMode { Narrow, Wide }
internal enum OpenCodeSidebarDrawer { None, Left, Right }

/// <summary>Page-local effective pixels, not window pixels (the shell also consumes width).</summary>
internal readonly record struct OpenCodeSidebarLayout(
    OpenCodeSidebarMode Mode = OpenCodeSidebarMode.Narrow,
    OpenCodeSidebarDrawer Drawer = OpenCodeSidebarDrawer.None)
{
    // Existing AppTokens: 320 left + 360 right. Reserve 640 for the reading/editor
    // surface plus its existing 16px padding on either side. The 820px timeline cap
    // still applies at larger widths. A 48px dead band prevents resize oscillation.
    internal const double LeftWidth = 320;
    internal const double RightWidth = 360;
    internal const double EnterWideWidth = LeftWidth + RightWidth + 640 + 32; // 1352
    internal const double LeaveWideWidth = EnterWideWidth - 48; // 1304

    internal bool IsWide => Mode == OpenCodeSidebarMode.Wide;
    internal bool LeftOpen => IsWide || Drawer == OpenCodeSidebarDrawer.Left;
    internal bool RightOpen => IsWide || Drawer == OpenCodeSidebarDrawer.Right;
    internal bool ShowHeaderContext => !IsWide;
    internal bool ShowHeaderConnection => !IsWide;
    internal bool ShowHeaderOverview => !IsWide;
    internal bool AnimateDrawers(bool systemAnimationsEnabled) => !IsWide && systemAnimationsEnabled;

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
