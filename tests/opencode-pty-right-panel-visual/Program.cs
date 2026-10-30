using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PtyRightPanelVisual;

internal static class Program
{
    internal static string Evidence = "", Theme = "dark";
    internal static int Result = 2;
    [STAThread] static int Main(string[] args)
    {
        Evidence = Path.GetFullPath(args[0]); Theme = args.Length > 1 ? args[1] : "dark"; Directory.CreateDirectory(Evidence);
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

/// <summary>
/// Production PTY strip/button/overlay/view hosted in a page-shaped mock: the right session card mirrors
/// OpenCodePage.xaml RightDock → RightSessionCard → WslQuickAccessSection (same paddings, columns and slots),
/// and the overlay host is sized by OpenCodeBrowserOverlayLayout.Place exactly like SizePersistentPtyOverlay.
/// </summary>
public sealed partial class Fixture : Application
{
    private const double PageWidth = 1280, PageHeight = 900, TabsHeight = 40;
    private Window? _window;
    private readonly List<string> _log = new();

    internal Fixture()
    {
        // Application-level theme so code-resolved brushes and ThemeResource lookups agree.
        RequestedTheme = Program.Theme == "light" ? ApplicationTheme.Light : ApplicationTheme.Dark;
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            File.WriteAllText(Path.Combine(Program.Evidence, "native-unhandled.txt"), args.Exception.ToString());
            args.Handled = true; Program.Result = 1; _window?.Close(); Exit();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new Window { Title = "PTY right panel visual fixture" };
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 1000));
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            foreach (var theme in new[] { Program.Theme == "light" ? ElementTheme.Light : ElementTheme.Dark })
            {
                var name = theme == ElementTheme.Dark ? "dark" : "light";
                var scene = new Scene(theme);
                _window!.Content = scene.Root;
                _window.Activate();
                await Settle(scene.Root);
                scene.ShowStrip();
                await Task.Delay(450); await Settle(scene.Root);
                await Capture(scene.Root, $"{name}-right-panel.png");
                await Capture(scene.Card, $"{name}-session-card.png");
                await scene.OpenAsync("pty_build");
                await Task.Delay(700); await Settle(scene.Root);
                await Capture(scene.Root, $"{name}-terminal-overlay.png");
                await TryDesktop($"{name}-terminal-overlay-desktop.png");
                scene.RenderState(OpenCodePersistentPtyPhase.Live, OpenCodePersistentPtyRole.Controller, true);
                await Settle(scene.Root); await Task.Delay(900); await TryDesktop($"{name}-terminal-controller-desktop.png");
                scene.RenderState(OpenCodePersistentPtyPhase.Disconnected, OpenCodePersistentPtyRole.Observer, false, "连接已断开");
                await Settle(scene.Root); await Task.Delay(900); await TryDesktop($"{name}-terminal-disconnected-desktop.png");
                await Capture(scene.Root, $"{name}-terminal-disconnected.png");
                scene.RenderState(OpenCodePersistentPtyPhase.Live, OpenCodePersistentPtyRole.Observer, false);
                Check(scene.OverlayHost.Visibility == Visibility.Visible, $"{name}: overlay host visible after open");
                Check(Math.Abs(scene.Overlay.ActualWidth - PageWidth) < 1, $"{name}: overlay host spans the page width ({scene.Overlay.ActualWidth})");
                Check(Find<FrameworkElement>(scene.Root, "OpenCodePersistentPtyClose") is { } close && AutomationProperties.GetName(close) == "关闭终端浮窗", $"{name}: close button keeps automation contract");
                Check(scene.Strip.Button.ActualWidth is >= 39 and <= 41, $"{name}: button is a 40px quick-access target ({scene.Strip.Button.ActualWidth})");
                var row = scene.Strip.Button.TransformToVisual(scene.Card).TransformPoint(default);
                Check(row.X + scene.Strip.Button.ActualWidth <= scene.Card.ActualWidth - 16, $"{name}: button fits inside the card row ({row.X})");
                scene.Close();
                await Settle(scene.Root);
                Check(scene.OverlayHost.Visibility == Visibility.Collapsed, $"{name}: overlay host collapsed after close");
                scene.Strip.Render(scene.Context, new(OpenCodePersistentPtyInventoryPhase.Ready, ImmutableArray<PersistentPtyInfo>.Empty));
                await Task.Delay(100); await Settle(scene.Root);
                await Capture(scene.Card, $"{name}-session-card-empty.png");
            }
            Program.Result = _log.Any(line => line.StartsWith("FAIL", StringComparison.Ordinal)) ? 1 : 0;
        }
        catch (Exception error) { _log.Add("FAIL " + error); Program.Result = 1; }
        File.WriteAllText(Path.Combine(Program.Evidence, $"result-{Program.Theme}.json"), JsonSerializer.Serialize(new { result = Program.Result, checks = _log }, new JsonSerializerOptions { WriteIndented = true }));
        _window?.Close(); Exit();
    }

    private void Check(bool ok, string label) => _log.Add((ok ? "PASS " : "FAIL ") + label);

    private static async Task Settle(FrameworkElement root)
    {
        root.UpdateLayout();
        for (var i = 0; i < 3; i++) { await Task.Delay(60); root.UpdateLayout(); }
    }

    private async Task Capture(FrameworkElement element, string file)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var buffer = await bitmap.GetPixelsAsync();
        using var reader = DataReader.FromBuffer(buffer); var bytes = new byte[buffer.Length]; reader.ReadBytes(bytes);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync();
        stream.Seek(0); using var png = new DataReader(stream); await png.LoadAsync((uint)stream.Size);
        var data = new byte[(int)stream.Size]; png.ReadBytes(data);
        File.WriteAllBytes(Path.Combine(Program.Evidence, file), data);
        _log.Add($"CAPTURE {file} {bitmap.PixelWidth}x{bitmap.PixelHeight}");
    }

    private async Task TryDesktop(string file)
    {
        try { await DesktopCapture.SaveAsync(_window!, Path.Combine(Program.Evidence, file)); _log.Add("CAPTURE " + file); }
        catch (Exception error) { _log.Add($"SKIP {file}: {error.Message}"); }
    }

    internal static T? Find<T>(DependencyObject node, string automationId) where T : DependencyObject
    {
        if (node is T match && AutomationProperties.GetAutomationId(node) == automationId) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (Find<T>(VisualTreeHelper.GetChild(node, i), automationId) is { } found) return found;
        return null;
    }

    private sealed class Scene
    {
        internal Grid Root { get; } = new() { Width = PageWidth, Height = PageHeight };
        internal Border Card { get; }
        internal OpenCodePersistentPtyInventoryView Strip { get; } = new();
        internal OpenCodePersistentPtyOverlay Overlay { get; } = new();
        internal Grid OverlayHost { get; } = new() { HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        internal OpenCodePersistentPtyInventoryContext Context { get; } = new(Guid.NewGuid(), 1, "ses_fixture");
        private OpenCodePersistentPtyView? _view;
        internal OpenCodePersistentPtyViewContext? Context2 { get; private set; }

        internal void RenderState(OpenCodePersistentPtyPhase phase, OpenCodePersistentPtyRole role, bool authorized, string? notice = null) =>
            _view?.Render(new(Context2!, "dev server", "/home/ming/projects/aifrontend", phase, role, role == OpenCodePersistentPtyRole.Controller ? "你" : "OpenCode", true, authorized, notice));

        internal Scene(ElementTheme theme)
        {
            Root.RequestedTheme = theme;
            Root.Background = Brush("SolidBackgroundFillColorBaseBrush");
            Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TabsHeight) });
            Root.RowDefinitions.Add(new RowDefinition());
            Root.Children.Add(Tabs());

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
            body.ColumnDefinitions.Add(new ColumnDefinition());
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(332) });
            Grid.SetRow(body, 1); Root.Children.Add(body);
            body.Children.Add(LeftPane());
            var conversation = Conversation(); Grid.SetColumn(conversation, 1); body.Children.Add(conversation);

            Card = SessionCard();
            var dock = new Grid { Padding = new Thickness(8, 12, 24, 16), Children = { Card } };
            Grid.SetColumn(dock, 2); body.Children.Add(dock);

            Grid.SetRow(OverlayHost, 1); OverlayHost.Children.Add(Overlay); Root.Children.Add(OverlayHost);
            Overlay.CloseRequested += (_, _) => Close();

            Strip.Render(Context, new(OpenCodePersistentPtyInventoryPhase.Ready, ImmutableArray.Create(
                Info("pty_build", "dev server", "pnpm", ["dev"], "running", "node"),
                Info("pty_watch", "tsc --watch", "pnpm", ["exec", "tsc", "--watch"], "running", null),
                Info("pty_test", "unit tests", "dotnet", ["test"], "exited", null))));
        }

        internal void ShowStrip() => typeof(OpenCodePersistentPtyInventoryView).GetMethod("SetExpanded", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Strip, [true]);

        internal async Task OpenAsync(string id)
        {
            var context = new OpenCodePersistentPtyViewContext(Guid.NewGuid(), 1, Context.SessionId, id);
            _view = new OpenCodePersistentPtyView();
            _view.DetachRequested += _ => Close();
            _view.TakeControlRequested += _ => { }; _view.ReleaseControlRequested += _ => { }; _view.ReconnectRequested += _ => { };
            Context2 = context;
            _view.Render(new(context, "dev server", "/home/ming/projects/aifrontend", OpenCodePersistentPtyPhase.Live, OpenCodePersistentPtyRole.Observer, "OpenCode", true, false));
            Overlay.Content = _view; Strip.SetOpen(id);
            OverlayHost.Visibility = Visibility.Visible;
            var placement = OpenCodeBrowserOverlayLayout.Place(PageWidth, PageHeight - TabsHeight);
            OverlayHost.Width = placement.HostWidth; Overlay.SetSurfaceSize(placement.Width, placement.Height);
            try
            {
                if (await _view.Renderer.InitializeAsync(context, 120, 32))
                    await _view.Renderer.WriteAsync(context, Encoding.UTF8.GetBytes(
                        "\x1b[32m❯\x1b[0m pnpm dev\r\n\r\n  \x1b[1;32mVITE\x1b[0m v6.2.0  ready in 412 ms\r\n\r\n  ➜  Local:   \x1b[36mhttp://localhost:5173/\x1b[0m\r\n  ➜  Network: use --host to expose\r\n"));
            }
            catch { }
        }

        internal void Close()
        {
            if (_view is { } view) { if (ReferenceEquals(Overlay.Content, view)) Overlay.Content = null; view.Renderer.Dispose(); _view = null; }
            Strip.SetOpen(null);
            OverlayHost.Visibility = Visibility.Collapsed;
        }

        private static PersistentPtyInfo Info(string id, string title, string command, string[] args, string status, string? foreground) =>
            new(id, title, command, [.. args], "/home/ming/projects/aifrontend", status, 4242, status == "exited" ? 0 : null, "ses_fixture", foreground,
                new PersistentPtySize(120, 32), new PersistentPtyOutput(0, 0));

        private static FrameworkElement Tabs()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Padding = new Thickness(12, 6, 12, 0) };
            row.Children.Add(new Border { CornerRadius = new CornerRadius(8, 8, 0, 0), Padding = new Thickness(14, 8, 14, 8), Background = Brush("LayerFillColorDefaultBrush"),
                Child = new TextBlock { Text = "重构右侧面板", FontSize = 12 } });
            row.Children.Add(new Border { Padding = new Thickness(14, 8, 14, 8), Child = new TextBlock { Text = "修复登录", FontSize = 12, Foreground = Brush("TextFillColorSecondaryBrush") } });
            return row;
        }

        private static FrameworkElement LeftPane()
        {
            var tree = new StackPanel { Spacing = 2, Padding = new Thickness(16, 16, 8, 16) };
            tree.Children.Add(new TextBlock { Text = "代理", Style = (Style)Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 0, 0, 8) });
            foreach (var (label, depth) in new[] { ("build", 0), ("explore · 右侧面板", 1), ("fixer · 终端浮窗", 1), ("designer", 1) })
                tree.Children.Add(new TextBlock { Text = label, Margin = new Thickness(12 + depth * 16, 6, 0, 6), FontSize = 13 });
            return new Grid { Padding = new Thickness(24, 12, 8, 16), Children = { new Border { Style = DockCard(), Child = tree } } };
        }

        private static FrameworkElement Conversation()
        {
            var stack = new StackPanel { Spacing = 16, Padding = new Thickness(32, 24, 32, 24), MaxWidth = 720 };
            stack.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Right, CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10),
                Background = Brush("CardBackgroundFillColorDefaultBrush"), Child = new TextBlock { Text = "把长期终端放到右侧", TextWrapping = TextWrapping.Wrap } });
            stack.Children.Add(new TextBlock { Text = "已在右侧会话卡片加入终端入口。", TextWrapping = TextWrapping.Wrap });
            var composer = new Border { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(32, 0, 32, 16), Height = 96, CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1), BorderBrush = Brush("CardStrokeColorDefaultBrush"), Background = Brush("CardBackgroundFillColorDefaultBrush") };
            return new Grid { Background = Brush("LayerFillColorDefaultBrush"), Children = { stack, composer } };
        }

        private Border SessionCard()
        {
            var content = new StackPanel { Spacing = 16, Padding = new Thickness(16) };
            var header = new StackPanel { Spacing = 4 };
            header.Children.Add(new TextBlock { Text = "重构右侧面板", Style = (Style)Current.Resources["BodyStrongTextBlockStyle"] });
            header.Children.Add(new TextBlock { Text = "ses_fixture", Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CaptionSubtleStyleKey) });
            var context = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
            var label = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
            label.Children.Add(new TextBlock { Text = "上下文", Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CaptionSubtleStyleKey) });
            var pct = new TextBlock { Text = "38%", Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CaptionSubtleStyleKey) }; Grid.SetColumn(pct, 1); label.Children.Add(pct);
            context.Children.Add(label); context.Children.Add(new ProgressBar { Value = 38 });
            header.Children.Add(context);
            content.Children.Add(header);

            // Mirrors OpenCodePage.xaml WslQuickAccessSection (divider → Auto,Auto,Auto,* row → strip slots).
            var section = new StackPanel();
            section.Children.Add(new Border { Height = 1, Background = Brush("DividerStrokeColorDefaultBrush") });
            var row = new Grid { ColumnSpacing = 4, Margin = new Thickness(0, 12, 0, 0) };
            foreach (var width in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) }) row.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            var wsl = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            wsl.Children.Add(Quick("\uE838", 40));
            wsl.Children.Add(new SplitButton { Width = 72, Height = 36, Padding = new Thickness(0), Content = new FontIcon { Glyph = "\uE943", FontSize = 18 } });
            row.Children.Add(wsl);
            var browser = new ToggleButton { Width = 40, Height = 36, Padding = new Thickness(0), Content = new FontIcon { Glyph = "\uE774", FontSize = 18 } };
            browser.Style = Strip.Button.Style;
            Grid.SetColumn(browser, 1); row.Children.Add(browser);
            Grid.SetColumn(Strip.Button, 2); row.Children.Add(Strip.Button);
            section.Children.Add(row);
            section.Children.Add(new Grid { Children = { Strip } });
            content.Children.Add(section);

            content.Children.Add(new Border { Height = 1, Background = Brush("DividerStrokeColorDefaultBrush") });
            var todo = new StackPanel { Spacing = 8 };
            todo.Children.Add(new TextBlock { Text = "待办", Style = (Style)Current.Resources["BodyStrongTextBlockStyle"] });
            foreach (var item in new[] { "移动终端入口", "终端浮窗", "更新测试" })
                todo.Children.Add(new CheckBox { Content = item, IsChecked = item != "更新测试", MinHeight = 28 });
            content.Children.Add(todo);

            var card = new Border { Style = DockCard(), VerticalAlignment = VerticalAlignment.Top, Child = content, Translation = new System.Numerics.Vector3(0, 0, 16), Shadow = new ThemeShadow() };
            return card;
        }

        private static Button Quick(string glyph, double width) => new()
        {
            Width = width, Height = 36, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon { Glyph = glyph, FontSize = 18 },
        };

        private static Style DockCard()
        {
            var style = new Style(typeof(Border));
            style.Setters.Add(new Setter(Border.BackgroundProperty, Brush("SolidBackgroundFillColorQuarternaryBrush")));
            style.Setters.Add(new Setter(Border.BorderBrushProperty, Brush("CardStrokeColorDefaultBrush")));
            style.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(8)));
            return style;
        }

        private static Brush Brush(string key) => (Brush)Current.Resources[key];
    }
}

internal static class DesktopCapture
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Header { internal uint Size; internal int Width, Height; internal ushort Planes, BitCount; internal uint Compression, SizeImage; internal int X, Y; internal uint Used, Important; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint hwnd, nint dc, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, byte[] pixels, ref Header info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint value);

    internal static async Task SaveAsync(Window window, string path)
    {
        window.Activate(); await Task.Delay(150);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (!GetWindowRect(hwnd, out var rect)) throw new InvalidOperationException("window bounds unavailable");
        var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
        var dc = GetDC(hwnd); var memory = CreateCompatibleDC(dc); var bitmap = CreateCompatibleBitmap(dc, width, height); var previous = SelectObject(memory, bitmap);
        try
        {
            if (!PrintWindow(hwnd, memory, 2)) throw new InvalidOperationException("PrintWindow failed");
            var pixels = new byte[checked(width * height * 4)];
            var header = new Header { Size = (uint)Marshal.SizeOf<Header>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
            if (GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref header, 0) == 0) throw new InvalidOperationException("pixels unavailable");
            var visible = 0; for (var i = 0; i < pixels.Length; i += 4) if ((pixels[i] | pixels[i + 1] | pixels[i + 2]) != 0) visible++;
            if (visible < width * height / 4) throw new InvalidOperationException("desktop capture blank (session locked?)");
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, pixels); await encoder.FlushAsync();
            stream.Seek(0); using var reader = new DataReader(stream); await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[checked((int)stream.Size)]; reader.ReadBytes(bytes); File.WriteAllBytes(path, bytes);
        }
        finally { SelectObject(memory, previous); DeleteObject(bitmap); DeleteDC(memory); ReleaseDC(hwnd, dc); }
    }
}
