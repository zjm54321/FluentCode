using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace FluentCode.Pages.OpenCode;

// Touch counterparts for hover-only affordances. A finger "enters" an element on contact and "exits" on lift —
// or never, when the transcript's pan takes the contact — so touch must neither start hover previews nor be
// the only way hover-revealed actions appear.
internal static class OpenCodeTouchInput
{
    /// <summary>Finger contact. Pen is excluded: a hovering pen is a real hover.</summary>
    internal static bool IsTouch(PointerRoutedEventArgs e) => e.Pointer.PointerDeviceType == PointerDeviceType.Touch;

    /// <summary>
    /// Hover/focus reveal for secondary actions. Mouse keeps the hover behaviour; the first touch or pen
    /// contact latches the actions visible (a later mouse exit returns to hover-only), so a finger never has
    /// to hit an invisible target.
    /// </summary>
    internal static void WireReveal(UIElement host, Action<bool> apply)
    {
        bool hovered = false, focused = false, latched = false;
        void Update() => apply(hovered || focused || latched);
        host.PointerEntered += (_, e) =>
        {
            if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse) hovered = true;
            else latched = true;
            Update();
        };
        host.PointerExited += (_, e) =>
        {
            if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
            hovered = false;
            latched = false;
            Update();
        };
        host.GotFocus += (_, _) => { focused = true; Update(); };
        host.LostFocus += (_, _) => { focused = false; Update(); };
        Update();
    }
}
