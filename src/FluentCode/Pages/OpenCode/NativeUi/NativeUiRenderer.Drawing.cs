// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using FluentCode.Services.NativeUi;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace FluentCode.Pages.OpenCode;

internal sealed partial class NativeUiRenderer
{
    private static double[] Coordinates(string value)
    {
        if (value.Length > 20000 || !Regex.IsMatch(value, @"^[\d\s,.eE+-]*$")) return [];
        var parts = value.Trim().Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var result = parts.Select(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : double.NaN).ToArray();
        return result.Any(double.IsNaN) ? [] : result;
    }
    private static Transform? DrawingTransform(string value)
    {
        if (value.Length == 0 || value.Length > 500) return null;
        var matches = Regex.Matches(value, @"\s*([a-zA-Z]+)\(([^()]*)\)\s*");
        if (string.Concat(matches.Select(match => match.Value)) != value) return null;
        var group = new TransformGroup();
        foreach (Match match in matches)
        {
            var n = Coordinates(match.Groups[2].Value);
            Transform? transform = match.Groups[1].Value switch
            {
                "translate" when n.Length is 1 or 2 => new TranslateTransform { X = n[0], Y = n.Length == 2 ? n[1] : 0 },
                "scale" when n.Length is 1 or 2 => new ScaleTransform { ScaleX = n[0], ScaleY = n.Length == 2 ? n[1] : n[0] },
                "rotate" when n.Length is 1 or 3 => new RotateTransform { Angle = n[0], CenterX = n.Length == 3 ? n[1] : 0, CenterY = n.Length == 3 ? n[2] : 0 },
                "skewX" when n.Length == 1 => new SkewTransform { AngleX = n[0] },
                "skewY" when n.Length == 1 => new SkewTransform { AngleY = n[0] },
                "matrix" when n.Length == 6 => new MatrixTransform { Matrix = new(n[0], n[1], n[2], n[3], n[4], n[5]) }, _ => null,
            };
            if (transform is null) return null; group.Children.Add(transform);
        }
        return group;
    }
    private static void Paint(Shape shape, NativeUiElement node, bool filled)
    {
        shape.Fill = P(node, "fill") is not null ? NativeUiComponentTheme.Brush(S(node, "fill")) : filled && P(node, "stroke") is null ? NativeUiComponentTheme.Brush("ink") : null;
        shape.Stroke = P(node, "stroke") is not null ? NativeUiComponentTheme.Brush(S(node, "stroke")) : !filled ? NativeUiComponentTheme.Brush("ink") : null;
        shape.StrokeThickness = Math.Clamp(N(node, "strokeWidth", filled ? 1 : 2), 0, 1000); shape.Opacity = Math.Clamp(N(node, "opacity", 1), 0, 1); shape.StrokeLineJoin = PenLineJoin.Round; shape.StrokeStartLineCap = shape.StrokeEndLineCap = PenLineCap.Round;
    }
    private Visual? CreateDrawing(string type, string key, int depth)
    {
        switch (type)
        {
            case "Svg":
                var canvas = new Canvas(); var viewport = new Canvas(); viewport.Children.Add(canvas); var viewbox = new Viewbox { Child = viewport, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left }; var svg = new Visual(viewbox);
                svg.Apply = node =>
                {
                    var bounds = Coordinates(S(node, "viewBox")); if (bounds.Length != 4 || bounds[2] <= 0 || bounds[3] <= 0) bounds = [0, 0, 100, 100];
                    viewport.Width = Math.Clamp(bounds[2], 1, 10000); viewport.Height = Math.Clamp(bounds[3], 1, 10000); canvas.Width = viewport.Width; canvas.Height = viewport.Height;
                    canvas.RenderTransform = new TranslateTransform { X = -bounds[0], Y = -bounds[1] }; viewport.Clip = new RectangleGeometry { Rect = new(0, 0, viewport.Width, viewport.Height) };
                    Sync(canvas, Children(P(node, "children"), key + "/children", depth + 1).Cast<UIElement>().ToArray());
                    NativeUiComponentTheme.Length(viewbox, P(node, "width")); NativeUiComponentTheme.Length(viewbox, P(node, "height"), true);
                    if (double.IsNaN(viewbox.Width)) { viewbox.Width = Math.Min(NativeUiTheme.MaximumWidth, Math.Max(280, bounds[2] * 1.5)); viewbox.MaxWidth = NativeUiTheme.MaximumWidth; }
                }; return svg;
            case "G":
                var group = new Canvas(); var groupVisual = new Visual(group);
                groupVisual.Apply = node => { Sync(group, Children(P(node, "children"), key + "/children", depth + 1).Cast<UIElement>().ToArray()); group.RenderTransform = DrawingTransform(S(node, "transform")); group.Opacity = Math.Clamp(N(node, "opacity", 1), 0, 1); }; return groupVisual;
            case "Circle": case "Ellipse":
                var ellipse = new Ellipse(); var ellipseVisual = new Visual(ellipse);
                ellipseVisual.Apply = node => { var rx = Math.Clamp(N(node, type == "Circle" ? "r" : "rx"), 0, 10000); var ry = Math.Clamp(N(node, type == "Circle" ? "r" : "ry"), 0, 10000); ellipse.Width = 2 * rx; ellipse.Height = 2 * ry; Canvas.SetLeft(ellipse, N(node, "cx") - rx); Canvas.SetTop(ellipse, N(node, "cy") - ry); Paint(ellipse, node, true); }; return ellipseVisual;
            case "Rect":
                var rectangle = new Rectangle(); var rectVisual = new Visual(rectangle);
                rectVisual.Apply = node => { rectangle.Width = Math.Clamp(N(node, "width"), 0, 10000); rectangle.Height = Math.Clamp(N(node, "height"), 0, 10000); Canvas.SetLeft(rectangle, N(node, "x")); Canvas.SetTop(rectangle, N(node, "y")); rectangle.RadiusX = rectangle.RadiusY = Math.Clamp(N(node, "rx"), 0, 10000); Paint(rectangle, node, true); }; return rectVisual;
            case "Line":
                var line = new Line(); var lineVisual = new Visual(line);
                lineVisual.Apply = node => { line.X1 = N(node, "x1"); line.Y1 = N(node, "y1"); line.X2 = N(node, "x2"); line.Y2 = N(node, "y2"); Paint(line, node, false); }; return lineVisual;
            case "Path":
                var path = new Path(); var pathVisual = new Visual(path);
                pathVisual.Apply = node =>
                {
                    var d = S(node, "d"); path.Data = null;
                    if (d.Length < 20000 && Regex.IsMatch(d, @"^[MmLlHhVvCcSsQqTtAaZz\d\s,.eE+-]*$"))
                        try { path.Data = ((Path)XamlReader.Load("<Path xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Data=\"" + d.Replace("\r", " ").Replace("\n", " ") + "\" />")).Data; } catch (Exception) { /* Malformed numeric path is empty, never executable markup. */ }
                    Paint(path, node, P(node, "fill") is not null);
                }; return pathVisual;
            case "Polyline": case "Polygon":
                Shape pointsShape = type == "Polygon" ? new Polygon() : new Polyline(); var pointsVisual = new Visual(pointsShape);
                pointsVisual.Apply = node => { var coordinates = Coordinates(S(node, "points")); var points = new PointCollection(); for (var i = 0; i + 1 < coordinates.Length; i += 2) points.Add(new(coordinates[i], coordinates[i + 1])); if (pointsShape is Polygon polygon) polygon.Points = points; else ((Polyline)pointsShape).Points = points; Paint(pointsShape, node, type == "Polygon"); }; return pointsVisual;
            case "SvgText":
                var svgText = NativeUiComponentTheme.Text(""); svgText.IsTextSelectionEnabled = false; var svgTextVisual = new Visual(svgText);
                svgTextVisual.Apply = node => { svgText.Text = S(node, "text"); svgText.FontSize = Math.Clamp(N(node, "size", 12), 1, 500); svgText.Foreground = NativeUiComponentTheme.Brush(S(node, "fill")); svgText.FontWeight = S(node, "weight") switch { "semibold" => FontWeights.SemiBold, "medium" => FontWeights.Medium, _ => FontWeights.Normal }; svgText.Measure(new(double.PositiveInfinity, double.PositiveInfinity)); var offset = S(node, "anchor") switch { "middle" => svgText.DesiredSize.Width / 2, "end" => svgText.DesiredSize.Width, _ => 0 }; Canvas.SetLeft(svgText, N(node, "x") - offset); Canvas.SetTop(svgText, N(node, "y") - svgText.FontSize); }; return svgTextVisual;
            default: return null;
        }
    }
}
