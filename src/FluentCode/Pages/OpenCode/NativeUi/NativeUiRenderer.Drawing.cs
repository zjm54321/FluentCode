// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using FluentCode.Services.NativeUi;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SkiaSharp;
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
    private void Paint(Shape shape, NativeUiElement node, bool filled)
    {
        shape.Fill = P(node, "fill") is not null ? ComponentBrush(S(node, "fill")) : filled && P(node, "stroke") is null ? ComponentBrush("ink") : null;
        shape.Stroke = P(node, "stroke") is not null ? ComponentBrush(S(node, "stroke")) : !filled ? ComponentBrush("ink") : null;
        shape.StrokeThickness = Math.Clamp(N(node, "strokeWidth", filled ? 1 : 2), 0, 1000); shape.Opacity = Math.Clamp(N(node, "opacity", 1), 0, 1); shape.StrokeLineJoin = PenLineJoin.Round; shape.StrokeStartLineCap = shape.StrokeEndLineCap = PenLineCap.Round;
    }
    private static Geometry? NumericPath(string data, out Size extent)
    {
        extent = default;
        if (data.Length >= 20000 || !Regex.IsMatch(data, @"^[MmLlHhVvCcSsQqTtAaZz\d\s,.eE+-]*$")) return null;
        using var parsed = SKPath.ParseSvgPathData(data);
        if (parsed is null) return null;
        var bounds = parsed.Bounds;
        if (!float.IsFinite(bounds.Right) || !float.IsFinite(bounds.Bottom)) return null;
        extent = new(Math.Clamp(bounds.Right, 0, 20000), Math.Clamp(bounds.Bottom, 0, 20000));
        var result = new PathGeometry { FillRule = FillRule.Nonzero }; PathFigure? figure = null;
        using var iterator = parsed.CreateRawIterator(); var points = new SKPoint[4]; var count = 0;
        static Point Convert(SKPoint point) => new(Math.Clamp(point.X, -20000, 20000), Math.Clamp(point.Y, -20000, 20000));
        for (var verb = iterator.Next(points); verb != SKPathVerb.Done; verb = iterator.Next(points))
        {
            if (++count > 4096 || points.Any(point => !float.IsFinite(point.X) || !float.IsFinite(point.Y))) return null;
            if (verb == SKPathVerb.Move) { figure = new PathFigure { StartPoint = Convert(points[0]), IsFilled = true }; result.Figures.Add(figure); }
            else if (figure is not null)
                switch (verb)
                {
                    case SKPathVerb.Line: figure.Segments.Add(new LineSegment { Point = Convert(points[1]) }); break;
                    case SKPathVerb.Quad: figure.Segments.Add(new QuadraticBezierSegment { Point1 = Convert(points[1]), Point2 = Convert(points[2]) }); break;
                    case SKPathVerb.Cubic: figure.Segments.Add(new BezierSegment { Point1 = Convert(points[1]), Point2 = Convert(points[2]), Point3 = Convert(points[3]) }); break;
                    case SKPathVerb.Conic:
                        var quads = SKPath.ConvertConicToQuads(points[0], points[1], points[2], iterator.ConicWeight(), 3);
                        for (var i = 1; i + 1 < quads.Length; i += 2) { if (++count > 4096) return null; figure.Segments.Add(new QuadraticBezierSegment { Point1 = Convert(quads[i]), Point2 = Convert(quads[i + 1]) }); }
                        break;
                    case SKPathVerb.Close: figure.IsClosed = true; break;
                }
        }
        return result;
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
                    path.Data = NumericPath(S(node, "d"), out var extent); path.Width = extent.Width; path.Height = extent.Height;
                    Paint(path, node, P(node, "fill") is not null);
                }; return pathVisual;
            case "Polyline": case "Polygon":
                Shape pointsShape = type == "Polygon" ? new Polygon() : new Polyline(); var pointsVisual = new Visual(pointsShape);
                pointsVisual.Apply = node => { var coordinates = Coordinates(S(node, "points")); var points = new PointCollection(); for (var i = 0; i + 1 < coordinates.Length; i += 2) points.Add(new(coordinates[i], coordinates[i + 1])); if (pointsShape is Polygon polygon) polygon.Points = points; else ((Polyline)pointsShape).Points = points; Paint(pointsShape, node, type == "Polygon"); }; return pointsVisual;
            case "SvgText":
                var svgText = NativeUiComponentTheme.Text(""); svgText.IsTextSelectionEnabled = false; var svgTextVisual = new Visual(svgText);
                svgTextVisual.Apply = node => { svgText.Text = S(node, "text"); svgText.FontSize = Math.Clamp(N(node, "size", 12), 1, 500); svgText.Foreground = ComponentBrush(S(node, "fill")); svgText.FontWeight = S(node, "weight") switch { "semibold" => FontWeights.SemiBold, "medium" => FontWeights.Medium, _ => FontWeights.Normal }; svgText.Measure(new(double.PositiveInfinity, double.PositiveInfinity)); var offset = S(node, "anchor") switch { "middle" => svgText.DesiredSize.Width / 2, "end" => svgText.DesiredSize.Width, _ => 0 }; Canvas.SetLeft(svgText, N(node, "x") - offset); Canvas.SetTop(svgText, N(node, "y") - svgText.FontSize); }; return svgTextVisual;
            default: return null;
        }
    }
}
