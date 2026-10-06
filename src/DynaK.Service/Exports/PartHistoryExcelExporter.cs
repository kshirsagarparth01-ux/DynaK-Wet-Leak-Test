using System.Globalization;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using DynaK.Service.Models;

namespace DynaK.Service.Exports;

public static class PartHistoryExcelExporter
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly string[] Headers =
    [
        "SR NO.",
        "Date",
        "Time",
        "Shift",
        "Part No.",
        "QR Code",
        "Leak Test Value",
        "Leak OK Range",
        "Result / Status"
    ];

    private static readonly double[] MinimumWidths = [16, 12, 10, 12, 18, 30, 15, 18, 16];
    private static readonly double[] MaximumWidths = [32, 14, 12, 18, 28, 60, 18, 24, 24];

    public static string CreateFileName(DateTimeOffset exportedAt) =>
        $"DynaK_Part_History_{exportedAt.ToLocalTime():yyyy-MM-dd_HHmm}.xlsx";

    public static byte[] CreateWorkbook(IReadOnlyList<LogicalPart> parts, DateTimeOffset exportedAt)
    {
        var rows = parts.Select(ExportRow.From).ToList();
        return CreateWorkbook(
            rows,
            "DYNAK WET LEAK TEST - PART HISTORY",
            $"Exported: {exportedAt.ToLocalTime():dd-MM-yyyy HH:mm}");
    }

    public static byte[] CreateDailyWorkbook(IReadOnlyList<ProductionRecord> records, DateOnly reportDate)
    {
        var rows = records.Select(ExportRow.From).ToList();
        return CreateWorkbook(
            rows,
            "DYNAK WET LEAK TEST - PRODUCTION REPORT",
            $"Report Date: {reportDate:dd-MM-yyyy}");
    }

    private static byte[] CreateWorkbook(IReadOnlyList<ExportRow> rows, string title, string subtitle)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, true))
        {
            var workbookPart = document.AddWorkbookPart();
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
            stylesPart.Stylesheet = CreateStylesheet();

            worksheetPart.Worksheet = CreateWorksheet(rows, title, subtitle);
            worksheetPart.Worksheet.Save();

            workbookPart.Workbook = new Workbook(new Sheets(
                new Sheet
                {
                    Id = workbookPart.GetIdOfPart(worksheetPart),
                    SheetId = 1U,
                    Name = "Part History"
                }));
            workbookPart.Workbook.Save();
        }

        var workbook = stream.ToArray();
        ValidateWorkbook(workbook, rows.Count);
        return workbook;
    }

    public static void ValidateWorkbook(byte[] workbook, int expectedRecordCount)
    {
        if (workbook.Length == 0)
        {
            throw new InvalidDataException("Excel export did not produce a workbook.");
        }

        using var stream = new MemoryStream(workbook, writable: false);
        using var document = SpreadsheetDocument.Open(stream, false);
        var validationErrors = new OpenXmlValidator().Validate(document).Take(3).ToList();
        if (validationErrors.Count > 0)
        {
            throw new InvalidDataException($"Excel export validation failed: {string.Join(" | ", validationErrors.Select(error => error.Description))}");
        }

        var workbookPart = document.WorkbookPart ?? throw new InvalidDataException("Excel export has no workbook part.");
        var sheets = workbookPart.Workbook.Sheets?.Elements<Sheet>().ToList() ?? [];
        if (sheets.Count != 1 || !string.Equals(sheets[0].Name?.Value, "Part History", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Excel export must contain one 'Part History' worksheet.");
        }

        var worksheetPart = workbookPart.GetPartById(sheets[0].Id!.Value!) as WorksheetPart
            ?? throw new InvalidDataException("Excel export worksheet relationship is missing.");
        var sheetData = worksheetPart.Worksheet.GetFirstChild<SheetData>()
            ?? throw new InvalidDataException("Excel export worksheet has no data.");
        var rows = sheetData.Elements<Row>().ToList();
        var headerRow = rows.SingleOrDefault(row => row.RowIndex?.Value == 3U)
            ?? throw new InvalidDataException("Excel export header row is missing.");
        var headers = headerRow.Elements<Cell>().Select(CellText).ToArray();
        if (!headers.SequenceEqual(Headers, StringComparer.Ordinal))
        {
            throw new InvalidDataException("Excel export headers do not match the production report contract.");
        }

        var exportedRows = rows.Count(row => row.RowIndex?.Value >= 4U);
        if (exportedRows != expectedRecordCount)
        {
            throw new InvalidDataException($"Excel export row count {exportedRows} does not match selected history record count {expectedRecordCount}.");
        }

        foreach (var cell in rows.SelectMany(row => row.Elements<Cell>()))
        {
            var value = cell.CellValue?.Text;
            if (string.Equals(value, "NaN", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "Infinity", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "-Infinity", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Excel export contains an invalid numeric value in {cell.CellReference?.Value ?? "an unnamed cell"}.");
            }
        }
    }

    private static Worksheet CreateWorksheet(IReadOnlyList<ExportRow> rows, string title, string subtitle)
    {
        var lastRow = rows.Count + 3;
        var sheetData = new SheetData(
            TitleRow(title, 1U, 24D, 1U),
            TitleRow(subtitle, 2U, 20D, 2U),
            HeaderRow());

        for (var index = 0; index < rows.Count; index++)
        {
            sheetData.Append(DataRow(rows[index], (uint)index + 4U));
        }

        return new Worksheet(
            new SheetViews(new SheetView(
                new Pane
                {
                    VerticalSplit = 3D,
                    TopLeftCell = "A4",
                    ActivePane = PaneValues.BottomLeft,
                    State = PaneStateValues.Frozen
                })
                { WorkbookViewId = 0U }),
            CreateColumns(rows),
            sheetData,
            new AutoFilter { Reference = $"A3:I{Math.Max(3, lastRow)}" },
            new MergeCells(
                new MergeCell { Reference = "A1:I1" },
                new MergeCell { Reference = "A2:I2" })
            { Count = 2U },
            new PageMargins
            {
                Left = 0.7D,
                Right = 0.7D,
                Top = 0.75D,
                Bottom = 0.75D,
                Header = 0.3D,
                Footer = 0.3D
            });
    }

    private static Row TitleRow(string value, uint rowIndex, double height, uint styleIndex) =>
        new(InlineStringCell($"A{rowIndex}", value, styleIndex))
        {
            RowIndex = rowIndex,
            Height = height,
            CustomHeight = true
        };

    private static Row HeaderRow()
    {
        var row = new Row { RowIndex = 3U, Height = 22D, CustomHeight = true };
        for (var index = 0; index < Headers.Length; index++)
        {
            row.Append(InlineStringCell(CellReference(index + 1, 3), Headers[index], 3U));
        }

        return row;
    }

    private static Columns CreateColumns(IReadOnlyList<ExportRow> rows)
    {
        var widths = MinimumWidths.ToArray();
        foreach (var row in rows)
        {
            var values = row.DisplayValues();
            for (var index = 0; index < values.Length; index++)
            {
                widths[index] = Math.Min(MaximumWidths[index], Math.Max(widths[index], values[index].Length + 2));
            }
        }

        var columns = new Columns();
        for (var index = 0; index < widths.Length; index++)
        {
            columns.Append(new Column
            {
                Min = (uint)index + 1U,
                Max = (uint)index + 1U,
                Width = widths[index],
                CustomWidth = true
            });
        }

        return columns;
    }

    private static Row DataRow(ExportRow row, uint rowIndex) =>
        new(
            InlineStringCell(CellReference(1, (int)rowIndex), row.SerialNumber, 7U),
            NumberCell(CellReference(2, (int)rowIndex), row.Date.ToDateTime(TimeOnly.MinValue).ToOADate(), 5U),
            NumberCell(CellReference(3, (int)rowIndex), row.Time.ToTimeSpan().TotalDays, 6U),
            InlineStringCell(CellReference(4, (int)rowIndex), row.Shift, 4U),
            InlineStringCell(CellReference(5, (int)rowIndex), row.PartNumber, 7U),
            InlineStringCell(CellReference(6, (int)rowIndex), row.QrCode, 7U),
            NumberOrBlankCell(CellReference(7, (int)rowIndex), row.LeakValue, 8U),
            InlineStringCell(CellReference(8, (int)rowIndex), row.LeakOkRange, 4U),
            InlineStringCell(CellReference(9, (int)rowIndex), row.Result, 4U))
        {
            RowIndex = rowIndex,
            Height = 20D,
            CustomHeight = true
        };

    private static Cell InlineStringCell(string reference, string value, uint styleIndex)
    {
        var text = new Text(SanitizeExcelText(value));
        if (NeedsPreservedWhitespace(text.Text))
        {
            text.Space = SpaceProcessingModeValues.Preserve;
        }

        return new Cell
        {
            CellReference = reference,
            StyleIndex = styleIndex,
            DataType = CellValues.InlineString,
            InlineString = new InlineString(text)
        };
    }

    private static Cell NumberCell(string reference, double value, uint styleIndex)
    {
        if (!double.IsFinite(value))
        {
            throw new InvalidDataException($"Excel export cannot write an invalid numeric value to {reference}.");
        }

        return new Cell
        {
            CellReference = reference,
            StyleIndex = styleIndex,
            CellValue = new CellValue(value.ToString("0.###############", CultureInfo.InvariantCulture))
        };
    }

    private static Cell NumberCell(string reference, decimal value, uint styleIndex) =>
        new()
        {
            CellReference = reference,
            StyleIndex = styleIndex,
            CellValue = new CellValue(value.ToString(CultureInfo.InvariantCulture))
        };

    private static Cell NumberOrBlankCell(string reference, decimal? value, uint styleIndex) =>
        value.HasValue
            ? NumberCell(reference, value.Value, styleIndex)
            : InlineStringCell(reference, "", styleIndex);

    private static Stylesheet CreateStylesheet() =>
        new(
            new NumberingFormats(
                new NumberingFormat { NumberFormatId = 164U, FormatCode = "dd-mm-yyyy" },
                new NumberingFormat { NumberFormatId = 165U, FormatCode = "hh:mm:ss" },
                new NumberingFormat { NumberFormatId = 166U, FormatCode = "0.0000" },
                new NumberingFormat { NumberFormatId = 167U, FormatCode = "0.0" })
            { Count = 4U },
            new Fonts(
                Font(11D, "FF111827"),
                Font(14D, "FF111827", bold: true),
                Font(11D, "FF374151"),
                Font(11D, "FFFFFFFF", bold: true))
            { Count = 4U },
            new Fills(
                new Fill(new PatternFill { PatternType = PatternValues.None }),
                new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
                new Fill(new PatternFill(
                    new ForegroundColor { Rgb = "FF1F2937" },
                    new BackgroundColor { Indexed = 64U })
                { PatternType = PatternValues.Solid }))
            { Count = 3U },
            new Borders(
                new Border(new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder()),
                new Border(
                    new LeftBorder(),
                    new RightBorder(),
                    new TopBorder(),
                    new BottomBorder(new Color { Rgb = "FFD1D5DB" }) { Style = BorderStyleValues.Thin },
                    new DiagonalBorder()))
            { Count = 2U },
            new CellStyleFormats(new CellFormat { NumberFormatId = 0U, FontId = 0U, FillId = 0U, BorderId = 0U }) { Count = 1U },
            new CellFormats(
                CellFormat(),
                CellFormat(fontId: 1U, applyFont: true),
                CellFormat(fontId: 2U, applyFont: true),
                CellFormat(fontId: 3U, fillId: 2U, borderId: 1U, center: true, applyFont: true, applyFill: true),
                CellFormat(borderId: 1U, center: true),
                CellFormat(numberFormatId: 164U, borderId: 1U, center: true),
                CellFormat(numberFormatId: 165U, borderId: 1U, center: true),
                CellFormat(borderId: 1U),
                CellFormat(numberFormatId: 166U, borderId: 1U, right: true),
                CellFormat(numberFormatId: 167U, borderId: 1U, right: true))
            { Count = 10U },
            new CellStyles(new CellStyle { Name = "Normal", FormatId = 0U, BuiltinId = 0U }) { Count = 1U },
            new DifferentialFormats { Count = 0U },
            new TableStyles { Count = 0U, DefaultTableStyle = "TableStyleMedium2", DefaultPivotStyle = "PivotStyleLight16" });

    private static Font Font(double size, string color, bool bold = false)
    {
        var font = new Font();
        if (bold)
        {
            font.Append(new Bold());
        }

        font.Append(new FontSize { Val = size }, new Color { Rgb = color }, new FontName { Val = "Calibri" });

        return font;
    }

    private static CellFormat CellFormat(
        uint numberFormatId = 0U,
        uint fontId = 0U,
        uint fillId = 0U,
        uint borderId = 0U,
        bool center = false,
        bool right = false,
        bool applyFont = false,
        bool applyFill = false)
    {
        var format = new CellFormat
        {
            NumberFormatId = numberFormatId,
            FontId = fontId,
            FillId = fillId,
            BorderId = borderId,
            FormatId = 0U,
            ApplyFont = applyFont,
            ApplyFill = applyFill,
            ApplyBorder = borderId != 0,
            ApplyNumberFormat = numberFormatId != 0
        };
        if (center || right || borderId != 0)
        {
            format.ApplyAlignment = true;
            format.Alignment = new Alignment
            {
                Horizontal = center ? HorizontalAlignmentValues.Center : right ? HorizontalAlignmentValues.Right : null,
                Vertical = VerticalAlignmentValues.Center
            };
        }

        return format;
    }

    private static string CellReference(int column, int row)
    {
        var letters = "";
        while (column > 0)
        {
            column--;
            letters = (char)('A' + column % 26) + letters;
            column /= 26;
        }

        return $"{letters}{row}";
    }

    private static string CellText(Cell cell) => cell.DataType?.Value == CellValues.InlineString
        ? cell.InlineString?.Text?.Text ?? ""
        : cell.CellValue?.Text ?? "";

    private static string SanitizeExcelText(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (XmlConvert.IsXmlChar(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static bool NeedsPreservedWhitespace(string value) =>
        value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]));

    private sealed record ExportRow(
        string SerialNumber,
        DateOnly Date,
        TimeOnly Time,
        string Shift,
        string PartNumber,
        string QrCode,
        decimal? LeakValue,
        string LeakOkRange,
        string Result)
    {
        public static ExportRow From(LogicalPart part)
        {
            var attempt = part.LatestAttempt;
            return new ExportRow(
                attempt.SerialNumber ?? "",
                attempt.Date,
                attempt.Time,
                attempt.Shift,
                attempt.PartNumber,
                attempt.QrCode,
                attempt.LeakTestValue,
                FormatLeakRange(attempt.LowerLimit, attempt.UpperLimit),
                ResultText(attempt, part));
        }

        public static ExportRow From(ProductionRecord record) =>
            new(
                record.SerialNumber ?? "",
                record.Date,
                record.Time,
                record.Shift,
                record.PartNumber,
                record.QrCode,
                record.LeakTestValue,
                FormatLeakRange(record.LowerLimit, record.UpperLimit),
                NormalizeResult(record.ResolvedResult));

        public string[] DisplayValues() =>
        [
            SerialNumber,
            Date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture),
            Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            Shift,
            PartNumber,
            QrCode,
            LeakValue?.ToString("0.0000", CultureInfo.InvariantCulture) ?? "",
            LeakOkRange,
            Result
        ];

        private static string FormatLeakRange(decimal minimum, decimal maximum) =>
            $"{minimum.ToString("0.00", CultureInfo.InvariantCulture)} TO {maximum.ToString("0.000", CultureInfo.InvariantCulture)}";

        private static string ResultText(ProductionRecord record, LogicalPart part)
        {
            var overallResult = NormalizeResult(part.OverallResult);
            if (overallResult.Length > 0)
            {
                return overallResult;
            }

            return NormalizeResult(record.ResolvedResult);
        }

        private static string NormalizeResult(string? value)
        {
            var normalized = value?.Trim().ToUpperInvariant() ?? "";
            return normalized switch
            {
                "OK" => "OK",
                "NG" => "NG",
                "REWORK" => "REWORK",
                "NG-REWORK" => "NG-REWORK",
                _ when normalized.StartsWith("NG ", StringComparison.Ordinal) || normalized.StartsWith("NG/", StringComparison.Ordinal) => "NG",
                _ => normalized
            };
        }

    }
}
