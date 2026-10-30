using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using AIFrontend.Pages.OpenCode;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SidebarVisual;

internal static class Program
{
    internal static string Evidence = "", Repo = "";
    internal static int Result = 2;
    [STAThread] static int Main(string[] args)
    {
        Evidence = Path.GetFullPath(args[0]); Repo = Path.GetFullPath(args[1]); Directory.CreateDirectory(Evidence);
        File.WriteAllText(Path.Combine(Evidence, "stage.txt"), "Main entered");
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(initialization =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new Fixture();
            });
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(Evidence, "host-error.txt"), error.ToString()); }
        return Result;
    }
}
public sealed partial class Fixture : Application
{
    private Window? _window;
    internal Fixture()
    {
        RequestedTheme = ApplicationTheme.Dark;
        InitializeComponent();
        File.AppendAllText(Path.Combine(Program.Evidence, "stage.txt"), "\nApplication constructed");
        UnhandledException += (_, args) =>
        {
            File.WriteAllText(Path.Combine(Program.Evidence, "native-unhandled.txt"), args.Exception.ToString());
            args.Handled = true; Program.Result = 1; _window?.Close(); Exit();
        };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        File.AppendAllText(Path.Combine(Program.Evidence, "stage.txt"), "\nOnLaunched");
        var root = new Grid { RequestedTheme = ElementTheme.Dark, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32)) };
        _window = new Window { Title = "Controlled sidebar layout fixture — no backend", Content = root };
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1024, 850));
        root.Loaded += async (_, _) => await RunAsync(root);
        _window.Activate();
    }
    private async Task RunAsync(Grid root)
    {
        try
        {
            var slot = XDocument.Load(Path.Combine(Program.Repo, "src/AIFrontend/Pages/OpenCodePage.xaml"))
                .Descendants().Single(element => element.Attributes().Any(attribute => attribute.Name.LocalName == "Name" && attribute.Value == "RightPanelHeaderSlot"));
            if (slot.Name.LocalName != "StackPanel") throw new Exception("Production header slot still overlays native children.");
            foreach (var width in new[] { 319, 420 })
            {
                var metadata = new OpenCodeRightPanelView();
                metadata.RenderSession(new("ses_e9b370171ffevyaD9RxFN6pcOW", "恢复AIFrontend会话遗留需求并整合插件前端\n恢复AIFrontend会话遗留需求并整合插件前端", "/controlled/fixture"));
                var stack = new StackPanel { Width = width - 64, Spacing = double.Parse(slot.Attribute("Spacing")!.Value), Children = { metadata.Header } };
                var tools = new OpenCodeWslQuickAccessView();
                tools.Render(new(true, new("fixture", "/mnt/c/fixture", @"C:\fixture"), null), new[] { new WslHostEditor("Zed", @"C:\fixture\zed.exe", WslEditorOpenMode.HostFolder, null) }, "Zed");
                stack.Children.Add(tools);
                var card = new Border { Width = width, Padding = new Thickness(32), Child = stack,
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32)),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                root.Children.Clear(); root.Children.Add(card);
                await Task.Delay(250); root.UpdateLayout();
                if (((StackPanel)metadata.Header).Children.Count != 3) throw new Exception("Original title/ID/copy-hint hierarchy gained extra items.");
                await CaptureAsync(card, $"sidebar-{width}.png");
                File.WriteAllText(Path.Combine(Program.Evidence, $"layout-{width}.json"), JsonSerializer.Serialize(new { width,
                    headerHeight = metadata.Header.ActualHeight, headerChildren = 3, presetOrPluginActions = false, overlap = false,
                    boundary = "Actual production native views; inert metadata/style lookup; no session/network or OS action" }));
            }
            var gauges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Margin = new Thickness(24) };
            var selectors = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Margin = new Thickness(24) };
            var model = new AIFrontend.Services.OpenCode.OpenCodeModelInfo { Variants = new() { new() { Id = "minimal" }, new() { Id = "low" }, new() { Id = "medium" }, new() { Id = "high" }, new() { Id = "xhigh" }, new() { Id = "max" } } };
            foreach (var value in new string?[] { null, "low", "medium", "high", "xhigh", "max" })
            {
                var gauge = new OpenCodeComposerEffortGauge(); gauge.Render(OpenCodeComposerEffort.SemanticTicks(value));
                gauges.Children.Add(new StackPanel { Spacing = 12, Children = { new TextBlock { Text = value ?? "默认" }, new Viewbox { Width = 48, Height = 40, Child = gauge } } });
                // Mirrors the production icon-only composer footprint (OpenCodePage.ComposerToolbar.cs).
                var combo = new OpenCodeNativeComboBox { Width = 36, Height = 32, Padding = new Thickness(0) };
                combo.SetLabelExpanded(false);
                combo.Render(OpenCodeNativeComboBox.Efforts(model), new OpenCodeEffortChoice(value), true);
                selectors.Children.Add(combo);
            }
            var intensityPanel = new StackPanel { Children = { gauges, selectors }, Background = root.Background,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            root.Children.Clear(); root.Children.Add(intensityPanel); await Task.Delay(200); root.UpdateLayout();
            await CaptureAsync(intensityPanel, "gauge-levels.png");
            await CheckEditorAndCatalogsAsync(root);
            await ObserveNativeListClickAsync(root);
            await CheckActualModelPickerAsync(root);
            await NativeCommands.RunAsync(root, Program.Evidence);
            File.WriteAllText(Path.Combine(Program.Evidence, "result.json"), "{\"passed\":true,\"nativeWindow\":true,\"liveBackend\":false,\"typedFakeHttp\":true,\"widths\":[319,420]}");
            Program.Result = 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(Program.Evidence, "failure.txt"), error.ToString()); Program.Result = 1; }
        finally { _window?.Close(); Exit(); }
    }
    private sealed class LaunchRecorder : IWslQuickAccessLauncher
    {
        internal readonly System.Collections.Generic.List<WslHostLaunch> Opens = new();
        public Task OpenExplorerAsync(WslQuickAccessTarget target) => Task.CompletedTask;
        public Task OpenEditorAsync(WslHostLaunch launch) { Opens.Add(launch); return Task.CompletedTask; }
    }
    private static async Task ObserveNativeListClickAsync(Grid root)
    {
        var content = new TextBlock { Text = "Controlled model B" };
        var item = new ListViewItem { Content = content, Tag = "model-B" };
        var list = new ListView { IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.Single };
        list.Items.Add(item); object? clicked = null;
        list.ItemClick += (_, args) => clicked = args.ClickedItem;
        root.Children.Clear(); root.Children.Add(list); await Task.Delay(150); root.UpdateLayout();
        var invoke = FrameworkElementAutomationPeer.CreatePeerForElement(item).GetPattern(PatternInterface.Invoke) as IInvokeProvider;
        if (invoke is null) throw new Exception("Native model-like item has no invoke provider.");
        invoke.Invoke(); await Task.Delay(100);
        File.WriteAllText(Path.Combine(Program.Evidence, "native-list-click.json"), JsonSerializer.Serialize(new { clickedType = clicked?.GetType().FullName,
            isContainer = ReferenceEquals(clicked, item), isContent = ReferenceEquals(clicked, content), selected = ReferenceEquals(list.SelectedItem, item) }));
        if (clicked is null) throw new Exception("Native model-like click did not arrive.");
    }
    private static async Task CheckActualModelPickerAsync(Grid root)
    {
        var shown = new TextBlock { Text = "Model A" };
        var anchor = new Button { Content = "选择模型", Width = 160 };
        var surface = new StackPanel { Margin = new Thickness(24), Spacing = 16, Children = { shown, anchor }, Background = root.Background,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        root.Children.Clear(); root.Children.Add(surface); await Task.Delay(100); root.UpdateLayout();
        var page = new AIFrontend.Pages.OpenCodePage();
        var presentation = await page.ReadModelsAsync() ?? throw new Exception("Actual Page model catalog read failed.");
        var basis = presentation.Basis;
        var choosing = OpenCodeModelPickerDialog.ShowAsync(root.XamlRoot, presentation, anchor: anchor);
        await Task.Delay(200);
        T? Find<T>(DependencyObject node, Func<T, bool> accept) where T : DependencyObject
        {
            if (node is T candidate && accept(candidate)) return candidate;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
                if (Find(VisualTreeHelper.GetChild(node, index), accept) is { } child) return child;
            return null;
        }
        var list = VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).Select(popup => popup.Child)
            .Where(child => child is not null).Select(child => Find<ListView>(child!, candidate => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(candidate) == "OpenCodeModelPickerList")).FirstOrDefault(candidate => candidate is not null)
            ?? throw new Exception("Actual model picker did not open its native list.");
        var row = list.Items.OfType<ListViewItem>().Single(item => item.Tag is OpenCodeModelPickerRow { ModelId: "model-B" });
        var invoke = FrameworkElementAutomationPeer.CreatePeerForElement(row).GetPattern(PatternInterface.Invoke) as IInvokeProvider
            ?? throw new Exception("Actual model row has no native invoke provider.");
        invoke.Invoke();
        var result = await choosing.WaitAsync(TimeSpan.FromSeconds(3)); await Task.Delay(250);
        if (result is not { Outcome: OpenCodeModelPickerOutcome.Confirmed, Choice.ModelId: "model-B" } || !ReferenceEquals(result.Presentation?.Basis, basis))
            throw new Exception("Actual native model click did not freeze the exact owned choice.");
        if (VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).Any(popup => popup.IsOpen && Find<ListView>(popup.Child, candidate => ReferenceEquals(candidate, list)) is not null))
            throw new Exception("Actual chooser stayed open after native model invocation.");
        await page.CommitAsync(result);
        if (page.WaitingCopy(2, false) != "待投递 · 2" || page.WaitingCopy(0, true) != "已投递" || page.WaitingCopy(0, false) != string.Empty)
            throw new Exception("Actual delivery status did not retain concise state/count without explanatory prose.");
        if (page.Http.Writes != 1 || page.VisibleModel != result.Choice.ModelId || page.LastStatus != SessionControlStatus.Accepted)
            throw new Exception("Actual Page/coordinator commit did not persist and observe the native clicked model.");
        using var sent = JsonDocument.Parse(page.Http.LastBody);
        if (sent.RootElement.GetProperty("model").GetProperty("id").GetString() != result.Choice.ModelId
            || sent.RootElement.GetProperty("model").GetProperty("providerID").GetString() != result.Choice.ProviderId)
            throw new Exception("Typed model request did not match the actual clicked catalog choice.");
        shown.Text = page.VisibleModel; await Task.Delay(100); await CaptureAsync(surface, "model-click-closed-updated.png");
        File.WriteAllText(Path.Combine(Program.Evidence, "model-click.json"), JsonSerializer.Serialize(new { actualProductionChooser = true,
            nativeRowInvoked = true, closed = true, choice = result.Choice, basisRetained = true, visibleResultUpdated = true,
            actualPageReadCommitMembers = true, actualSourceCoordinatorClient = true, fakeHttpOnly = true, writes = page.Http.Writes,
            requestBody = page.Http.LastBody, requests = page.Http.Requests.Select(request => new { request.Method, request.Path, request.Query }),
            observedModel = page.VisibleModel, status = page.LastStatus.ToString(), realUserBackendAccess = false }));
        async Task<OpenCodeModelPickerResult> ChooseAgain(AIFrontend.Pages.OpenCodePage target)
        {
            var options = await target.ReadModelsAsync() ?? throw new Exception("Controlled owned catalog unavailable.");
            var pending = OpenCodeModelPickerDialog.ShowAsync(root.XamlRoot, options, anchor: anchor);
            await Task.Delay(150);
            var choices = VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).Select(popup => popup.Child).Where(child => child is not null)
                .Select(child => Find<ListView>(child!, candidate => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(candidate) == "OpenCodeModelPickerList")).First(candidate => candidate is not null)!;
            var selected = choices.Items.OfType<ListViewItem>().Single(item => item.Tag is OpenCodeModelPickerRow { ModelId: "model-B" });
            ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(selected).GetPattern(PatternInterface.Invoke)).Invoke();
            var chosen = await pending.WaitAsync(TimeSpan.FromSeconds(3)); await Task.Delay(180);
            if (chosen.Choice?.ModelId != "model-B") throw new Exception("Native protection-case row did not select.");
            return chosen;
        }
        var revoked = new AIFrontend.Pages.OpenCodePage(); var revokedChoice = await ChooseAgain(revoked);
        revoked.Revoke(); await revoked.CommitAsync(revokedChoice);
        if (revoked.Http.Writes != 0 || revoked.VisibleModel != "model-A") throw new Exception("Revoked native choice wrote or repainted another binding.");
        var unknown = new AIFrontend.Pages.OpenCodePage(); var unknownChoice = await ChooseAgain(unknown);
        unknown.Http.FailWrite = true; await unknown.CommitAsync(unknownChoice);
        if (unknown.Http.Writes != 1 || unknown.LastStatus != SessionControlStatus.OutcomeUnknown || unknown.VisibleModel != "model-A")
            throw new Exception("Unknown native model write was retried, shown accepted or repainted without observation.");
        File.WriteAllText(Path.Combine(Program.Evidence, "model-protections.json"), JsonSerializer.Serialize(new {
            nativeChoices = true, revokedWrites = revoked.Http.Writes, revokedModel = revoked.VisibleModel,
            unknownWrites = unknown.Http.Writes, unknownStatus = unknown.LastStatus.ToString(), unknownModel = unknown.VisibleModel,
            automaticRetry = false, providerOrUserBackend = false }));
    }
    private async Task CheckEditorAndCatalogsAsync(Grid root)
    {
        var recorder = new LaunchRecorder(); var selected = "VS Code";
        var editors = new[] { new WslHostEditor("VS Code", @"C:\fixture\Code.exe", WslEditorOpenMode.RemoteWsl, null),
            new WslHostEditor("Zed", @"C:\fixture\zed.exe", WslEditorOpenMode.HostFolder, null) };
        var state = new WslQuickAccessState(true, new("fixture", "/mnt/c/项目 空格", @"C:\项目 空格"), null);
        var tools = new OpenCodeWslQuickAccessView();
        tools.EditorRequested += async (_, request) => await OpenCodeWslQuickAccess.LaunchEditorAsync(state, editors, selected,
            request.SelectDefault ? request.Name : null, name => { selected = name; tools.Render(state, editors, selected); }, recorder);
        tools.Render(state, editors, selected);
        root.Children.Clear(); root.Children.Add(tools); await Task.Delay(150); root.UpdateLayout();
        var split = tools.Children.OfType<SplitButton>().Single();
        split.Flyout.ShowAt(split); await Task.Delay(150);
        var item = ((MenuFlyout)split.Flyout).Items.OfType<MenuFlyoutItem>().Single(option => option.Text == "Zed");
        await CaptureAsync(item, "editor-menu-zed.png");
        var invoke = FrameworkElementAutomationPeer.CreatePeerForElement(item).GetPattern(PatternInterface.Invoke) as IInvokeProvider;
        if (invoke is null) throw new Exception("Native menu has no invoke provider.");
        invoke.Invoke(); await Task.Delay(100);
        if (selected != "Zed" || split.Content is not PathIcon || recorder.Opens.Count != 1 || recorder.Opens[0].Executable != editors[1].Executable || !recorder.Opens[0].Arguments.SequenceEqual(new[] { @"C:\项目 空格" }))
            throw new Exception("Native Zed menu selection did not immediately reach the editor launch boundary with mapped Windows path.");
        await CaptureAsync(tools, "editor-selected-zed.png");
        var catalogs = new StackPanel { Margin = new Thickness(24), Spacing = 12, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Background = root.Background };
        foreach (var count in new[] { 3, 4, 5 })
        {
            var ids = Enumerable.Range(0, count).Select(i => "opaque-" + i).ToArray();
            var model = new AIFrontend.Services.OpenCode.OpenCodeModelInfo { Variants = ids.Select(id => new AIFrontend.Services.OpenCode.OpenCodeModelVariant { Id = id }).ToList() };
            var ticks = OpenCodeComposerEffort.Ticks(model, ids[1]);
            if (ticks.Count != count || ticks.Count(tick => tick.Selected) != 1) throw new Exception("Catalog tick count differs from available effort levels.");
            var gauge = new OpenCodeComposerEffortGauge(); gauge.Render(ticks);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { new TextBlock { Text = count + " 档 · " + count + " 刻度" }, new Viewbox { Width = 72, Height = 60, Child = gauge } } };
            var combo = new OpenCodeNativeComboBox { Width = 160 };
            combo.Render(OpenCodeNativeComboBox.Efforts(model), new OpenCodeEffortChoice(ids[1]), true); row.Children.Add(combo);
            catalogs.Children.Add(row);
        }
        root.Children.Clear(); root.Children.Add(catalogs); await Task.Delay(150); root.UpdateLayout(); await CaptureAsync(catalogs, "catalog-ticks-3-4-5.png");
        File.WriteAllText(Path.Combine(Program.Evidence, "editor-action.json"), "{\"nativeSplitButton\":true,\"nativeMenuInvoked\":true,\"immediateLaunchBoundary\":true,\"windowsMappedSingleArgument\":true,\"realOSProcessLaunched\":false,\"catalogTicks\":[3,4,5]}");
    }
    internal static async Task CaptureAsync(UIElement element, string name)
    {
        var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(element);
        var pixels = await bitmap.GetPixelsAsync();
        var file = await StorageFile.GetFileFromPathAsync(CreateEmpty(Path.Combine(Program.Evidence, name)));
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
        await encoder.FlushAsync();
    }
    private static string CreateEmpty(string path) { File.WriteAllBytes(path, Array.Empty<byte>()); return path; }
}
