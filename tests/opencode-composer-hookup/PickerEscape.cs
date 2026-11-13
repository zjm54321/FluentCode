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

        CommentScannerTests(Check);
        var source = StripComments(File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/OpenCodePickerFlyout.cs")));
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

    /// <summary>Removes C# comments, preserving literals and token/line boundaries for source guards.</summary>
    private static string StripComments(string source)
    {
        // Keep this scanner and its executed cases in sync with tests/fluent-motion/Program.cs
        // and tests/native-ui/NativeUiEffectsTests.cs; each runner compiles its own copy.
        var code = new System.Text.StringBuilder(source.Length);
        var index = 0;
        while (index < source.Length)
        {
            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                code.Append(' '); index += 2;
                while (index < source.Length && source[index] is not '\r' and not '\n') index++;
                continue;
            }
            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                code.Append(' '); index += 2;
                while (index < source.Length)
                {
                    if (source[index] == '*' && index + 1 < source.Length && source[index + 1] == '/') { index += 2; break; }
                    if (source[index] is '\r' or '\n') code.Append(source[index]);
                    index++;
                }
                continue;
            }
            if (source[index] is '"' or '\'')
            {
                var start = index; var quote = source[index];
                var verbatim = quote == '"' && ((index > 0 && source[index - 1] == '@')
                    || (index > 1 && source[index - 1] == '$' && source[index - 2] == '@'));
                var delimiterLength = 1;
                if (quote == '"' && !verbatim)
                    while (start + delimiterLength < source.Length && source[start + delimiterLength] == '"') delimiterLength++;
                if (delimiterLength >= 3)
                {
                    index += delimiterLength;
                    while (index < source.Length)
                    {
                        if (source[index] != '"') { index++; continue; }
                        var quotes = 1;
                        while (index + quotes < source.Length && source[index + quotes] == '"') quotes++;
                        index += quotes;
                        if (quotes == delimiterLength) break;
                    }
                }
                else
                {
                    index++;
                    while (index < source.Length)
                    {
                        var current = source[index++];
                        if (!verbatim && current == '\\' && index < source.Length) { index++; continue; }
                        if (current != quote) continue;
                        if (verbatim && index < source.Length && source[index] == '"') { index++; continue; }
                        break;
                    }
                }
                code.Append(source, start, index - start);
                continue;
            }
            code.Append(source[index++]);
        }
        return code.ToString();
    }

    private static void CommentScannerTests(Action<bool, string> require)
    {
        void Literal(string source, string name)
        {
            require(StripComments(source + " // discard DevWinUI") == source + "  ", name + ": preserves literal and following code, removes line comment");
            require(StripComments(source + " /* discard DevWinUI */") == source + "  ", name + ": preserves literal and following code, removes block comment");
        }
        Literal("var url = \"https://example.test\"; DevWinUI.After();", "URL before same-line vendor code");
        Literal("var text = \"/* DevWinUI */\"; DevWinUI.After();", "Block marker inside a string");
        Literal("var type = \"DevWinUI.Widget\";", "Vendor type named only inside a string remains a reference");
        Literal("var slash = '/'; var quote = '\"'; var apostrophe = '\\''; var backslash = '\\\\'; DevWinUI.After();", "Char literals with slash, quote, escaped apostrophe and backslash");
        foreach (var prefix in new[] { "", "$" })
            Literal("var text = " + prefix + "\"\\\"/* DevWinUI */ // kept\\\"\"; DevWinUI.After();", prefix + " regular string with escaped quotes");
        foreach (var prefix in new[] { "@", "$@", "@$" })
        {
            Literal("var text = " + prefix + "\"https://host/\"\"/* DevWinUI */ // kept\"\"\"; DevWinUI.After();", prefix + " verbatim string with doubled quotes");
            Literal("var text = " + prefix + "\"\"\"/* DevWinUI */ // kept\"\"\"; DevWinUI.After();", prefix + " verbatim string starting with a doubled quote, not a raw delimiter");
        }
        foreach (var count in new[] { 3, 4, 5 })
            foreach (var prefix in new[] { "", "$$" })
            {
                var delimiter = new string('"', count);
                Literal("var text = " + prefix + delimiter + "\nhttps://host/ " + new string('"', count - 1)
                    + " /* DevWinUI */ // kept\n" + delimiter + "; DevWinUI.After();", prefix + " raw string with " + count + "-quote delimiters");
            }
        require(StripComments("var first = 1; // DevWinUI\r\n/* DevWinUI\ncomment */ var second = 2;")
            == "var first = 1;  \r\n \n var second = 2;", "Real vendor comments disappear while line boundaries and following code survive");
        require(StripComments("one/* DevWinUI */two") == "one two", "Removing a comment never joins two code tokens");
    }
}
