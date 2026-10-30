using System.Runtime.CompilerServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace AIFrontend.Pages.OpenCode;

// Mirrors ComboBox's TouchInputMode for flyout pickers: the input that invoked a trigger decides row sizing.
internal static class OpenCodePickerInput
{
    internal const double TouchRowHeight = 48, MouseRowHeight = 34, TouchActionSize = 40, MouseActionSize = 24;
    private static readonly ConditionalWeakTable<UIElement, StrongBox<bool>> Touch = new();

    internal static void Track(UIElement trigger)
    {
        var touch = Touch.GetValue(trigger, _ => new StrongBox<bool>());
        // handledEventsToo: Button marks its own press handled before Click.
        trigger.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
            touch.Value = e.Pointer.PointerDeviceType is PointerDeviceType.Touch or PointerDeviceType.Pen), true);
        trigger.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, _) => touch.Value = false), true);
    }

    internal static bool IsTouch(UIElement? trigger)
        => trigger is not null && Touch.TryGetValue(trigger, out var touch) && touch.Value;
}
