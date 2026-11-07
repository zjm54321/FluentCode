// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FluentCode.Services.NativeUi;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using WinUI.TableView;

namespace FluentCode.Pages.OpenCode;

internal sealed partial class NativeUiRenderer
{
    private sealed class TableRow(int index, int columns)
    {
        internal int Index { get; } = index;
        internal object?[] Values { get; } = new object?[columns];
        internal Border[] Cells { get; } = Enumerable.Range(0, columns).Select(_ => new Border { Padding = new Thickness(NativeUiTheme.Gap(2)) }).ToArray();
    }
    private sealed class ComponentCell(TableRow row, int index) : IComparable
    {
        internal Border Element => row.Cells[index];
        internal object? Value => row.Values[index] is NativeUiElement node ? S(node, "text") : row.Values[index];
        public int CompareTo(object? other)
        {
            var value = other is ComponentCell cell ? cell.Value : other;
            return Value is double number && value is double second ? number.CompareTo(second) : StringComparer.CurrentCulture.Compare(NativeUiValues.Text(Value), NativeUiValues.Text(value));
        }
    }
    private sealed partial class ComponentColumn(int index) : TableViewColumn
    {
        public override FrameworkElement GenerateElement(TableViewCell cell, object? dataItem)
        {
            var content = dataItem is TableRow row ? row.Cells[index] : dataItem is ComponentCell component ? component.Element : null;
            if (content is null) return new TextBlock { Text = NativeUiValues.Text(dataItem) };
            Detach(content); return content;
        }
        public override FrameworkElement GenerateEditingElement(TableViewCell cell, object? dataItem) => GenerateElement(cell, dataItem);
        public override object? GetCellContent(object? dataItem) => dataItem is TableRow row ? new ComponentCell(row, index) : null;
        public override object? GetClipboardContent(object? dataItem) => GetCellContent(dataItem) is ComponentCell cell ? NativeUiValues.Text(cell.Value) : "";
    }
    private SKColor ChartColor(int index)
    {
        var token = new[] { "info", "success", "warning", "purple", "danger", "pink" }[index % 6];
        var brush = ComponentBrush(token) as SolidColorBrush;
        var color = brush?.Color ?? Microsoft.UI.Colors.Gray;
        return new SKColor(color.R, color.G, color.B, color.A);
    }
    private static string FormatChart(double value, NativeUiElement node)
    {
        if (!double.IsFinite(value)) return "—";
        var number = value.ToString("N2", CultureInfo.GetCultureInfo("en-US")).TrimEnd('0').TrimEnd('.');
        if (S(node, "format") == "percent") return number + "%";
        if (S(node, "format") != "currency") return number;
        var code = S(node, "currency").ToUpperInvariant();
        var symbol = code switch { "EUR" => "€", "GBP" => "£", "CNY" or "JPY" => "¥", "USD" or "" => "$", _ => code + " " };
        return symbol + number;
    }
    private Visual? CreateData(string type, string key, int depth)
    {
        if (type == "Table")
        {
            var table = new TableView { AutoGenerateColumns = false, IsReadOnly = true, CanResizeColumns = true, CanSortColumns = true, CanFilterColumns = false, MaxHeight = 480, MinHeight = 80, HorizontalAlignment = HorizontalAlignment.Stretch };
            var visual = new Visual(table); string signature = ""; TableRow[] currentRows = [];
            visual.Apply = node =>
            {
                var columns = (P(node, "columns") as IReadOnlyList<object?> ?? []).Select(NativeUiValues.Text).ToArray();
                var rows = (P(node, "rows") as IReadOnlyList<object?> ?? []).OfType<IReadOnlyList<object?>>().ToArray();
                var nextSignature = string.Join("\0", columns) + "/" + rows.Length;
                if (signature != nextSignature)
                {
                    table.Columns.Clear();
                    for (var c = 0; c < columns.Length; c++) table.Columns.Add(new ComponentColumn(c) { Header = columns[c], CanSort = true, CanFilter = false, IsReadOnly = true });
                    currentRows = rows.Select((_, i) => new TableRow(i, columns.Length)).ToArray();
                    signature = nextSignature;
                }
                for (var r = 0; r < rows.Length; r++) for (var c = 0; c < columns.Length; c++)
                {
                    var value = c < rows[r].Count ? rows[r][c] : null; var cell = currentRows[r].Cells[c]; currentRows[r].Values[c] = value;
                    FrameworkElement child;
                    if (value is NativeUiElement component) child = Node(component, key + "/rows/" + r + "/" + c, depth + 1);
                    else { var text = cell.Child as TextBlock ?? NativeUiComponentTheme.Text(""); text.Text = value is double number ? number.ToString("N2", CultureInfo.GetCultureInfo("en-US")).TrimEnd('0').TrimEnd('.') : NativeUiValues.Text(value); text.TextAlignment = value is double || double.TryParse(text.Text.Trim('$', '¥', '€', '£', '%').Replace(",", "", StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? TextAlignment.Right : TextAlignment.Left; child = text; }
                    if (!ReferenceEquals(cell.Child, child)) { Detach(child); cell.Child = child; }
                }
                if (!ReferenceEquals(table.ItemsSource, currentRows)) table.ItemsSource = currentRows;
            }; return visual;
        }
        if (type != "Chart") return null;
        var host = new Grid(); var chartVisual = new Visual(host); string previous = "";
        host.ActualThemeChanged += (_, _) => { previous = ""; if (!chartVisual.Disposed && chartVisual.Model.TypeName == "Chart") chartVisual.Apply(chartVisual.Model); };
        chartVisual.Apply = node =>
        {
            var signature = NativeUiValues.ToJson(node.Props)?.ToJsonString() ?? ""; if (signature == previous) return; previous = signature;
            var rows = (P(node, "data") as IReadOnlyList<object?> ?? []).OfType<IReadOnlyDictionary<string, object?>>().ToArray();
            host.Height = Math.Clamp(N(node, "height", 240), 140, 480);
            host.Children.Clear();
            if (rows.Length == 0) { host.Children.Add(NativeUiComponentTheme.Text("暂无图表数据", true)); return; }
            var kind = S(node, "type"); var first = rows[0]; var x = S(node, "x");
            if (x.Length == 0 || !first.ContainsKey(x)) x = first.FirstOrDefault(entry => kind == "scatter" ? entry.Value is double : entry.Value is string).Key ?? first.Keys.First();
            var series = (P(node, "series") as IReadOnlyList<object?>)?.Select(NativeUiValues.Text).ToArray();
            if (series is null || series.Length == 0) series = first.Where(entry => entry.Key != x && entry.Value is double).Select(entry => entry.Key).ToArray();
            series = series.Take(8).ToArray(); if (series.Length == 0) { host.Children.Add(NativeUiComponentTheme.Text("没有数值序列", true)); return; }
            double Value(IReadOnlyDictionary<string, object?> row, string name) { var value = NativeUiValues.Number(row.GetValueOrDefault(name)); return double.IsFinite(value) ? value : 0; }
            var textBrush = ComponentBrush("ink") as SolidColorBrush; var textColor = textBrush?.Color ?? Microsoft.UI.Colors.Gray;
            var labelsPaint = new SolidColorPaint(new SKColor(textColor.R, textColor.G, textColor.B));
            if (kind is "pie" or "donut")
            {
                var pie = new PieChart { AnimationsSpeed = TimeSpan.Zero, LegendPosition = LegendPosition.Bottom, LegendTextPaint = labelsPaint };
                pie.Series = rows.Select((row, i) => (ISeries)new PieSeries<double> { Name = NativeUiValues.Text(row.GetValueOrDefault(x)), Values = new[] { Math.Max(0, Value(row, series[0])) }, InnerRadius = kind == "donut" ? 55 : 0, Fill = new SolidColorPaint(ChartColor(i)), Stroke = null, DataLabelsFormatter = point => FormatChart(point.Coordinate.PrimaryValue, node), ToolTipLabelFormatter = point => FormatChart(point.Coordinate.PrimaryValue, node) }).ToArray();
                host.Children.Add(pie); return;
            }
            var chart = new CartesianChart { AnimationsSpeed = TimeSpan.Zero, LegendPosition = series.Length > 1 ? LegendPosition.Bottom : LegendPosition.Hidden, LegendTextPaint = labelsPaint };
            var plots = new List<ISeries>();
            for (var i = 0; i < series.Length; i++)
            {
                var name = series[i]; var values = rows.Select(row => Value(row, name)).ToArray(); var color = ChartColor(i);
                var stroke = new SolidColorPaint(color) { StrokeThickness = 2 }; var fill = new SolidColorPaint(color.WithAlpha(kind == "area" ? (byte)90 : (byte)255));
                ISeries plot = kind switch
                {
                    "scatter" => new ScatterSeries<ObservablePoint> { Name = name, Values = rows.Select(row => new ObservablePoint(Value(row, x), Value(row, name))).ToArray(), Fill = fill, Stroke = null, GeometrySize = 9, YToolTipLabelFormatter = point => FormatChart(point.Coordinate.PrimaryValue, node) },
                    "bar" when B(node, "stacked") => new StackedColumnSeries<double> { Name = name, Values = values, Fill = fill, Stroke = null, YToolTipLabelFormatter = point => FormatChart(point.Coordinate.PrimaryValue, node) },
                    "bar" => new ColumnSeries<double> { Name = name, Values = values, Fill = fill, Stroke = null, YToolTipLabelFormatter = point => FormatChart(point.Coordinate.PrimaryValue, node) },
                    "area" when B(node, "stacked") => new StackedAreaSeries<double> { Name = name, Values = values, Fill = fill, Stroke = stroke, GeometrySize = 0, YToolTipLabelFormatter = point => FormatChart(point.Coordinate.PrimaryValue, node) },
                    _ => new LineSeries<double> { Name = name, Values = values, Fill = kind == "area" ? fill : null, Stroke = stroke, GeometrySize = 6, GeometryFill = new SolidColorPaint(color), GeometryStroke = stroke, LineSmoothness = 0, YToolTipLabelFormatter = point => FormatChart(point.Coordinate.PrimaryValue, node) },
                };
                plots.Add(plot);
            }
            chart.Series = plots;
            chart.XAxes = new[] { new Axis { Labels = kind == "scatter" ? null : rows.Select(row => NativeUiValues.Text(row.GetValueOrDefault(x))).ToArray(), LabelsPaint = labelsPaint, TextSize = 12, MinStep = kind == "scatter" ? 0 : 1 } };
            chart.YAxes = new[] { new Axis { Labeler = value => FormatChart(value, node), LabelsPaint = labelsPaint, TextSize = 12 } };
            host.Children.Add(chart);
        }; return chartVisual;
    }
}
