using System;
using System.Linq;
using FluentCode.Services.OpenCode.Browser;

internal static class PreviewTableTests
{
    internal static int Run(Func<string, string> read)
    {
        var checks = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
        void Invalid(string source)
        {
            try { OpenCodeBrowserPreviewPolicy.ParseTable(source, ','); }
            catch (FormatException) { checks++; return; }
            throw new Exception("malformed CSV must not render as a table");
        }

        var csv = OpenCodeBrowserPreviewPolicy.ParseTable("name,value\r\n\"a,b\",\"line 1\nline 2\"\r\n\"\"\"quoted\"\"\",", ',');
        Check(csv.Rows.Count == 3 && csv.Rows[0].SequenceEqual(new[] { "name", "value" }), "CSV retains the first row as the header");
        Check(csv.Rows[1].SequenceEqual(new[] { "a,b", "line 1\nline 2" }) && csv.Rows[2].SequenceEqual(new[] { "\"quoted\"", "" }), "quoted delimiters, newlines, escaped quotes and empty values remain literal copy text");
        var tsv = OpenCodeBrowserPreviewPolicy.ParseTable("name\tvalue\n\"a\tb\"\t\"x\ny\"", '\t');
        Check(tsv.Rows[1].SequenceEqual(new[] { "a\tb", "x\ny" }), "TSV uses the same quoted-field behavior");

        var rows = OpenCodeBrowserPreviewPolicy.ParseTable(string.Join('\n', Enumerable.Range(0, 503).Select(index => "row" + index)), ',');
        Check(rows.Rows.Count == 500 && rows.RemainingRows == 3 && !rows.CellsTruncated, "500-row bound counts the header and reports only the remaining rows");
        Check(rows.Rows[0][0] == "row0" && rows.Rows[^1][0] == "row499" && rows.Rows.Skip(1).Count() == 499, "header-first preview never adds a 501st retained row");
        var exactRows = OpenCodeBrowserPreviewPolicy.ParseTable(string.Join('\n', Enumerable.Repeat("cell", 500)), ',');
        Check(exactRows.Rows.Count == 500 && exactRows.RemainingRows == 0, "exactly 500 rows need no remaining-row notice");

        var columns = OpenCodeBrowserPreviewPolicy.ParseTable(string.Join(',', Enumerable.Range(0, 65)) + "\nshort", ',');
        Check(columns.Rows[0].Length == 64 && columns.Rows[0][63] == "63" && columns.CellsTruncated, "64-column bound retains the original values and reports truncation");
        Check(columns.Rows[1].SequenceEqual(new[] { "short" }), "ragged rows stay ragged for blank-cell projection");
        var exactColumns = OpenCodeBrowserPreviewPolicy.ParseTable(string.Join(',', Enumerable.Range(0, 64)), ',');
        Check(exactColumns.Rows[0].Length == 64 && !exactColumns.CellsTruncated, "exactly 64 columns are not truncated");

        var exactCell = new string('x', 16384);
        var cells = OpenCodeBrowserPreviewPolicy.ParseTable(exactCell + "," + exactCell + "y", ',');
        Check(cells.Rows[0].SequenceEqual(new[] { exactCell, exactCell }) && cells.CellsTruncated, "16,384-character bound preserves the retained copy text and reports overflow");
        Check(!OpenCodeBrowserPreviewPolicy.ParseTable("\"" + exactCell + "\"", ',').CellsTruncated, "exactly 16,384 quoted characters are not truncated");
        var empty = OpenCodeBrowserPreviewPolicy.ParseTable("", ',');
        Check(empty.Rows.Count == 0 && empty.RemainingRows == 0 && !empty.CellsTruncated, "empty files remain empty tables");
        Check(OpenCodeBrowserPreviewPolicy.ParseTable("header", ',').Rows.Count == 1, "header-only files have no invented data row");
        Invalid("\"unfinished"); Invalid("\"quoted\"suffix"); Invalid("unescaped\"quote");

        var preview = read("Pages/OpenCode/OpenCodeBrowserPreviewView.cs");
        Check(preview.Contains("new TableView") && preview.Contains("AutoGenerateColumns = false, IsReadOnly = true") && !preview.Contains("Grid Row(string[] values"), "STATIC CSV uses the maintained read-only TableView, not hand-built cell grids");
        Check(preview.Contains("ParseTable(text, delimiter)") && preview.Contains("table.ItemsSource = parsed.Rows.Skip(1).ToArray()") && preview.Contains("parsed.Rows.Max(row => row.Length)"), "STATIC table consumes the unchanged parser defaults, first-row headers and all retained ragged columns");
        Check(preview.Contains("Width = new GridLength(180)") && preview.Contains("CanResizeColumns = true, CanSortColumns = true, CanFilterColumns = false"), "STATIC original starting column width remains resizable with native sorting");
        Check(preview.Contains("host.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) })") && !preview.Contains("return Scroll(stack);"), "STATIC native table has a finite viewport rather than an outer scrolling stack");
        Check(preview.Contains("IsTextSelectionEnabled = true") && preview.Contains("ContentControl.ContentTemplateProperty") && preview.Contains("IsTextSelectionEnabled=\\\"True\\\""), "STATIC cell and header text retain substring selection");
        Check(preview.Contains("GetClipboardContent(object? dataItem) => CellText(dataItem)") && preview.Contains("index < values.Length ? values[index] : \"\""), "STATIC native clipboard values are the displayed strings, including blank ragged cells");
        Check(preview.Contains("table.CopyToClipboard += TableCopyToClipboard") && preview.Contains("args.Handled = true") && preview.Contains("OpenCodeMessageContextMenu.Copy(label.SelectedText)"), "STATIC selected text takes precedence over whole-cell grid copy");
        Check(preview.Contains("if (cell.Content is TextBlock label) label.Text = CellText(dataItem)") && preview.Contains("else cell.Content = GenerateElement(cell, dataItem)"), "STATIC column refresh handles initially empty and recycled cells");
        Check(preview.Contains("AppFonts.Apply(table, AppFontSlot.Body)") && preview.Contains("AppFonts.Apply(label, AppFontSlot.Body)") && preview.Contains("ThemeResource AppBodyFontFamily"), "STATIC table and header text use the existing live body-font contract");
        Check(preview.Contains("最多显示 500 行") && preview.Contains("表格最多显示 64 列；每格最多 16 384 字符。完整内容请切换源码。"), "STATIC bounded-preview notices still direct users to the full source");

        var overlay = read("Pages/OpenCode/OpenCodeBrowserOverlay.cs");
        Check(overlay.Contains("AppFonts.Apply(_errorCode, AppFontSlot.Monospace)") && !overlay.Contains("new(\"Cascadia Mono\")"), "STATIC browser error codes follow the user's monospace font");
        foreach (var file in new[] { "OpenCodeBrowserOverlay.cs", "OpenCodeBrowserPreviewView.cs", "OpenCodeBrowserExternalView.cs", "OpenCodeBrowserSelection.cs", "OpenCodeBrowserButton.cs", "OpenCodeBrowserColorIcon.cs" })
            Check(!read("Pages/OpenCode/" + file).Contains("Segoe Fluent Icons"), "STATIC FontIcon uses its native theme font in " + file);
        var external = read("Pages/OpenCode/OpenCodeBrowserExternalView.cs");
        var selected = read("Pages/OpenCode/OpenCodeBrowserSelectedList.cs");
        Check(overlay.Contains("OpenCodeDividerSurfaceStyle") && overlay.Contains("OpenCodeSubtleSurfaceStyle") && external.Contains("OpenCodeCardChromeStyle") && selected.Contains("OpenCodeSubtleCardChromeStyle") && selected.Contains("OpenCodeCardChromeStyle"), "STATIC browser chrome reuses existing divider and card resources");
        Check(external.Contains("CaptionSubtleTextBlockStyle") && selected.Contains("CaptionSubtleTextBlockStyle") && selected.Contains("OpenCodeTransparentToolButtonStyle"), "STATIC captions and quiet buttons reuse the shared style contract");
        return checks;
    }
}
