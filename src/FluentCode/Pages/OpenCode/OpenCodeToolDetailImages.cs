using System;
using System.Collections.Generic;
using System.Threading;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace FluentCode.Pages.OpenCode;

internal static partial class OpenCodeToolDetailContent
{
    internal static IReadOnlyList<OpenCodeToolImageSource> ToolImages(OpenCodeToolRowModel model)
        => OpenCodeToolImageResolver.Sources(model.State, model.ToolName, model.Target);

    /// <summary>No file I/O until this image section is realized; unload cancels both fetch and publication.</summary>
    internal static FrameworkElement BuildToolImages(OpenCodeToolRowModel model, OpenCodeToolDetailOptions options,
        bool thumbnail = false, Action? openLarge = null, Action<int, int>? dimensions = null)
    {
        var panel = new StackPanel { Spacing = 8 };
        AutomationProperties.SetAutomationId(panel, "OpenCodeToolImageSection");
        var sources = ToolImages(model);
        foreach (var source in sources)
        {
            panel.Children.Add(BuildImage(model.PartId, source, options, thumbnail, openLarge, dimensions,
                model.Kind == OpenCodeToolKind.Browser ? "OpenCodeBrowserScreenshot" : "OpenCodeToolImage"));
        }
        return panel;
    }

    /// <summary>Shared native renderer and session-byte cache for tool results and user attachments.</summary>
    internal static FrameworkElement BuildImage(string partId, OpenCodeToolImageSource source, OpenCodeToolDetailOptions options,
        bool thumbnail = false, Action? openLarge = null, Action<int, int>? dimensions = null, string automationId = "OpenCodeUserImage")
    {
        var panel = new StackPanel { Spacing = 4 };
        var image = new Image { Stretch = Stretch.Uniform, MaxHeight = thumbnail ? 240 : options.Compact ? 200 : double.PositiveInfinity,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(image, automationId);
        AutomationProperties.SetName(image, source.Name);
        var caption = Caption(source.Name + " · 正在读取图片…");
        var frame = new Border { Child = image, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
        OpenCodeToolTheme.Apply(frame, OpenCodeToolTheme.Border(OpenCodeToolTheme.SubtleSecondary, OpenCodeToolTheme.CardStroke));
        if (thumbnail && openLarge is not null)
        {
            var button = new Button { Content = frame, Padding = new Thickness(0), BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(button, "查看完整图片 · " + source.Name);
            button.Click += (_, _) => openLarge();
            panel.Children.Add(button);
        }
        else panel.Children.Add(frame);
        panel.Children.Add(caption);
        var lifetime = new OpenCodeImageRenderLifetime();
        frame.Loaded += async (_, _) =>
        {
            var scope = options.BrowserImageScope ?? OpenCodeImageContext.Find(frame);
            var (generation, token) = lifetime.Begin();
            bool Current() => lifetime.IsCurrent(generation, token) && scope is not { IsCurrent: false };
            try
            {
                var file = scope is not null ? await scope.ReadToolImageAsync(partId, source, token)
                    : source.Reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                        ? OpenCodeToolImageResolver.DecodeDataUrl(source.Reference)
                        : throw new InvalidOperationException("当前会话未提供图片读取方式");
                if (!Current()) return;
                using var stream = await OpenCodeBrowserCaptureImage.StreamAsync(file.Bytes);
                if (!Current()) return;
                var decoder = await BitmapDecoder.CreateAsync(stream);
                if (!Current()) return;
                var width = decoder.PixelWidth;
                var height = decoder.PixelHeight;
                var scale = Math.Min(1d, OpenCodeToolImageResolver.DecodeWidth / (double)Math.Max(width, height));
                stream.Seek(0);
                var bitmap = new BitmapImage { DecodePixelWidth = Math.Max(1, (int)(width * scale)), DecodePixelHeight = Math.Max(1, (int)(height * scale)) };
                await bitmap.SetSourceAsync(stream);
                if (!Current()) return;
                image.Source = bitmap;
                caption.Text = source.Name + " · " + width + " × " + height;
                dimensions?.Invoke((int)width, (int)height);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (Current()) caption.Text = source.Name + " · 无法读取图片：" + error.Message; }
        };
        frame.Unloaded += (_, _) => { lifetime.Cancel(); image.Source = null; };
        return panel;
    }
}
