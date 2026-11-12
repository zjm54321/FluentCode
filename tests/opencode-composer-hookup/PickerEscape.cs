using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml.Input;
using Windows.System;

// The runner extracts the production Escape callback verbatim. Native event delivery is not simulated:
// only Flyout.Hide is a double; static guards verify the content-root preview hookup and result paths.
internal static partial class PickerEscapeTests
{
    private delegate void KeyEventHandler(object sender, KeyRoutedEventArgs args);

    private sealed class Flyout
    {
        internal int HideCalls { get; private set; }
        public void Hide() => HideCalls++;
    }

    internal static void Run(string repo)
    {
        var checks = 0;
        void Check(bool value, string note)
        {
            checks++;
            if (!value) throw new InvalidOperationException("Picker Escape: " + note);
        }

        var source = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/OpenCodePickerFlyout.cs"));
        source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        source = Regex.Replace(source, @"//[^\n]*", "");
        var registration = source.IndexOf("content.AddHandler(UIElement.PreviewKeyDownEvent, dismissOnEscape, true);", StringComparison.Ordinal);
        Check(registration >= 0, "content root receives preview Escape even when an ancestor marks it handled");
        Check(registration >= 0 && registration < source.IndexOf("flyout.ShowAt(anchor);", StringComparison.Ordinal),
            "preview handler is installed before the flyout opens");
        Check(Regex.IsMatch(source, @"finally\s*\{[^}]*content\.RemoveHandler\(UIElement\.PreviewKeyDownEvent, dismissOnEscape\);"),
            "finally detaches the handler on cancellation, selection and show failure");
        Check(source.Contains("flyout.Closed += (_, _) => completion.TrySetResult(cancelled);", StringComparison.Ordinal),
            "dismissal retains the existing cancelled result path");
        Check(source.Contains("wireChoice(value => { completion.TrySetResult(value); flyout.Hide(); });", StringComparison.Ordinal),
            "selection still completes with the chosen value before closing");
        var callback = Regex.Match(source, @"(?ms)^        var dismissOnEscape = new KeyEventHandler\(\(_, e\) =>\s*\{.*?^        \}\);").Value;
        Check(callback.Length > 0 && !Regex.IsMatch(callback, @"\bcompletion\b|\bwireChoice\b|\bchoose\b"),
            "Escape callback only dismisses, never publishes a selection or replaces the cancellation result");

        foreach (var alreadyHandled in new[] { false, true })
        {
            var flyout = new Flyout();
            var key = new KeyRoutedEventArgs { Key = VirtualKey.Escape, Handled = alreadyHandled };
            CreateDismissHandler(flyout)(new object(), key);
            Check(flyout.HideCalls == 1 && key.Handled, "production callback hides once and handles Escape, initial Handled=" + alreadyHandled);
        }
        foreach (var otherKey in new[] { VirtualKey.Enter, VirtualKey.Up, VirtualKey.Down })
        {
            var flyout = new Flyout();
            var key = new KeyRoutedEventArgs { Key = otherKey };
            CreateDismissHandler(flyout)(new object(), key);
            Check(flyout.HideCalls == 0 && !key.Handled, "production callback leaves native navigation/selection untouched: " + otherKey);
        }
        Console.WriteLine($"PASS picker Escape: {checks} production-callback/static assertions; Flyout double, no native UI.");
    }
}
