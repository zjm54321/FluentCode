using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Net.Codecrete.QrCodeGenerator;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace FluentCode.Pages.Account;

/// <summary>
/// 配对链接的二维码：把模块矩阵画成一条矢量路径（任何缩放都清晰，相邻模块之间没有抗锯齿细缝）。
/// 始终黑码白底并带 4 模块静区——深色主题与高对比度下也不跟随主题色，否则扫码器读不出来。
/// 内容是凭据：不缓存、不导出位图，<see cref="Clear"/> 后不留任何几何数据。
/// </summary>
internal sealed class OpenCodePairQrCode : UserControl
{
    private const int QuietZone = 4;
    private const double TargetSize = 232;
    private readonly Path _path = new() { Fill = new SolidColorBrush(Colors.Black), Stretch = Stretch.None };
    private readonly Border _plate = new();

    internal OpenCodePairQrCode()
    {
        _plate.Background = new SolidColorBrush(Colors.White);
        _plate.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0));
        _plate.BorderThickness = new Thickness(1);
        _plate.CornerRadius = new CornerRadius(8);
        _plate.Child = _path;
        IsTabStop = false;
        HorizontalAlignment = HorizontalAlignment.Center;
        // 二维码方向固定，不随从右到左布局镜像。
        FlowDirection = FlowDirection.LeftToRight;
        UseLayoutRounding = true;
        Content = _plate;
        AutomationProperties.SetAutomationId(this, "OpenCodePairQrCode");
        AutomationProperties.SetName(this, "配对链接二维码");
        AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Content);
        Clear();
    }

    /// <summary>画出 text 的二维码。scale = XamlRoot.RasterizationScale：模块边长取整到物理像素，边缘不发虚。</summary>
    internal void Show(string text, double scale)
    {
        var qr = QrCode.EncodeText(text, QrCode.Ecc.Medium);
        if (!(scale > 0) || double.IsInfinity(scale)) scale = 1;
        var total = qr.Size + QuietZone * 2;
        var module = Math.Max(2, Math.Floor(TargetSize * scale / total)) / scale;
        var geometry = new PathGeometry { FillRule = FillRule.Nonzero };
        foreach (var polygon in qr.ToOutlines())
        {
            var vertices = polygon.Vertices;
            if (vertices.Count < 3) continue;
            var figure = new PathFigure { IsClosed = true, IsFilled = true, StartPoint = At(vertices[0], module) };
            for (var i = 1; i < vertices.Count; i++) figure.Segments.Add(new LineSegment { Point = At(vertices[i], module) });
            geometry.Figures.Add(figure);
        }

        _path.Data = geometry;
        _path.Width = _path.Height = qr.Size * module;
        _plate.Padding = new Thickness(QuietZone * module);
        Visibility = Visibility.Visible;
    }

    internal void Clear()
    {
        _path.Data = null;
        Visibility = Visibility.Collapsed;
    }

    private static Point At(QrPoint point, double module) => new(point.X * module, point.Y * module);
}
