using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static partial class Program
{
    private static IEnumerable<string> TimelineSources(string repo) => Directory.EnumerateFiles(
        Path.Combine(repo, "src/FluentCode/Pages/OpenCode"), "*.cs").Where(path =>
        Path.GetFileName(path) is var name && (name.StartsWith("OpenCodeConversationView", StringComparison.Ordinal)
            || name.StartsWith("OpenCodeTool", StringComparison.Ordinal) && name != "OpenCodeToolTheme.cs"
            || name.StartsWith("OpenCodeWorkingIndicator", StringComparison.Ordinal)
            || name.StartsWith("OpenCodeReasoning", StringComparison.Ordinal)
            || name.StartsWith("OpenCodeTimeline", StringComparison.Ordinal)
            || name.StartsWith("OpenCodeLinkChipView", StringComparison.Ordinal)
            || name == "OpenCodeAgentTreeView.cs"));

    private static void TimelineLifecycle(string repo)
    {
        TimelineLifetime.GroupLifecycleProbe.Exercise(Check);
        TimelineLifetime.WindowFrameProbe.Exercise(Check);

        // Syntax-level guards, not a semantic proof of every external service's ownership.
        // Every static collection is reviewed: styles contain resource setters, the set contains
        // strings, and ephemeron tables are keyed by models/options/roots rather than owning them.
        var allowedCollections = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OpenCodeToolDetailContent.cs:Cache"] = "Dictionary<string, Style?>",
            ["OpenCodeToolRowPresentation.cs:CodeKeys"] = "HashSet<string>",
            ["OpenCodeToolRowPresentation.cs:Models"] = "ConditionalWeakTable<OpenCodeContentPart, ToolModels>",
            ["OpenCodeToolRowPresentation.cs:StructuredOutputs"] = "ConditionalWeakTable<string, StructuredProperties>",
            ["OpenCodeConversationView.cs:MessageFingerprints"] = "System.Runtime.CompilerServices.ConditionalWeakTable<OpenCodeMessage, SnapshotFingerprint>",
            ["OpenCodeConversationView.cs:PartFingerprints"] = "System.Runtime.CompilerServices.ConditionalWeakTable<OpenCodeContentPart, SnapshotFingerprint>",
            ["OpenCodeConversationView.cs:MessageProjections"] = "System.Runtime.CompilerServices.ConditionalWeakTable<OpenCodeMessage, MessageProjection>",
            ["OpenCodeToolDetailPopup.cs:SuppressedRoots"] = "ConditionalWeakTable<DependencyObject, object>",
            ["OpenCodeToolDetailLiveChildOutput.cs:ChildOutputs"] = "ConditionalWeakTable<OpenCodeToolDetailOptions, ChildOutputBinding>",
        };
        var collectionCount = 0;
        var binds = 0;
        var sharedSubscriptions = 0;
        foreach (var path in TimelineSources(repo))
        {
            var name = Path.GetFileName(path);
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();
            foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>().Where(f => f.Modifiers.Any(SyntaxKind.StaticKeyword)))
            {
                var type = field.Declaration.Type is NullableTypeSyntax nullable ? nullable.ElementType : field.Declaration.Type;
                if (type is not (GenericNameSyntax or ArrayTypeSyntax or QualifiedNameSyntax)) continue;
                var typeName = type.ToString();
                // Qualified scalar structs (e.g. Thickness) are not collections.
                if (type is QualifiedNameSyntax && !typeName.Contains("<", StringComparison.Ordinal)) continue;
                foreach (var variable in field.Declaration.Variables)
                {
                    var key = name + ":" + variable.Identifier.ValueText;
                    Check(allowedCollections.TryGetValue(key, out var expected) && expected == typeName,
                        "source guard: no unreviewed static collection/control/closure root: " + key + " (" + typeName + ")");
                    collectionCount++;
                }
            }
            Check(!root.DescendantNodes().OfType<EventFieldDeclarationSyntax>().Any(e => e.Modifiers.Any(SyntaxKind.StaticKeyword))
                && !root.DescendantNodes().OfType<EventDeclarationSyntax>().Any(e => e.Modifiers.Any(SyntaxKind.StaticKeyword)),
                "source guard: no static events in " + name);
            Check(!root.DescendantNodes().OfType<PropertyDeclarationSyntax>().Any(p => p.Modifiers.Any(SyntaxKind.StaticKeyword)
                && (p.Type is NullableTypeSyntax nullable ? nullable.ElementType : p.Type) is GenericNameSyntax or ArrayTypeSyntax
                && p.Identifier.ValueText != "LiveEntranceAnimator"),
                "source guard: no static collection/delegate properties in " + name);
            foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (!assignment.IsKind(SyntaxKind.AddAssignmentExpression)) continue;
                var target = assignment.Left.ToString();
                if (!target.Contains(".", StringComparison.Ordinal) || !char.IsUpper(target[0])) continue;
                var groupVisibility = name == "OpenCodeToolGroupView.cs" && target == "OpenCodeWorkingClock.Shared.HostVisibilityChanged"
                    && assignment.Right.ToString() == "HostVisibilityChanged"
                    && assignment.Ancestors().OfType<ConstructorDeclarationSyntax>().SingleOrDefault()?.Identifier.ValueText == "OpenCodeToolGroupVisual";
                var queuedFrame = name == "OpenCodeConversationView.cs" && target == "CompositionTarget.Rendering"
                    && assignment.Right.ToString() == "WindowFrame"
                    && assignment.Ancestors().OfType<MethodDeclarationSyntax>().SingleOrDefault()?.Identifier.ValueText == "QueueWindowUpdate";
                Check(groupVisibility || queuedFrame,
                    "source guard: no new static/singleton event subscription or captured-control closure: " + target);
                sharedSubscriptions++;
            }
            foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call =>
                call.Expression.ToString() is "FluentCode.Motion.FluentMotion.Bind" or "FluentCode.Motion.FluentMotion.BindTransitions"
                    or "FluentMotion.Bind" or "FluentMotion.BindTransitions"))
            {
                var host = call.ArgumentList.Arguments[0].ToString();
                var constructor = call.Ancestors().OfType<ConstructorDeclarationSyntax>().SingleOrDefault();
                Check(!call.Ancestors().Any(n => n is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax
                    or DoStatementSyntax or AnonymousFunctionExpressionSyntax)
                    && constructor is not null && (name == "OpenCodeToolGroupView.cs" && host == "_root"
                    && constructor.Identifier.ValueText == "OpenCodeToolGroupVisual"
                    || name == "OpenCodeToolDetailPopup.cs" && host == "_dialogPresenter"
                    && constructor.Identifier.ValueText == "OpenCodeToolDetailPopup"),
                    "source guard: motion binds only once in the owning host constructor: " + name);
                binds++;
            }
        }
        Check(collectionCount == allowedCollections.Count && binds == 2 && sharedSubscriptions == 2,
            "source guard: reviewed static collection and lifetime-binding inventory remains exact");
        var group = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/OpenCodeToolGroupView.cs"));
        Check(group.Contains("_working.VisibilityChanged += UpdateTimer;", StringComparison.Ordinal)
            && group.Contains("_working.VisibilityChanged -= UpdateTimer;", StringComparison.Ordinal)
            && group.Contains("OpenCodeWorkingClock.Shared.HostVisibilityChanged -= HostVisibilityChanged;", StringComparison.Ordinal)
            && group.Contains("_visual?.Dispose(); _visual = null;", StringComparison.Ordinal),
            "source guard: production subscriptions and retained group-to-visual disposal match the executed probe");
        var indicator = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/OpenCodeWorkingIndicator.cs"));
        Check(indicator.Contains("UnwatchVisibility();", StringComparison.Ordinal)
            && indicator.Contains("element.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, token);", StringComparison.Ordinal)
            && indicator.Contains("_visibilityWatches.Clear();", StringComparison.Ordinal)
            && indicator.Contains("clock.Leave(this);", StringComparison.Ordinal),
            "source guard: indicator releases visibility callbacks and leaves the shared clock");
        var conversation = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(repo,
            "src/FluentCode/Pages/OpenCode/OpenCodeConversationView.cs"))).GetRoot();
        foreach (var name in new[] { "DisconnectViewport", "ClearMessagesCore", "WindowFrame" })
            Check(conversation.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == name)
                .DescendantNodes().OfType<InvocationExpressionSyntax>().Any(c => c.Expression.ToString() == "CancelWindowFrame"),
                "source guard: frame cancellation remains wired to " + name);
        Check(conversation.ToString().Contains("Unloaded += (_, _) => DisconnectViewport();", StringComparison.Ordinal),
            "source guard: unload disconnects and cancels the bounded frame handler");
    }

    private static void TimelineResourceContracts(string repo)
    {
        var app = XDocument.Load(Path.Combine(repo, "src/FluentCode/App.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var (key, setters) in new[]
        {
            ("OpenCodeSubtleSurfaceStyle", "Background={ThemeResource SubtleFillColorSecondaryBrush}"),
            ("OpenCodeTransparentSurfaceStyle", "Background={ThemeResource SubtleFillColorTransparentBrush}"),
            ("OpenCodeDividerSurfaceStyle", "Background={ThemeResource DividerStrokeColorDefaultBrush}"),
            ("OpenCodeSubtleCardChromeStyle", "Background={ThemeResource SubtleFillColorSecondaryBrush};BorderBrush={ThemeResource CardStrokeColorDefaultBrush}"),
        })
        {
            var style = app.Descendants().Single(e => (string?)e.Attribute(x + "Key") == key);
            Check((string?)style.Attribute("TargetType") == "Border" && string.Join(";", style.Elements()
                .Select(s => (string?)s.Attribute("Property") + "=" + (string?)s.Attribute("Value"))) == setters,
                "source resource guard: reused style has exactly the old setters: " + key);
        }
        var code = app.Descendants().Single(e => (string?)e.Attribute(x + "Key") == "OpenCodeCodeTextBlockStyle");
        Check(code.Elements().Any(e => (string?)e.Attribute("Property") == "FontFamily"
            && (string?)e.Attribute("Value") == "{ThemeResource AppMonospaceFontFamily}"),
            "source resource guard: code style follows the existing user monospace resource");
        foreach (var path in TimelineSources(repo))
            Check(!File.ReadAllText(path).Contains("\"Cascadia Mono", StringComparison.Ordinal)
                && !File.ReadAllText(path).Contains("\"Consolas\"", StringComparison.Ordinal),
                "source resource guard: no user-font-bypassing mono literals in " + Path.GetFileName(path));
        var results = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/OpenCodeToolDetailResults.cs"));
        Check(results.Contains("(Style)Application.Current.Resources[\"SubtleButtonStyle\"]", StringComparison.Ordinal)
            && !results.Contains("TryStyle(\"SubtleButtonStyle\")", StringComparison.Ordinal),
            "source resource guard: stock SubtleButtonStyle lookup cannot silently fall back");
    }
}
