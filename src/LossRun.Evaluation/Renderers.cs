using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using LossRun.Contracts;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using static LossRun.Evaluation.TruthFactory;

namespace LossRun.Evaluation;

/// <summary>A rendered document and what its layout prints.</summary>
public sealed record Rendered(byte[] Bytes, string Extension, int Pages, PrintedFields Printed);

/// <summary>
/// Writes truth documents in five fictitious carrier layouts (brief 08). PDF layouts paginate with continuation
/// headers; everything is synthetic. The Synthetic Mutual layout matches the hand-written spec (seen template).
/// </summary>
public static class Renderers
{
    private static readonly string[] MoneyTargets = ["indemnity.paid", "indemnity.reserve", "medical.paid", "medical.reserve", "expense.paid"];

    public static Rendered Render(CarrierProfile carrier, LossRunDocument d) => carrier.Layout switch
    {
        "landscape-blocks" => LandscapeBlocks(d),
        "portrait-codes" => PortraitCodes(d),
        "narrative" => Narrative(d),
        "xlsx-stacked" => XlsxStacked(d),
        "csv-wide" => CsvWide(d),
        "two-line-records" => TwoLineRecords(d),
        "claim-per-table" => ClaimPerTable(d),
        "stacked-columns" => StackedColumns(d),
        "csv-composite" => CsvComposite(d),
        "measures-rows" => MeasuresRows(d),
        _ => throw new ArgumentOutOfRangeException(nameof(carrier), carrier.Layout, null),
    };

    // ------------------------------------------------------------------ paged PDF canvas

    private sealed class Canvas : IDisposable
    {
        private readonly PdfDocument _pdf = new();
        private readonly double _width, _height;
        private XGraphics? _g;
        public double Y { get; set; }
        public double Left { get; } = 36;
        public double Bottom => _height - 46;
        private int? _savedPages;
        public int Pages => _savedPages ?? _pdf.PageCount;
        public XGraphics G => _g!;

        public Canvas(double widthIn, double heightIn, string title)
        {
            (_width, _height) = (widthIn * 72, heightIn * 72);
            _pdf.Info.Title = title;
            NewPage();
        }

        public void NewPage()
        {
            _g?.Dispose();
            var page = _pdf.AddPage();
            page.Width = XUnit.FromPoint(_width);
            page.Height = XUnit.FromPoint(_height);
            _g = XGraphics.FromPdfPage(page);
            Y = 40;
        }

        public bool Fits(double height) => Y + height <= Bottom;

        public void Text(string text, XFont font, double x, double? y = null, XBrush? brush = null) => G.DrawString(text, font, brush ?? XBrushes.Black, x, y ?? Y);

        public byte[] Save(string footer)
        {
            _g?.Dispose();
            _g = null;
            var font = new XFont("Arial", 7, XFontStyleEx.Regular);
            for (var i = 0; i < _pdf.PageCount; i++)
            {
                using var g = XGraphics.FromPdfPage(_pdf.Pages[i], XGraphicsPdfPageOptions.Append);
                g.DrawString($"{footer}   Page {i + 1} of {_pdf.PageCount}", font, XBrushes.Gray, Left, _height - 24);
            }

            _savedPages = _pdf.PageCount;
            using var stream = new MemoryStream();
            _pdf.Save(stream, closeStream: false);
            return Deterministic.Pdf(stream.ToArray());
        }

        public void Dispose()
        {
            _g?.Dispose();
            _pdf.Dispose();
        }
    }

    private static double Row(Canvas c, string[] cells, double[] widths, XFont font, int textColumns, double height, bool header = false)
    {
        var x = c.Left;
        for (var i = 0; i < cells.Length; i++)
        {
            var rect = new XRect(x, c.Y, widths[i], height);
            if (header)
            {
                c.G.DrawRectangle(XBrushes.Gainsboro, rect);
            }

            c.G.DrawRectangle(XPens.DarkGray, rect);
            var numeric = i >= textColumns && !header;
            var spansText = i == 0 && cells.Length > 1 && cells[1] == "" && cells[0].Length > 12;
            if (spansText)
            {
                c.G.DrawString(cells[0], font, XBrushes.Black, new XRect(x + 3, c.Y, widths.Take(textColumns).Sum() - 6, height), XStringFormats.CenterLeft);
            }
            else if (!(i > 0 && i < textColumns && cells[0].Length > 12 && cells[1] == ""))
            {
                c.G.DrawString(cells[i], font, XBrushes.Black, new XRect(x + 3, c.Y, widths[i] - 6, height), numeric ? XStringFormats.CenterRight : XStringFormats.CenterLeft);
            }

            x += widths[i];
        }

        c.Y += height;
        return c.Y;
    }

    // ------------------------------------------------------------------ C1: landscape blocks (matches synthetic-mutual-pdf)

    private static Rendered LandscapeBlocks(LossRunDocument d)
    {
        using var c = new Canvas(11, 8.5, "Workers' Compensation Loss Run (synthetic)");
        var title = new XFont("Arial", 14, XFontStyleEx.Bold);
        var bold = new XFont("Arial", 8, XFontStyleEx.Bold);
        var body = new XFont("Arial", 8, XFontStyleEx.Regular);
        string[] headers = ["Claim #", "Claimant", "DOI", "Reported", "Status", "Type", "Ind Paid", "Ind Reserve", "Med Paid", "Med Reserve", "Exp Paid", "Total Incurred"];
        double[] widths = [64, 92, 52, 52, 40, 52, 58, 58, 58, 58, 52, 66];
        const double h = 16;

        c.Text(d.CarrierName!, title, c.Left);
        c.Y += 18;
        c.Text("Workers' Compensation Loss Run", bold, c.Left);
        c.Y += 14;
        c.Text($"Insured: {d.InsuredName}", body, c.Left);
        c.Text($"Valued as of: {Date(d.ValuationDate)}", body, c.Left + 360);
        c.Text($"Run Date: {Date(d.ReportRunDate)}", body, c.Left + 560);
        c.Y += 20;

        string[] Totals(string label, Totals s) =>
        [
            label, "", "", "", "", "", Money(s.Indemnity?.Paid), Money(s.Indemnity?.Reserve), Money(s.Medical?.Paid), Money(s.Medical?.Reserve), Money(s.Expense?.Paid),
            Money(s.Total?.Incurred),
        ];

        foreach (var p in d.PolicyPeriods)
        {
            var anchor = $"Policy {p.PolicyNumber}   Policy Period {Date(p.EffectiveDate)} - {Date(p.ExpirationDate)}";
            if (!c.Fits(6 + 2 * h + 16))
            {
                c.NewPage();
            }

            c.Text(anchor, bold, c.Left);
            c.Y += 6;
            Row(c, headers, widths, bold, 6, h, header: true);
            foreach (var claim in p.Claims)
            {
                if (!c.Fits(h))
                {
                    c.NewPage();
                    c.Text(anchor + " (continued)", bold, c.Left);
                    c.Y += 6;
                    Row(c, headers, widths, bold, 6, h, header: true);
                }

                Row(c,
                [
                    claim.ClaimNumber!, claim.ClaimantName!, Date(claim.DateOfInjury), Date(claim.DateReported), claim.Status.ToString()!,
                    claim.ClaimType == ClaimType.MedicalOnly ? "Med Only" : "Indemnity", Money(claim.Indemnity?.Paid), Money(claim.Indemnity?.Reserve),
                    Money(claim.Medical?.Paid), Money(claim.Medical?.Reserve), Money(claim.Expense?.Paid), Money(claim.Total?.Incurred),
                ], widths, body, 6, h);
            }

            var s = p.Subtotals!;
            if (!c.Fits(h))
            {
                c.NewPage();
            }

            Row(c, Totals($"Subtotal ({s.ClaimCount} claims: {s.OpenCount} open / {s.ClosedCount} closed)", s), widths, bold, 6, h);
            c.Y += 16;
        }

        if (!c.Fits(h + 8))
        {
            c.NewPage();
        }

        var t = d.ReportTotals!;
        Row(c, Totals($"GRAND TOTAL ({t.ClaimCount} claims: {t.OpenCount} open / {t.ClosedCount} closed)", t), widths, bold, 6, h);
        var bytes = c.Save("Synthetic test document - not a real loss run.");
        return new Rendered(bytes, "pdf", c.Pages, new PrintedFields(
            ["claimNumber", "claimantName", "dateOfInjury", "dateReported", "status", "claimType", .. MoneyTargets, "total.incurred"],
            ["carrierName", "insuredName", "valuationDate", "reportRunDate"],
            ["policyNumber", "effectiveDate", "expirationDate"],
            ["claimCount", "openCount", "closedCount", .. MoneyTargets, "total.incurred"],
            ["claimCount", "openCount", "closedCount", .. MoneyTargets, "total.incurred"]));
    }

    // ------------------------------------------------------------------ C2: portrait with status codes

    private static Rendered PortraitCodes(LossRunDocument d)
    {
        using var c = new Canvas(8.5, 11, "Loss History Report (synthetic)");
        var title = new XFont("Arial", 13, XFontStyleEx.Bold);
        var bold = new XFont("Arial", 6.5, XFontStyleEx.Bold);
        var body = new XFont("Arial", 6.5, XFontStyleEx.Regular);
        var label = new XFont("Arial", 8, XFontStyleEx.Regular);
        const double h = 13;
        c.Y = 46;
        c.Text(d.CarrierName!.ToUpperInvariant(), title, c.Left);
        c.Y += 16;
        c.Text("Loss History Report - Workers Compensation", label, c.Left);
        c.Y += 14;
        c.Text($"Named Insured: {d.InsuredName}", label, c.Left);
        c.Y += 12;
        c.Text($"Evaluation Date: {Date(d.ValuationDate)}", label, c.Left);
        c.Text($"Printed: {Date(d.ReportRunDate)}", label, c.Left + 300);
        c.Y += 16;

        double[] sw = [80, 60, 60, 44, 64];
        Row(c, ["Policy No.", "Eff", "Exp", "Claims", "Tot. Inc"], sw, bold, 3, h, header: true);
        foreach (var p in d.PolicyPeriods)
        {
            Row(c, [p.PolicyNumber!, Date(p.EffectiveDate), Date(p.ExpirationDate), p.Subtotals!.ClaimCount!.Value.ToString(CultureInfo.InvariantCulture), Money(p.Subtotals.Total?.Incurred)],
                sw, body, 3, h);
        }

        c.Y += 18;
        string[] headers = ["Claim No", "Employee", "Accident Dt", "Rptd", "Cls Dt", "St", "Ind. Pd", "Ind. Res", "Med. Pd", "Med. Res", "Exp. Pd", "O/S", "Tot. Inc"];
        double[] widths = [52, 74, 44, 40, 40, 16, 40, 40, 40, 40, 38, 40, 44];
        string[] Totals(string text, Totals s) =>
        [
            text, "", "", "", "", "", Money(s.Indemnity?.Paid), Money(s.Indemnity?.Reserve), Money(s.Medical?.Paid), Money(s.Medical?.Reserve), Money(s.Expense?.Paid),
            Money(s.Total?.Reserve), Money(s.Total?.Incurred),
        ];

        static string Code(ClaimStatus? s) => s switch { ClaimStatus.Open => "O", ClaimStatus.Closed => "C", _ => "R" };
        foreach (var p in d.PolicyPeriods)
        {
            var anchor = $"Policy No: {p.PolicyNumber}    Eff {Date(p.EffectiveDate)}    Exp {Date(p.ExpirationDate)}";
            if (!c.Fits(5 + 3 * h))
            {
                c.NewPage();
            }

            c.Text(anchor, bold, c.Left);
            c.Y += 5;
            Row(c, headers, widths, bold, 6, h, header: true);
            foreach (var claim in p.Claims)
            {
                if (!c.Fits(h))
                {
                    c.NewPage();
                    c.Text($"Policy No: {p.PolicyNumber} (cont.)", bold, c.Left);
                    c.Y += 5;
                    Row(c, headers, widths, bold, 6, h, header: true);
                }

                Row(c,
                [
                    claim.ClaimNumber!, claim.ClaimantName!, Date(claim.DateOfInjury), Date(claim.DateReported), Date(claim.DateClosed), Code(claim.Status),
                    Money(claim.Indemnity?.Paid), Money(claim.Indemnity?.Reserve), Money(claim.Medical?.Paid), Money(claim.Medical?.Reserve), Money(claim.Expense?.Paid),
                    Money(claim.Total?.Reserve), Money(claim.Total?.Incurred),
                ], widths, body, 6, h);
            }

            if (!c.Fits(h))
            {
                c.NewPage();
            }

            Row(c, Totals($"Policy Total - {p.Subtotals!.ClaimCount} Claims", p.Subtotals), widths, bold, 6, h);
            c.Y += 18;
        }

        if (!c.Fits(h + 40))
        {
            c.NewPage();
        }

        Row(c, Totals($"Account Total - {d.ReportTotals!.ClaimCount} Claims", d.ReportTotals), widths, bold, 6, h);
        c.Text("Status: O = Open, C = Closed, R = Reopened.  O/S = outstanding reserves.", body, c.Left, c.Y + 24);
        var bytes = c.Save("Synthetic test document - not a real loss run.");
        string[] money = [.. MoneyTargets, "total.reserve", "total.incurred"];
        return new Rendered(bytes, "pdf", c.Pages, new PrintedFields(
            ["claimNumber", "claimantName", "dateOfInjury", "dateReported", "dateClosed", "status", .. money],
            ["carrierName", "insuredName", "valuationDate", "reportRunDate"],
            ["policyNumber", "effectiveDate", "expirationDate"],
            ["claimCount", .. money],
            ["claimCount", .. money]));
    }

    // ------------------------------------------------------------------ C3: narrative statement (no tables)

    private static Rendered Narrative(LossRunDocument d)
    {
        using var c = new Canvas(8.5, 11, "Loss Experience Statement (synthetic)");
        var title = new XFont("Times New Roman", 15, XFontStyleEx.Bold);
        var bold = new XFont("Times New Roman", 9.5, XFontStyleEx.Bold);
        var body = new XFont("Times New Roman", 9.5, XFontStyleEx.Regular);
        const double line = 13;
        c.Y = 50;
        c.Text(d.CarrierName!, title, c.Left);
        c.Y += 18;
        c.Text("Loss Experience Statement — Workers' Compensation", bold, c.Left);
        c.Y += line;
        c.Text($"Insured: {d.InsuredName}.  Losses valued as of {Date(d.ValuationDate)}; statement issued {Date(d.ReportRunDate)}.", body, c.Left);
        c.Y += 2 * line;

        void Line(string text, XFont font)
        {
            if (!c.Fits(line))
            {
                c.NewPage();
                c.Y = 50;
            }

            c.Text(text, font, c.Left);
            c.Y += line;
        }

        foreach (var p in d.PolicyPeriods)
        {
            Line($"Policy {p.PolicyNumber}, effective {Date(p.EffectiveDate)} to {Date(p.ExpirationDate)}", bold);
            if (p.Claims.Count == 0)
            {
                Line("No losses were reported under this policy.", body);
            }

            foreach (var claim in p.Claims)
            {
                Line($"Claim {claim.ClaimNumber} — {claim.ClaimantName}; injured {Date(claim.DateOfInjury)}, reported {Date(claim.DateReported)}; status {claim.Status}" +
                    (claim.DateClosed is { } closed ? $" (closed {Date(closed)})." : "."), body);
                Line($"    Indemnity paid ${Money(claim.Indemnity?.Paid)}, reserve ${Money(claim.Indemnity?.Reserve)}; medical paid ${Money(claim.Medical?.Paid)}, " +
                    $"reserve ${Money(claim.Medical?.Reserve)}; expense paid ${Money(claim.Expense?.Paid)}; total incurred ${Money(claim.Total?.Incurred)}.", body);
            }

            var s = p.Subtotals!;
            Line($"Policy totals: {s.ClaimCount} claims ({s.OpenCount} open); paid ${Money(s.Total?.Paid)}, reserves ${Money(s.Total?.Reserve)}, incurred ${Money(s.Total?.Incurred)}.", bold);
            c.Y += line / 2;
        }

        var t = d.ReportTotals!;
        Line($"All policies: {t.ClaimCount} claims; paid ${Money(t.Total?.Paid)}, reserves ${Money(t.Total?.Reserve)}, incurred ${Money(t.Total?.Incurred)}.", bold);
        var bytes = c.Save("Synthetic statement - not a real loss run.");
        return new Rendered(bytes, "pdf", c.Pages, new PrintedFields(
            ["claimNumber", "claimantName", "dateOfInjury", "dateReported", "dateClosed", "status", .. MoneyTargets, "total.incurred"],
            ["carrierName", "insuredName", "valuationDate", "reportRunDate"],
            ["policyNumber", "effectiveDate", "expirationDate"],
            ["claimCount", "openCount", "total.paid", "total.reserve", "total.incurred"],
            ["claimCount", "total.paid", "total.reserve", "total.incurred"]));
    }

    // ------------------------------------------------------------------ C4: XLSX, stacked headers, summary sheet

    private static Rendered XlsxStacked(LossRunDocument d)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Claims Detail");
        ws.Cell("A1").Value = d.CarrierName;
        ws.Cell("A2").Value = "Loss experience valued";
        ws.Cell("B2").Value = d.ValuationDate!.Value.ToDateTime(TimeOnly.MinValue);
        ws.Cell("B2").Style.DateFormat.Format = "mm/dd/yyyy";
        ws.Cell("A3").Value = $"Named Insured: {d.InsuredName}";
        // Corpus v3: WCIO codes, litigation, last day worked and claim type (lost time: the litigation-rate denominator) after the state;
        // subrogation after the grand total.
        string[] fixedHeaders = ["Policy", "Claim Ref", "Injured Party", "Loss Date", "Reported", "Closed", "Status", "State", "Nature", "Cause", "Body Part", "Litigated",
            "Last Day Worked", "Claim Type"];
        for (var i = 0; i < fixedHeaders.Length; i++)
        {
            ws.Cell(6, i + 1).Value = fixedHeaders[i];
        }

        string[] groups = ["Indemnity", "Medical", "Expense"];
        string[] measures = ["Paid", "Outstanding", "Total"];
        for (var g = 0; g < groups.Length; g++)
        {
            var first = fixedHeaders.Length + 1 + g * 3;
            ws.Cell(5, first).Value = groups[g];
            ws.Range(5, first, 5, first + 2).Merge();
            for (var m = 0; m < 3; m++)
            {
                ws.Cell(6, first + m).Value = measures[m];
            }
        }

        var grand = fixedHeaders.Length + 10;
        ws.Cell(6, grand).Value = "Grand Total Incurred";
        ws.Cell(6, grand + 1).Value = "Subrogation";
        ws.Range(5, 1, 6, grand + 1).Style.Font.Bold = true;
        var row = 7;
        foreach (var p in d.PolicyPeriods)
        {
            foreach (var claim in p.Claims)
            {
                ws.Cell(row, 1).Value = p.PolicyNumber;
                ws.Cell(row, 2).Value = claim.ClaimNumber;
                ws.Cell(row, 3).Value = claim.ClaimantName;
                ws.Cell(row, 4).Value = claim.DateOfInjury!.Value.ToDateTime(TimeOnly.MinValue);
                ws.Cell(row, 5).Value = claim.DateReported!.Value.ToDateTime(TimeOnly.MinValue);
                if (claim.DateClosed is { } closed)
                {
                    ws.Cell(row, 6).Value = closed.ToDateTime(TimeOnly.MinValue);
                }

                ws.Cell(row, 7).Value = claim.Status.ToString();
                ws.Cell(row, 8).Value = claim.JurisdictionState;
                ws.Cell(row, 9).Value = claim.NatureOfInjury?.Code;
                ws.Cell(row, 10).Value = claim.CauseOfInjury?.Code;
                ws.Cell(row, 11).Value = claim.PartOfBody?.Code;
                ws.Cell(row, 12).Value = claim.LitigationFlag == true ? "Y" : "N";
                if (claim.LastDayWorked is { } ldw)
                {
                    ws.Cell(row, 13).Value = ldw.ToDateTime(TimeOnly.MinValue);
                    ws.Cell(row, 13).Style.DateFormat.Format = "mm/dd/yyyy";
                }

                ws.Cell(row, 14).Value = claim.ClaimType switch { ClaimType.MedicalOnly => "Medical Only", ClaimType.Indemnity => "Indemnity", _ => "" };
                MoneyTriplet?[] triplets = [claim.Indemnity, claim.Medical, claim.Expense];
                for (var g = 0; g < 3; g++)
                {
                    var first = fixedHeaders.Length + 1 + g * 3;
                    ws.Cell(row, first).Value = triplets[g]!.Paid;
                    ws.Cell(row, first + 1).Value = triplets[g]!.Reserve;
                    ws.Cell(row, first + 2).Value = triplets[g]!.Incurred;
                }

                ws.Cell(row, grand).Value = claim.Total!.Incurred;
                if (claim.RecoverySubrogation is { } recovery)
                {
                    ws.Cell(row, grand + 1).Value = recovery;
                }
                ws.Range(row, 4, row, 6).Style.DateFormat.Format = "mm/dd/yyyy";
                row++;
            }
        }

        var summary = wb.AddWorksheet("Policy Summary");
        string[] sh = ["Policy", "Effective", "Expiration", "Claims", "Open", "Total Incurred"];
        for (var i = 0; i < sh.Length; i++)
        {
            summary.Cell(1, i + 1).Value = sh[i];
        }

        var r = 2;
        foreach (var p in d.PolicyPeriods)
        {
            summary.Cell(r, 1).Value = p.PolicyNumber;
            summary.Cell(r, 2).Value = p.EffectiveDate!.Value.ToDateTime(TimeOnly.MinValue);
            summary.Cell(r, 3).Value = p.ExpirationDate!.Value.ToDateTime(TimeOnly.MinValue);
            summary.Cell(r, 4).Value = p.Subtotals!.ClaimCount;
            summary.Cell(r, 5).Value = p.Subtotals.OpenCount;
            summary.Cell(r, 6).Value = p.Subtotals.Total!.Incurred;
            summary.Range(r, 2, r, 3).Style.DateFormat.Format = "mm/dd/yyyy";
            r++;
        }

        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        var xlsx = Deterministic.Xlsx(stream.ToArray());
        string[] money = ["indemnity.paid", "indemnity.reserve", "indemnity.incurred", "medical.paid", "medical.reserve", "medical.incurred", "expense.paid",
            "expense.reserve", "expense.incurred", "total.incurred"];
        return new Rendered(xlsx, "xlsx", 2, new PrintedFields(
            ["claimNumber", "claimantName", "dateOfInjury", "dateReported", "dateClosed", "status", "jurisdictionState", "natureOfInjury", "causeOfInjury", "partOfBody",
             "litigationFlag", "lastDayWorked", "claimType", "recoverySubrogation", .. money],
            ["carrierName", "insuredName", "valuationDate"],
            ["policyNumber", "effectiveDate", "expirationDate"],
            ["claimCount", "openCount", "total.incurred"],
            []));
    }

    // ------------------------------------------------------------------ C5: CSV export, one wide row per claim

    private static Rendered CsvWide(LossRunDocument d)
    {
        var sb = new StringBuilder();
        sb.Append("Carrier,Policy Number,Pol Eff,Pol Exp,Valuation Date,Claim Number,Claimant,Loss Date,Report Date,Close Date,Claim Status,LOB,State,")
            .Append("Ind Paid,Ind Res,Med Paid,Med Res,Exp Paid,Exp Res,Total Incurred,")
            .Append("Cause of Loss,Nature of Injury,Loss Description,Litigation,Recoveries,LDW\r\n"); // corpus v3
        static string Csv(decimal? m) => m?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
        foreach (var p in d.PolicyPeriods)
        {
            foreach (var claim in p.Claims)
            {
                sb.AppendJoin(',', $"\"{d.CarrierName}\"", p.PolicyNumber, Date(p.EffectiveDate), Date(p.ExpirationDate), Date(d.ValuationDate), claim.ClaimNumber,
                        $"\"{claim.ClaimantName}\"", Date(claim.DateOfInjury), Date(claim.DateReported), Date(claim.DateClosed), claim.Status, claim.LineOfBusiness,
                        claim.JurisdictionState, Csv(claim.Indemnity?.Paid), Csv(claim.Indemnity?.Reserve), Csv(claim.Medical?.Paid), Csv(claim.Medical?.Reserve),
                        Csv(claim.Expense?.Paid), Csv(claim.Expense?.Reserve), Csv(claim.Total?.Incurred),
                        claim.CauseOfInjury?.Code, claim.NatureOfInjury?.Code, $"\"{claim.AccidentDescription}\"", claim.LitigationFlag == true ? "Yes" : "No",
                        Csv(claim.RecoverySubrogation), Date(claim.LastDayWorked))
                    .Append("\r\n");
            }
        }

        return new Rendered(Encoding.UTF8.GetBytes(sb.ToString()), "csv", 1, new PrintedFields(
            ["claimNumber", "claimantName", "dateOfInjury", "dateReported", "dateClosed", "status", "lineOfBusiness", "jurisdictionState", .. MoneyTargets,
             "expense.reserve", "total.incurred", "causeOfInjury", "natureOfInjury", "accidentDescription", "litigationFlag", "recoverySubrogation", "lastDayWorked"],
            ["carrierName", "valuationDate"],
            ["policyNumber", "effectiveDate", "expirationDate"],
            [],
            []));
    }

    // ------------------------------------------------------------------ brief 19 construct layouts (ADR 0018; corpus-gen 0.4.0 constructs plan)

    private static string StatusText(Claim claim) => claim.Status?.ToString() ?? "";

    private static string TypeText(Claim claim) => claim.ClaimType == ClaimType.MedicalOnly ? "Medical Only" : "Indemnity";

    private static string[] SixMoney(MoneyTriplet? ind, MoneyTriplet? med, MoneyTriplet? exp, MoneyTriplet? total) =>
        [Money(ind?.Paid), Money(med?.Paid), Money(exp?.Paid), Money(ind?.Reserve), Money(med?.Reserve), Money(total?.Incurred)];

    private static readonly string[] SixMoneyFields = ["indemnity.paid", "medical.paid", "expense.paid", "indemnity.reserve", "medical.reserve", "total.incurred"];

    /// <summary>C6 (P2, records rows): each claim prints a key line and an amounts line in the same columns, under two header rows.</summary>
    private static Rendered TwoLineRecords(LossRunDocument d)
    {
        using var c = new Canvas(11, 8.5, "Workers' Compensation Loss Run (synthetic)");
        var title = new XFont("Arial", 14, XFontStyleEx.Bold);
        var bold = new XFont("Arial", 8, XFontStyleEx.Bold);
        var body = new XFont("Arial", 8, XFontStyleEx.Regular);
        string[] line0 = ["Claim #:", "Loss State:", "Date of Injury:", "Reported:", "Status:", "Closed:", "Claimant:"];
        string[] line1 = ["", "Ind Paid:", "Med Paid:", "Exp Paid:", "Ind Reserve:", "Med Reserve:", "Total Incurred:"];
        double[] widths = [120, 88, 88, 88, 88, 88, 150];
        const double h = 15;
        c.Text(d.CarrierName!, title, c.Left);
        c.Y += 18;
        c.Text("Loss Experience Report - Workers' Compensation", bold, c.Left);
        c.Y += 14;
        c.Text($"Insured: {d.InsuredName}", body, c.Left);
        c.Text($"Valued as of: {Date(d.ValuationDate)}", body, c.Left + 360);
        c.Y += 20;

        void Headers()
        {
            Row(c, line0, widths, bold, 7, h, header: true);
            Row(c, line1, widths, bold, 7, h, header: true);
        }

        foreach (var p in d.PolicyPeriods)
        {
            var anchor = $"Policy {p.PolicyNumber}   Policy Period {Date(p.EffectiveDate)} - {Date(p.ExpirationDate)}";
            if (!c.Fits(6 + 4 * h + 16))
            {
                c.NewPage();
            }

            c.Text(anchor, bold, c.Left);
            c.Y += 6;
            Headers();
            foreach (var claim in p.Claims)
            {
                if (!c.Fits(2 * h))
                {
                    c.NewPage();
                    c.Text(anchor + " (continued)", bold, c.Left);
                    c.Y += 6;
                    Headers();
                }

                Row(c, [claim.ClaimNumber!, claim.JurisdictionState ?? "", Date(claim.DateOfInjury), Date(claim.DateReported), StatusText(claim), Date(claim.DateClosed),
                    claim.ClaimantName!], widths, body, 7, h);
                Row(c, ["", .. SixMoney(claim.Indemnity, claim.Medical, claim.Expense, claim.Total)], widths, body, 1, h);
            }

            var s = p.Subtotals!;
            if (!c.Fits(h))
            {
                c.NewPage();
            }

            Row(c, [$"Policy Total ({s.ClaimCount} claims)", .. SixMoney(s.Indemnity, s.Medical, s.Expense, s.Total)], widths, bold, 1, h);
            c.Y += 16;
        }

        if (!c.Fits(h + 8))
        {
            c.NewPage();
        }

        var t = d.ReportTotals!;
        Row(c, [$"GRAND TOTAL ({t.ClaimCount} claims)", .. SixMoney(t.Indemnity, t.Medical, t.Expense, t.Total)], widths, bold, 1, h);
        var bytes = c.Save("Synthetic test document - not a real loss run.");
        return new Rendered(bytes, "pdf", c.Pages, new PrintedFields(
            ["claimNumber", "jurisdictionState", "dateOfInjury", "dateReported", "status", "dateClosed", "claimantName", .. SixMoneyFields],
            ["carrierName", "insuredName", "valuationDate"],
            ["policyNumber", "effectiveDate", "expirationDate"],
            ["claimCount", .. SixMoneyFields],
            ["claimCount", .. SixMoneyFields]));
    }

    private static readonly string[] MeasureComponents = ["total", "indemnity", "medical", "expense"];

    private static readonly string[] MeasureFields = [.. MeasureComponents.SelectMany(c => new[] { $"{c}.paid", $"{c}.reserve", $"{c}.incurred" })];

    /// <summary>One row per measure (Inc / Pd / O/S) under the component columns Total | Claim | Medical | Expense.</summary>
    private static IEnumerable<string[]> MeasureRows(MoneyTriplet? total, MoneyTriplet? ind, MoneyTriplet? med, MoneyTriplet? exp)
    {
        yield return ["Inc:", Money(total?.Incurred), Money(ind?.Incurred), Money(med?.Incurred), Money(exp?.Incurred)];
        yield return ["Pd:", Money(total?.Paid), Money(ind?.Paid), Money(med?.Paid), Money(exp?.Paid)];
        yield return ["O/S:", Money(total?.Reserve), Money(ind?.Reserve), Money(med?.Reserve), Money(exp?.Reserve)];
    }

    /// <summary>
    /// C10 (brief 22 Answers 1, measures as rows): each claim prints three rows, one per measure, labelled in a column of its own
    /// (a blank header) under the components Total | Claim | Medical | Expense — "Claim" being the indemnity component. The
    /// policy and grand totals print the same three rows. Modelled on a carrier-published sample; the data is synthetic.
    /// </summary>
    private static Rendered MeasuresRows(LossRunDocument d)
    {
        using var c = new Canvas(11, 8.5, "Workers' Compensation Loss Run (synthetic)");
        var title = new XFont("Arial", 14, XFontStyleEx.Bold);
        var bold = new XFont("Arial", 8, XFontStyleEx.Bold);
        var body = new XFont("Arial", 8, XFontStyleEx.Regular);
        string[] headers = ["Claim Number", "Claimant", "Date of Injury", "Status", "", "Total", "Claim", "Medical", "Expense"];
        double[] widths = [92, 130, 72, 52, 36, 82, 82, 82, 82];
        const double h = 14;
        c.Text(d.CarrierName!, title, c.Left);
        c.Y += 18;
        c.Text("Detail Loss Report - Workers' Compensation", bold, c.Left);
        c.Y += 14;
        c.Text($"Insured: {d.InsuredName}", body, c.Left);
        c.Text($"Valued as of: {Date(d.ValuationDate)}", body, c.Left + 360);
        c.Y += 20;

        void Record(string[] key, IEnumerable<string[]> measures, XFont font)
        {
            var first = true;
            foreach (var m in measures)
            {
                Row(c, [.. first ? key : ["", "", "", ""], .. m], widths, font, 4, h);
                first = false;
            }
        }

        foreach (var p in d.PolicyPeriods)
        {
            var anchor = $"Policy {p.PolicyNumber}   Policy Period {Date(p.EffectiveDate)} - {Date(p.ExpirationDate)}";
            if (!c.Fits(6 + 5 * h + 16))
            {
                c.NewPage();
            }

            c.Text(anchor, bold, c.Left);
            c.Y += 6;
            Row(c, headers, widths, bold, 4, h, header: true);
            foreach (var claim in p.Claims)
            {
                if (!c.Fits(3 * h))
                {
                    c.NewPage();
                    c.Text(anchor + " (continued)", bold, c.Left);
                    c.Y += 6;
                    Row(c, headers, widths, bold, 4, h, header: true);
                }

                Record([claim.ClaimNumber!, claim.ClaimantName!, Date(claim.DateOfInjury), StatusText(claim)], MeasureRows(claim.Total, claim.Indemnity, claim.Medical, claim.Expense), body);
            }

            var s = p.Subtotals!;
            if (!c.Fits(3 * h))
            {
                c.NewPage();
            }

            Record([$"Policy Total ({s.ClaimCount} claims)", "", "", ""], MeasureRows(s.Total, s.Indemnity, s.Medical, s.Expense), bold);
            c.Y += 16;
        }

        if (!c.Fits(3 * h + 8))
        {
            c.NewPage();
        }

        var t = d.ReportTotals!;
        Record([$"GRAND TOTAL ({t.ClaimCount} claims)", "", "", ""], MeasureRows(t.Total, t.Indemnity, t.Medical, t.Expense), bold);
        var bytes = c.Save("Synthetic test document - not a real loss run.");
        return new Rendered(bytes, "pdf", c.Pages, new PrintedFields(
            ["claimNumber", "claimantName", "dateOfInjury", "status", .. MeasureFields],
            ["carrierName", "insuredName", "valuationDate"],
            ["policyNumber", "effectiveDate", "expirationDate"],
            ["claimCount", .. MeasureFields],
            ["claimCount", .. MeasureFields]));
    }

    /// <summary>
    /// C7 (P2, records lines): one small table per claim, then loose lines (claimant, claim type, the claim number on a line of
    /// its own); per policy a "Policy Total:" line and at the end a "Total All Years:" line, amounts under the table's columns.
    /// </summary>
    private static Rendered ClaimPerTable(LossRunDocument d)
    {
        using var c = new Canvas(8.5, 11, "Workers' Compensation Loss Run (synthetic)");
        var title = new XFont("Arial", 13, XFontStyleEx.Bold);
        var bold = new XFont("Arial", 7.5, XFontStyleEx.Bold);
        var body = new XFont("Arial", 7.5, XFontStyleEx.Regular);
        string[] headers = ["Date of Loss", "Status", "Ind Paid", "Med Paid", "Exp Paid", "Ind Reserve", "Med Reserve", "Total Incurred"];
        double[] widths = [66, 56, 66, 66, 62, 66, 66, 72];
        const double h = 14;
        c.Text(d.CarrierName!, title, c.Left);
        c.Y += 16;
        c.Text($"Named Insured: {d.InsuredName}", body, c.Left);
        c.Y += 12;
        c.Text($"Valuation Date: {Date(d.ValuationDate)}", body, c.Left);
        c.Y += 18;

        void AmountsLine(string label, Totals totals)
        {
            var x = c.Left;
            c.Text(label, bold, x, c.Y + 10);
            string[] amounts = ["", "", .. SixMoney(totals.Indemnity, totals.Medical, totals.Expense, totals.Total)];
            for (var i = 0; i < widths.Length; i++)
            {
                if (amounts[i].Length > 0)
                {
                    c.G.DrawString(amounts[i], bold, XBrushes.Black, new XRect(x + 3, c.Y, widths[i] - 6, h), XStringFormats.CenterRight);
                }

                x += widths[i];
            }

            c.Y += h + 6;
        }

        var printedClaims = 0;
        foreach (var p in d.PolicyPeriods)
        {
            if (!c.Fits(4 * h + 40))
            {
                c.NewPage();
            }

            c.Text($"Policy Number: {p.PolicyNumber}   Policy Term: {Date(p.EffectiveDate)} - {Date(p.ExpirationDate)}", bold, c.Left);
            c.Y += 8;
            foreach (var claim in p.Claims)
            {
                if (!c.Fits(2 * h + 40))
                {
                    c.NewPage();
                }

                Row(c, headers, widths, bold, 2, h, header: true);
                Row(c, [Date(claim.DateOfInjury), StatusText(claim), .. SixMoney(claim.Indemnity, claim.Medical, claim.Expense, claim.Total)], widths, body, 2, h);
                c.Y += 10;
                c.Text($"Claimant: {claim.ClaimantName}", body, c.Left + 6);
                c.Y += 11;
                c.Text($"Claim Type: {TypeText(claim)}", body, c.Left + 6);
                c.Y += 11;
                c.Text(claim.ClaimNumber!, bold, c.Left + 6);
                if (printedClaims++ % 2 == 1)
                {
                    // corpus-gen 0.6.0: a file number printed apart on the claim-number line (one printed line, two values).
                    c.Text($"{1_000_000 + printedClaims * 7_919}", body, c.Left + 220);
                }

                c.Y += 12;
            }

            if (!c.Fits(h + 6))
            {
                c.NewPage();
            }

            AmountsLine("Policy Total:", p.Subtotals!);
            c.Y += 10;
        }

        if (!c.Fits(h + 6))
        {
            c.NewPage();
        }

        AmountsLine("Total All Years:", d.ReportTotals!);
        var bytes = c.Save("Synthetic test document - not a real loss run.");
        return new Rendered(bytes, "pdf", c.Pages, new PrintedFields(
            ["claimNumber", "claimantName", "claimType", "dateOfInjury", "status", .. SixMoneyFields],
            ["carrierName", "insuredName", "valuationDate"],
            ["policyNumber", "effectiveDate", "expirationDate"],
            [.. SixMoneyFields],
            [.. SixMoneyFields]));
    }

    /// <summary>C8 (P3): the first three columns print two fields stacked in one cell ("Claim Number" over "Claim Type").</summary>
    private static Rendered StackedColumns(LossRunDocument d)
    {
        using var c = new Canvas(11, 8.5, "Workers' Compensation Loss Run (synthetic)");
        var title = new XFont("Arial", 14, XFontStyleEx.Bold);
        var bold = new XFont("Arial", 8, XFontStyleEx.Bold);
        var body = new XFont("Arial", 8, XFontStyleEx.Regular);
        string[] headers = ["Claim Number\nClaim Type", "Claimant\nStatus", "Injury Date\nReported", "Ind Paid", "Med Paid", "Exp Paid", "Ind Reserve", "Med Reserve", "Total Incurred"];
        double[] widths = [96, 120, 74, 64, 64, 60, 64, 64, 70];
        const double h = 24;

        void Stacked(string[] cells, XFont font, bool header = false)
        {
            var x = c.Left;
            for (var i = 0; i < cells.Length; i++)
            {
                var rect = new XRect(x, c.Y, widths[i], h);
                if (header)
                {
                    c.G.DrawRectangle(XBrushes.Gainsboro, rect);
                }

                c.G.DrawRectangle(XPens.DarkGray, rect);
                var lines = cells[i].Split('\n');
                var numeric = i >= 3 && !header;
                for (var k = 0; k < lines.Length; k++)
                {
                    var lineRect = lines.Length == 1 ? new XRect(x + 3, c.Y, widths[i] - 6, h) : new XRect(x + 3, c.Y + 2 + k * (h - 4) / 2, widths[i] - 6, (h - 4) / 2);
                    c.G.DrawString(lines[k], font, XBrushes.Black, lineRect, numeric ? XStringFormats.CenterRight : XStringFormats.CenterLeft);
                }

                x += widths[i];
            }

            c.Y += h;
        }

        string[] Money5(MoneyTriplet? ind, MoneyTriplet? med, MoneyTriplet? exp, MoneyTriplet? total) => SixMoney(ind, med, exp, total);
        c.Text(d.CarrierName!, title, c.Left);
        c.Y += 18;
        c.Text("Workers' Compensation Loss Run", bold, c.Left);
        c.Y += 14;
        c.Text($"Insured: {d.InsuredName}", body, c.Left);
        c.Text($"Valued as of: {Date(d.ValuationDate)}", body, c.Left + 360);
        c.Y += 20;
        foreach (var p in d.PolicyPeriods)
        {
            var anchor = $"Policy {p.PolicyNumber}   Policy Period {Date(p.EffectiveDate)} - {Date(p.ExpirationDate)}";
            if (!c.Fits(6 + 3 * h + 16))
            {
                c.NewPage();
            }

            c.Text(anchor, bold, c.Left);
            c.Y += 6;
            Stacked(headers, bold, header: true);
            foreach (var claim in p.Claims)
            {
                if (!c.Fits(h))
                {
                    c.NewPage();
                    c.Text(anchor + " (continued)", bold, c.Left);
                    c.Y += 6;
                    Stacked(headers, bold, header: true);
                }

                Stacked([$"{claim.ClaimNumber}\n{TypeText(claim)}", $"{claim.ClaimantName}\n{StatusText(claim)}", $"{Date(claim.DateOfInjury)}\n{Date(claim.DateReported)}",
                    .. Money5(claim.Indemnity, claim.Medical, claim.Expense, claim.Total)], body);
            }

            var s = p.Subtotals!;
            if (!c.Fits(h))
            {
                c.NewPage();
            }

            Stacked([$"Subtotal ({s.ClaimCount} claims)", "", "", .. Money5(s.Indemnity, s.Medical, s.Expense, s.Total)], bold);
            c.Y += 16;
        }

        if (!c.Fits(h + 8))
        {
            c.NewPage();
        }

        var t = d.ReportTotals!;
        Stacked([$"GRAND TOTAL ({t.ClaimCount} claims)", "", "", .. Money5(t.Indemnity, t.Medical, t.Expense, t.Total)], bold);
        var bytes = c.Save("Synthetic test document - not a real loss run.");
        return new Rendered(bytes, "pdf", c.Pages, new PrintedFields(
            ["claimNumber", "claimType", "claimantName", "status", "dateOfInjury", "dateReported", .. SixMoneyFields],
            ["carrierName", "insuredName", "valuationDate"],
            ["policyNumber", "effectiveDate", "expirationDate"],
            ["claimCount", .. SixMoneyFields],
            ["claimCount", .. SixMoneyFields]));
    }

    /// <summary>
    /// C9 (component-sum headers): a CSV whose totals are indemnity + medical, headed so ("Paid (Ind+Med)"), with expense in
    /// separate columns. Truth: every claim's total is indemnity + medical and the document's totalComposition says so.
    /// </summary>
    private static Rendered CsvComposite(LossRunDocument d)
    {
        var sb = new StringBuilder("Policy Number,Pol Eff,Pol Exp,Valuation Date,Claim Number,Claimant,Loss Date,Claim Status,Ind Paid,Med Paid,Paid (Ind+Med),")
            .Append("Ind Res,Med Res,Reserve (Ind+Med),Total Incurred (Ind+Med),Exp Paid,Exp Res\r\n");
        static string Csv(decimal? m) => m?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
        foreach (var p in d.PolicyPeriods)
        {
            foreach (var claim in p.Claims)
            {
                sb.AppendJoin(',', p.PolicyNumber, Date(p.EffectiveDate), Date(p.ExpirationDate), Date(d.ValuationDate), claim.ClaimNumber, $"\"{claim.ClaimantName}\"",
                        Date(claim.DateOfInjury), claim.Status, Csv(claim.Indemnity?.Paid), Csv(claim.Medical?.Paid), Csv(claim.Total?.Paid), Csv(claim.Indemnity?.Reserve),
                        Csv(claim.Medical?.Reserve), Csv(claim.Total?.Reserve), Csv(claim.Total?.Incurred), Csv(claim.Expense?.Paid), Csv(claim.Expense?.Reserve))
                    .Append("\r\n");
            }
        }

        return new Rendered(Encoding.UTF8.GetBytes(sb.ToString()), "csv", 1, new PrintedFields(
            ["claimNumber", "claimantName", "dateOfInjury", "status", "indemnity.paid", "medical.paid", "total.paid", "indemnity.reserve", "medical.reserve", "total.reserve",
             "total.incurred", "expense.paid", "expense.reserve"],
            ["valuationDate"],
            ["policyNumber", "effectiveDate", "expirationDate"],
            [],
            []));
    }

    /// <summary>C9 truth: totals are indemnity + medical (expense printed apart), recomputed for every claim, subtotal and the report.</summary>
    public static LossRunDocument IndemnityMedicalTotals(LossRunDocument d)
    {
        static MoneyTriplet Sum(MoneyTriplet? a, MoneyTriplet? b) => new((a?.Paid ?? 0) + (b?.Paid ?? 0), (a?.Reserve ?? 0) + (b?.Reserve ?? 0), (a?.Incurred ?? 0) + (b?.Incurred ?? 0));
        foreach (var claim in d.PolicyPeriods.SelectMany(p => p.Claims))
        {
            claim.Total = Sum(claim.Indemnity, claim.Medical);
        }

        foreach (var p in d.PolicyPeriods.Where(p => p.Subtotals is not null))
        {
            p.Subtotals!.Total = Sum(p.Subtotals.Indemnity, p.Subtotals.Medical);
        }

        if (d.ReportTotals is not null)
        {
            d.ReportTotals.Total = Sum(d.ReportTotals.Indemnity, d.ReportTotals.Medical);
        }

        d.TotalComposition = TotalComposition.IndemnityMedical;
        d.ExpenseTreatment = ExpenseTreatment.Separate;
        return d;
    }

    // ------------------------------------------------------------------ brief 18 renderings (not in the corpus v3 plan)

    /// <summary>
    /// ADR 0016: the c5 csv-wide layout where one policy period had no claims and the export prints a statement row for it
    /// ("No claims exist for this policy year"). Truth: <paramref name="emptyPeriod"/> loses its claims and states no losses.
    /// Returns the truth it rendered.
    /// </summary>
    public static (Rendered Rendered, LossRunDocument Truth) CsvWideWithNoLossesYear(LossRunDocument d, int emptyPeriod)
    {
        var period = d.PolicyPeriods[emptyPeriod];
        period.Claims.Clear();
        period.NoLossesStatement = true;
        period.Subtotals = null;
        d.ReportTotals = d.ReportTotals is null ? null : Totalize([.. d.PolicyPeriods.SelectMany(p => p.Claims)]);
        var rendered = CsvWide(d);
        var text = Encoding.UTF8.GetString(rendered.Bytes);
        var lines = text.Split("\r\n").ToList();
        var statement = string.Join(',', $"\"{d.CarrierName}\"", period.PolicyNumber, Date(period.EffectiveDate), Date(period.ExpirationDate), Date(d.ValuationDate),
            "\"No claims exist for this policy year\"");
        var at = 1 + d.PolicyPeriods.Take(emptyPeriod).Sum(p => p.Claims.Count);
        lines.Insert(at, statement);
        var printed = rendered.Printed with { PeriodFields = [.. rendered.Printed.PeriodFields, "noLossesStatement"] };
        return (new Rendered(Encoding.UTF8.GetBytes(string.Join("\r\n", lines)), "csv", 1, printed), d);
    }

    /// <summary>
    /// ADR 0017: a summary-only run in the c5 style: one row per fiscal year with counts by status, paid / reserve / incurred
    /// by component and a deductible recovery, then a grand total row. Truth: every period summary-only, labelled by fiscal
    /// year, its claims replaced by the printed subtotals. Returns the truth it rendered.
    /// </summary>
    public static (Rendered Rendered, LossRunDocument Truth) CsvPeriodSummary(LossRunDocument d)
    {
        var sb = new StringBuilder("Fiscal Year,Open Claims,Closed Claims,Total Claims,Ind Paid,Ind Res,Med Paid,Med Res,Exp Paid,Exp Res,Total Incurred,Deductible Recovery\r\n");
        static string Csv(decimal? m) => m?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
        string Row(string label, Totals t) => string.Join(',', label, t.OpenCount, t.ClosedCount, t.ClaimCount, Csv(t.Indemnity?.Paid), Csv(t.Indemnity?.Reserve),
            Csv(t.Medical?.Paid), Csv(t.Medical?.Reserve), Csv(t.Expense?.Paid), Csv(t.Expense?.Reserve), Csv(t.Total?.Incurred), Csv(t.Recoveries?.Deductible)) + "\r\n";
        foreach (var p in d.PolicyPeriods)
        {
            var totals = Totalize(p.Claims);
            totals.Recoveries = new RecoveryAmounts { Deductible = Math.Round((totals.Total?.Paid ?? 0) * 0.1m, 2) };
            p.Subtotals = totals;
            p.Claims.Clear();
            p.DetailLevel = DetailLevel.SummaryOnly;
            p.PeriodKind = PeriodKind.FiscalYear;
            p.PeriodLabel = $"FY {p.EffectiveDate?.Year}-{p.ExpirationDate?.Year % 100:00}";
            (p.PolicyNumber, p.EffectiveDate, p.ExpirationDate, p.CarrierName) = (null, null, null, null);
            sb.Append(Row(p.PeriodLabel, totals));
        }

        static MoneyTriplet Add(IEnumerable<MoneyTriplet?> parts) => new(parts.Sum(x => x?.Paid ?? 0), parts.Sum(x => x?.Reserve ?? 0), parts.Sum(x => x?.Incurred ?? 0));
        var subtotals = d.PolicyPeriods.Select(p => p.Subtotals!).ToList();
        d.ReportTotals = new Totals
        {
            ClaimCount = subtotals.Sum(t => t.ClaimCount), OpenCount = subtotals.Sum(t => t.OpenCount), ClosedCount = subtotals.Sum(t => t.ClosedCount),
            Indemnity = Add(subtotals.Select(t => t.Indemnity)), Medical = Add(subtotals.Select(t => t.Medical)), Expense = Add(subtotals.Select(t => t.Expense)),
            Total = Add(subtotals.Select(t => t.Total)), Recoveries = new RecoveryAmounts { Deductible = subtotals.Sum(t => t.Recoveries!.Deductible) },
        };
        sb.Append(Row("Grand Total", d.ReportTotals));
        string[] subtotalFields = ["claimCount", "openCount", "closedCount", "indemnity.paid", "indemnity.reserve", "medical.paid", "medical.reserve", "expense.paid",
            "expense.reserve", "total.incurred", "recoveries.deductible"];
        return (new Rendered(Encoding.UTF8.GetBytes(sb.ToString()), "csv", 1, new PrintedFields([], [], ["periodLabel", "periodKind"], subtotalFields, subtotalFields)), d);
    }

    // ------------------------------------------------------------------ bundle, password, scans

    /// <summary>Loss run pages followed by a payroll report, an ACORD-style application page and a broker letter.</summary>
    public static (byte[] Bytes, List<TruthSegment> Others, int Pages) Bundle(byte[] lossRunPdf, LossRunDocument d)
    {
        using var input = new MemoryStream(lossRunPdf);
        using var lossRun = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        using var pdf = new PdfDocument();
        foreach (var page in lossRun.Pages)
        {
            pdf.AddPage(page);
        }

        var first = pdf.PageCount + 1;
        var h = new XFont("Arial", 14, XFontStyleEx.Bold);
        var f = new XFont("Arial", 10, XFontStyleEx.Regular);
        void Page(Action<XGraphics> draw)
        {
            var page = pdf.AddPage();
            page.Width = XUnit.FromInch(8.5);
            page.Height = XUnit.FromInch(11);
            using var g = XGraphics.FromPdfPage(page);
            draw(g);
        }

        Page(g =>
        {
            g.DrawString($"{d.InsuredName} - Annual Payroll Report by Class Code", h, XBrushes.Black, 50, 70);
            g.DrawString("Reporting period for workers' compensation audit (synthetic).", f, XBrushes.Black, 50, 95);
            string[][] rows = [["Class Code", "Description", "Employees", "Annual Payroll"], ["8810", "Clerical Office Employees", "12", "$842,000"], ["3632", "Machine Shop", "27", "$1,615,500"]];
            var y = 130.0;
            foreach (var row in rows)
            {
                var x = 50.0;
                foreach (var (cell, width) in row.Zip(new double[] { 80, 200, 80, 120 }))
                {
                    g.DrawRectangle(XPens.DarkGray, x, y, width, 20);
                    g.DrawString(cell, f, XBrushes.Black, new XRect(x + 4, y, width - 8, 20), XStringFormats.CenterLeft);
                    x += width;
                }

                y += 20;
            }
        });
        Page(g =>
        {
            g.DrawString("WORKERS COMPENSATION APPLICATION (synthetic ACORD-style form)", h, XBrushes.Black, 50, 70);
            string[] lines = [$"Applicant: {d.InsuredName}", "Mailing address: 100 Example Street, Sacramento, CA 95814", "Proposed effective date: 12/01/2024",
                "Prior carrier: (see attached loss runs)", "Number of employees: 43", "Nature of business: light manufacturing"];
            var y = 110.0;
            foreach (var line in lines)
            {
                g.DrawRectangle(XPens.DarkGray, 50, y - 14, 500, 20);
                g.DrawString(line, f, XBrushes.Black, 56, y);
                y += 24;
            }
        });
        Page(g =>
        {
            string[] lines = ["Synthetic Brokerage LLC", "", "Dear Underwriting Team,", "", $"Please find enclosed the loss history and payroll for {d.InsuredName}.",
                "Let us know if you need anything further to complete your quote.", "", "Sincerely,", "A. Broker (synthetic)"];
            var y = 80.0;
            foreach (var line in lines)
            {
                g.DrawString(line, f, XBrushes.Black, 72, y);
                y += 18;
            }
        });

        var pages = pdf.PageCount;
        using var stream = new MemoryStream();
        pdf.Save(stream, closeStream: false);
        return (Deterministic.Pdf(stream.ToArray()), [new("Payroll", first, first), new("Application", first + 1, first + 1), new("Letter", first + 2, first + 2)], pages);
    }

    public static byte[] Lock(byte[] pdfBytes, string password)
    {
        using var input = new MemoryStream(pdfBytes);
        using var pdf = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        pdf.SecuritySettings.UserPassword = password;
        pdf.SecuritySettings.OwnerPassword = password + "-owner";
        using var stream = new MemoryStream();
        pdf.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    /// <summary>Image-only PDF of every page: clean 300 dpi, 200 dpi rotated ±1–3° with speckle, or 150 dpi binarized fax.</summary>
    public static byte[] Scan(byte[] nativePdf, string inputType, Random random)
    {
        var (dpi, degrees, specks, quality, binarize) = inputType switch
        {
            InputTypes.Scan300 => (300, 0f, 800, 85, false),
            InputTypes.Scan200 => (200, (float)((random.NextDouble() * 2 + 1) * (random.Next(2) == 0 ? -1 : 1)), 6000, 70, false),
            InputTypes.Fax150 => (150, (float)(random.NextDouble() - 0.5), 2500, 60, true),
            _ => throw new ArgumentOutOfRangeException(nameof(inputType), inputType, null),
        };

        using var pdf = new PdfDocument();
        var pages = PDFtoImage.Conversion.GetPageCount(nativePdf);
        for (var i = 0; i < pages; i++)
        {
            using var rendered = PDFtoImage.Conversion.ToImage(nativePdf, page: i, options: new PDFtoImage.RenderOptions(Dpi: dpi));
            using var output = new SKBitmap(rendered.Width, rendered.Height);
            using (var canvas = new SKCanvas(output))
            {
                canvas.Clear(SKColors.White);
                canvas.RotateDegrees(degrees, rendered.Width / 2f, rendered.Height / 2f);
                using var source = SKImage.FromBitmap(rendered);
                canvas.DrawImage(source, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
                canvas.ResetMatrix();
                using var speck = new SKPaint { Color = new SKColor(60, 60, 60, 140) };
                for (var s = 0; s < specks; s++)
                {
                    canvas.DrawPoint(random.Next(output.Width), random.Next(output.Height), speck);
                }
            }

            if (binarize)
            {
                for (var y = 0; y < output.Height; y++)
                {
                    for (var x = 0; x < output.Width; x++)
                    {
                        var p = output.GetPixel(x, y);
                        var lum = 0.299 * p.Red + 0.587 * p.Green + 0.114 * p.Blue;
                        output.SetPixel(x, y, lum < 150 ? SKColors.Black : SKColors.White);
                    }
                }
            }

            using var image = SKImage.FromBitmap(output);
            using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, quality);
            var page = pdf.AddPage();
            page.Width = XUnit.FromInch(output.Width / (double)dpi);
            page.Height = XUnit.FromInch(output.Height / (double)dpi);
            using var g = XGraphics.FromPdfPage(page);
            using var stream = new MemoryStream(jpeg.ToArray());
            using var xImage = XImage.FromStream(stream);
            g.DrawImage(xImage, 0, 0, page.Width.Point, page.Height.Point);
        }

        using var result = new MemoryStream();
        pdf.Save(result, closeStream: false);
        return Deterministic.Pdf(result.ToArray());
    }
}
