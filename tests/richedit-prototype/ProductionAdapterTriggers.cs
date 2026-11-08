using System.Linq.Expressions;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace RichEditPrototype;

internal sealed partial class ProductionAdapterProbe
{
    // Load the Debug-built product, not a copied trigger/list implementation. Reflection crosses
    // internal test boundaries only; actual controller, view, editor, receipts and list slots run.
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Constructors = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static object? Get(object value, string name) => value.GetType().GetProperty(name, Members) is { } property
        ? property.GetValue(value) : value.GetType().GetField(name, Members)!.GetValue(value);
    private static void Set(object value, string name, object? next) => value.GetType().GetProperty(name, Members)!.SetValue(value, next);
    private static object? Call(object value, string name, params object?[] arguments)
    {
        var type = value as Type ?? value.GetType();
        var method = type.GetMethods(Members).Single(m => m.Name == name && m.GetParameters().Length == arguments.Length);
        try { return method.Invoke(value is Type ? null : value, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }
    private static object Create(Type type, params object?[] arguments)
        => Activator.CreateInstance(type, Constructors, null, arguments, null)!;
    private static Delegate DelegateFor(Type type, Func<object?[], object?> callback)
    {
        var signature = type.GetMethod("Invoke")!;
        var parameters = signature.GetParameters().Select(p => Expression.Parameter(p.ParameterType)).ToArray();
        var invoke = Expression.Invoke(Expression.Constant(callback), Expression.NewArrayInit(typeof(object), parameters.Select(p => Expression.Convert(p, typeof(object)))));
        Expression body = signature.ReturnType == typeof(void) ? Expression.Block(invoke, Expression.Empty()) : Expression.Convert(invoke, signature.ReturnType);
        return Expression.Lambda(type, body, parameters).Compile();
    }
    private static void Listen(object value, string name, Func<object?[], object?> callback)
    {
        var member = value.GetType().GetEvent(name, Members)!;
        member.GetAddMethod(true)!.Invoke(value, new object[] { DelegateFor(member.EventHandlerType!, callback) });
    }

    private async Task TriggerLists()
    {
        var directory = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "src/FluentCode/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64"));
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            var path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        }
        AssemblyLoadContext.Default.Resolving += Resolve;
        var product = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(directory, "FluentCode.dll"));
        Type T(string name) => product.GetType("FluentCode." + name, throwOnError: true)!;
        using var http = new HttpClient(new TriggerHttp());
        var options = Create(T("Services.OpenCode.OpenCodeClientOptions"));
        Set(options, "BaseUrl", "https://offline.invalid");
        Set(options, "Secret", "fixture-only");
        var client = Create(T("Services.OpenCode.OpenCodeClient"), http, options, true);
        var sourceType = T("Services.Notifications.OpenCodePageSource");
        var sourceFactory = sourceType.GetConstructors(Constructors).Single(c => c.GetParameters().Length == 3).GetParameters()[2].ParameterType;
        var source = Create(sourceType, "native-trigger-fixture", 7L, DelegateFor(sourceFactory, _ => client));
        var binding = Create(T("Pages.OpenCode.OpenCodeComposerBinding"), source, 7L, 1, client, 1, 1, "ses_fixture");
        Set(binding, "ConfirmedDirectory", "/workspace");
        var coreType = T("Pages.OpenCode.OpenCodeComposerController");
        var owns = coreType.GetConstructors(Constructors).Single(c => c.GetParameters().Length == 2).GetParameters()[0].ParameterType;
        var core = Create(coreType, DelegateFor(owns, _ => true), Create(T("Services.Notifications.OpenCodeInteractionGate")));
        Call(core, "Bind", binding);
        object Snapshot() => Get(core, "Snapshot")!;
        var viewType = T("Pages.OpenCode.OpenCodeComposerView");
        var viewParameters = viewType.GetConstructors(Constructors).Single().GetParameters();
        var view = Create(viewType, DelegateFor(viewParameters[0].ParameterType, _ => Snapshot()),
            DelegateFor(viewParameters[1].ParameterType, _ => Call(core, "BeginCatalogLifetime")),
            DelegateFor(viewParameters[2].ParameterType, a => Call(core, "EndCatalogLifetime", a[0])));
        var editor = (RichEditBox)Create(T("Pages.OpenCode.OpenCodeComposerEditor"));
        editor.AcceptsReturn = true;
        editor.TextWrapping = TextWrapping.Wrap;
        editor.FontSize = 14;
        editor.Padding = new Thickness(12, 9, 12, 9);
        editor.MinHeight = 40;
        var attach = viewType.GetMethod("AttachEditor", Members)!;
        attach.Invoke(view, new object[] { editor, DelegateFor(attach.GetParameters()[1].ParameterType, _ => Get(editor, "IsComposing")) });
        var searchType = T("Pages.OpenCode.OpenCodeComposerMentionSearch");
        var search = DelegateFor(searchType, a => Call(T("Pages.OpenCode.OpenCodeComposerMentions"), "SearchAsync", client, "/workspace", a[0], a[1], null));
        var providerType = viewType.GetProperty("MentionSearchProvider", Members)!.PropertyType;
        Set(view, "MentionSearchProvider", DelegateFor(providerType, _ => search));
        Listen(view, "TextEdited", a =>
        {
            var e = a[1]!;
            Set(e, "Acknowledgment", Call(core, "TryApplyDocumentObservation", Get(e, "Token"), Get(e, "Observation"), false));
            return null;
        });
        Listen(core, "SnapshotChanged", _ => Call(view, "RequestRender"));
        Listen(view, "ModelUndoRequested", a => Call(core, "TryUndoDocument", a[0], a[1]));
        Listen(view, "CatalogOpenRequested", a =>
        {
            var e = a[1]!;
            return Call(core, "LoadCatalogAsync", Get(e, "Token"), Get(e, "Lifetime"), Get(e, "Refresh"), CancellationToken.None);
        });
        Listen(view, "CatalogDismissRequested", a => Call(core, "DismissCatalog", Get(a[1]!, "Handle")));
        Listen(view, "CommandChosen", a =>
        {
            var e = a[1]!;
            Set(e, "Accepted", Call(core, "TrySelectCommand", Get(e, "Token"), Get(e, "Handle"), Get(e, "Identity")));
            return null;
        });
        Listen(view, "CompletionRequested", a =>
        {
            var e = a[1]!;
            Set(e, "Accepted", Call(core, "TryCompleteTrigger", Get(e, "Token"), Get(e, "Handle"), Get(e, "Completion")));
            return null;
        });
        editor.PreviewKeyDown += (_, e) => Call(view, "HandleEditorKey", e, false, Get(editor, "IsComposing"), TimeSpan.FromSeconds(1), false, false);
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Spacing = 8 };
        panel.Children.Add((UIElement)view);
        panel.Children.Add(editor);
        Grid.SetRow(panel, 1);
        var root = (Grid)Content;
        _normal.Visibility = Visibility.Collapsed;
        root.Children.Add(panel);
        _triggerEditor = editor;
        var evidence = Path.Combine(Environment.CurrentDirectory, "artifacts", "composer-align-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-triggers");
        Directory.CreateDirectory(evidence);
        async Task Settle() { await Task.Delay(350); await Idle(); root.UpdateLayout(); }
        async Task Reset()
        {
            Call(view, "CloseMention");
            if (Get(Snapshot(), "SelectedCommand") is not null)
            {
                Call(core, "RemoveCommand", Get(Snapshot(), "Token"));
                await Settle();
            }
            Call(editor, "SelectAll");
            Call(editor, "ReplaceSelection", ""); // An acknowledged user clear ends a dismissed typed episode.
            await Settle();
        }
        try
        {
            await Settle();
            await Reset();
            await ProductionPasteScenarios(core, view, editor, Reset, Settle);
            if (_pasteOnly) return;
            await Reset();
            Call(view, "InsertContextTrigger"); await Settle();
            Check("plus-context-opens-after-model-paint", (bool)Get(view, "IsMentionVisible")!);
            await Reset();
            await Unicode("@");
            await Settle();
            var slot = (FrameworkElement)Get(view, "_mentionSlot")!;
            var list = (ListView)Get(view, "_mentionList")!;
            ProbeLog.Write($"TRIGGERS @ visible={Get(view, "IsMentionVisible")} slotHeight={slot.ActualHeight} rows={list.Items.Count} raw={Get(editor, "Text")} caret={Get(editor, "SelectionStart")} sync={Get(Get(view, "_sync")!, "State")}");
            Check("@-real-view-visible-height-rows", (bool)Get(view, "IsMentionVisible")! && slot.ActualHeight > 0 && list.Items.Count > 0);
            await Capture(panel, evidence, "mention-open");
            await Unicode("rev");
            await Settle();
            Check("@-real-view-filter", list.Items.Count == 1 && ((TextBlock)Get(view, "_mentionQueryText")!).Text == "@rev");
            await Key(VirtualKey.Enter);
            await Settle();
            Check("@-Enter-accepts-chip-not-send", (string)Get(editor, "Text")! == "@review " && !(bool)Get(view, "IsMentionVisible")! && ((string)Call(editor, "ReadStory")!).Contains('\ufffc'));
            await Unicode("@ex"); await Settle();
            Check("@-raw-caret-after-one-character-chip", (bool)Get(view, "IsMentionVisible")! && list.Items.Count == 1
                && (string)Get(editor, "Text")! == "@review @ex" && (int)Get(editor, "SelectionStart")! == "@review @ex".Length);
            await Key(VirtualKey.Tab); await Settle();
            await Reset();
            await Unicode("@ex"); await Settle(); await Key(VirtualKey.Tab); await Settle();
            Check("@-Tab-accepts-chip", (string)Get(editor, "Text")! == "@explorer ");
            await Reset(); await Unicode("@"); await Settle(); await Key(VirtualKey.Escape); await Settle();
            Check("@-Escape-closes", !(bool)Get(view, "IsMentionVisible")! && slot.Visibility == Visibility.Collapsed);
            await Reset();
            Call(editor, "BeginNativeComposition"); Call(view, "OnEditorCompositionStarted", editor, null);
            editor.Document.Selection.TypeText("@rev");
            Check("@-IME-preedit-does-not-open", !(bool)Get(view, "IsMentionVisible")!);
            Call(editor, "EndNativeComposition"); Call(view, "OnEditorCompositionEnded", editor, null);
            await Settle();
            Check("@-IME-drained-commit-opens", (bool)Get(view, "IsMentionVisible")! && list.Items.Count == 1);
            await Key(VirtualKey.Escape); await Settle();
            await Reset(); await Unicode("/"); await Settle();
            var menuSlot = (FrameworkElement)Get(view, "_menuSlot")!;
            Check("slash-opens-real-list", (bool)Get(view, "_menuShown")! && menuSlot.ActualHeight > 0);
            await Unicode("rev"); await Settle(); await Key(VirtualKey.Tab); await Settle();
            ProbeLog.Write($"TRIGGERS slash Tab raw={Get(editor, "Text")} selected={Get(Snapshot(), "SelectedCommand")}");
            Check("slash-filter-Tab-completion", (string)Get(editor, "Text")! == "/review");
            await Key(VirtualKey.Tab); await Settle();
            Check("slash-second-Tab-stages-command", (string?)Get(Snapshot(), "SelectedCommand") == "review");
            await Reset(); await Unicode("/review"); await Settle(); await Key(VirtualKey.Enter); await Settle();
            Check("slash-Enter-stages-command", (string?)Get(Snapshot(), "SelectedCommand") == "review");
            Call(core, "RemoveCommand", Get(Snapshot(), "Token"));
            await Reset(); await Unicode("/"); await Settle(); await Key(VirtualKey.Escape); await Settle();
            Check("slash-Escape-closes", !(bool)Get(view, "_menuShown")!);
            await Reset();
            Call(editor, "BeginNativeComposition"); Call(view, "OnEditorCompositionStarted", editor, null);
            editor.Document.Selection.TypeText("/review");
            Check("slash-IME-preedit-does-not-open", !(bool)Get(view, "_menuShown")!);
            Call(editor, "EndNativeComposition"); Call(view, "OnEditorCompositionEnded", editor, null);
            await Settle();
            ProbeLog.Write($"TRIGGERS slash IME visible={Get(view, "_menuShown")} raw={Get(editor, "Text")} suppressed={Get(Get(view, "_menuSession")!, "Suppressed")}");
            Check("slash-IME-drained-commit-opens", (bool)Get(view, "_menuShown")!);
            await Key(VirtualKey.Escape); await Settle(); await Reset();
            var shellEntered = false;
            Listen(editor, "PreviewCharacterReceived", a =>
            {
                var e = (CharacterReceivedRoutedEventArgs)a[0]!;
                if (e.Character != '!') return null;
                var before = (string)Get(editor, "Text")!;
                ProbeLog.Write($"TRIGGERS bang preview raw={before} caret={Get(editor, "SelectionStart")} composing={Get(editor, "IsComposing")}");
                if ((int)Get(editor, "SelectionStart")! != 0 || !(bool)Call(T("Pages.OpenCode.OpenCodeComposerShellState"), "IsEntry", before, "!" + before,
                    Get(editor, "SelectionStart"), Get(editor, "SelectionLength"), Get(editor, "IsComposing"), Get(view, "IsAutocompleteVisible"))!) return null;
                e.Handled = true;
                shellEntered = true; // Page-owned terminal surface is the fixture boundary.
                return null;
            });
            await Unicode("!"); await Settle();
            ProbeLog.Write($"TRIGGERS bang entered={shellEntered} raw={Get(editor, "Text")}");
            Check("bang-native-character-enters-shell-without-draft-edit", shellEntered && (string)Get(editor, "Text")! == "");
            await Reset(); shellEntered = false; await Unicode("ab"); await Settle(); Call(editor, "Select", 1, 0);
            await Unicode("!"); await Settle();
            Check("bang-at-offset-one-is-ordinary-text", !shellEntered && (string)Get(editor, "Text")! == "a!b");
            ProbeLog.Write("TRIGGERS EVIDENCE " + evidence);
        }
        finally
        {
            _triggerEditor = null;
            root.Children.Remove(panel);
            _normal.Visibility = Visibility.Visible;
            ((IDisposable)client).Dispose();
            AssemblyLoadContext.Default.Resolving -= Resolve;
        }
    }

    private sealed class TriggerHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var data = request.RequestUri!.AbsolutePath.EndsWith("/agent", StringComparison.Ordinal) ? """[{"id":"explorer","name":"Explorer","request":{},"mode":"subagent","hidden":false,"permissions":[]}]"""
                : request.RequestUri.AbsolutePath.EndsWith("/skill", StringComparison.Ordinal) ? """[{"id":"review","name":"Review","path":"/workspace/SKILL.md","content":"fixture","description":"Review"}]"""
                // Multiple selectable prefixes are required for shell-style Tab completion;
                // one prefix match is confirmed directly by the production menu policy.
                : """[{"name":"review","description":"Review","hints":["text"]},{"name":"review-all","description":"Review all","hints":["text"]}]""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"location\":{\"directory\":\"/workspace\"},\"data\":" + data + "}", Encoding.UTF8, "application/json") });
        }
    }
}
