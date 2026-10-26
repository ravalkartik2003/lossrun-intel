using System.Globalization;
using ClosedXML.Excel;
using LossRun.Contracts;
using LossRun.Testing;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;

// Writes the synthetic brief-04 fixtures. All content is invented; the carrier name is fictitious.
var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "fixtures");
Directory.CreateDirectory(outDir);
GlobalFontSettings.UseWindowsFontsUnderWindows = true;

var document = SyntheticLossRuns.ThreeClaims();
if (args.Length > 1 && args[1] == "06")
{
    // Brief-06 set only, so the brief-04 files (and the CU recordings made from them) stay byte-identical.
    var second = Fixtures.SecondCarrierPdf(SyntheticLossRuns.SecondCarrier());
    Fixtures.Write(outDir, "lossrun2-native.pdf", second);
    Fixtures.Write(outDir, "lossrun2-scanned.pdf", Fixtures.Scanned(second, degrees: -1.5f, seed: 7));
    Fixtures.Write(outDir, "lossrun-drifted.pdf", Fixtures.LossRunPdf(document, Fixtures.Drifted));
    Console.WriteLine($"Brief-06 fixtures written to {outDir}");
    return;
}

if (args.Length > 1 && args[1] == "07")
{
    // Brief 07 review path: the native run with one printed subtotal wrong (period 1 medical paid 9,300.00 instead of 9,200.00),
    // so R-10 fails and the document lands in the review queue.
    var bad = SyntheticLossRuns.ThreeClaims();
    bad.PolicyPeriods[0].Subtotals!.Medical = new MoneyTriplet(9300m, 800m, 10100m);
    Fixtures.Write(outDir, "lossrun-badtotal.pdf", Fixtures.LossRunPdf(bad));
    Console.WriteLine($"Brief-07 fixtures written to {outDir}");
    return;
}

var native = Fixtures.LossRunPdf(document);
Fixtures.Write(outDir, "lossrun-native.pdf", native);
Fixtures.Write(outDir, "lossrun-scanned.pdf", Fixtures.Scanned(native));
Fixtures.Write(outDir, "lossrun-locked.pdf", Fixtures.Locked(native, Fixtures.Password));
Fixtures.Write(outDir, "lossrun-stacked.xlsx", Fixtures.StackedXlsx(document));
Fixtures.Write(outDir, "lossrun-export.csv", Fixtures.Csv(document));
Fixtures.Write(outDir, "bundle.pdf", Fixtures.Bundle(native));
Console.WriteLine($"Fixtures written to {outDir}");

internal static class Fixtures
{
    /// <summary>User password of lossrun-locked.pdf (synthetic; listed in the test configuration only).</summary>
    public const string Password = "synthetic-pass-04";

    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public static void Write(string dir, string name, byte[] bytes)
    {
        File.WriteAllBytes(Path.Combine(dir, name), bytes);
        Console.WriteLine($"  {name,-24} {bytes.Length,9:N0} bytes");
    }

    // ------------------------------------------------------------------ native PDF loss run

    /// <summary>Column layout of the Synthetic Mutual run: headers, widths, and the per-claim cell values.</summary>
    public sealed record Layout(string[] Headers, double[] Widths, int TextColumns, Func<Claim, string[]> Cells);

    public static readonly Layout Original = new(
        ["Claim #", "Claimant", "DOI", "Reported", "Status", "Type", "Ind Paid", "Ind Reserve", "Med Paid", "Med Reserve", "Exp Paid", "Total Incurred"],
        [64, 92, 52, 52, 40, 52, 58, 58, 58, 58, 52, 66], 6,
        c =>
        [
            c.ClaimNumber ?? "", c.ClaimantName ?? "", Date(c.DateOfInjury), Date(c.DateReported),
            c.Status?.ToString() ?? "", c.ClaimType == ClaimType.MedicalOnly ? "Med Only" : "Indemnity",
            Money(c.Indemnity?.Paid), Money(c.Indemnity?.Reserve), Money(c.Medical?.Paid), Money(c.Medical?.Reserve),
            Money(c.Expense?.Paid), Money(c.Total?.Incurred),
        ]);

    /// <summary>Drifted v2 (brief 06): "Reported" renamed to "Date Rptd" and a litigation column "Lit" added after "Type".</summary>
    public static readonly Layout Drifted = new(
        ["Claim #", "Claimant", "DOI", "Date Rptd", "Status", "Type", "Lit", "Ind Paid", "Ind Reserve", "Med Paid", "Med Reserve", "Exp Paid", "Total Incurred"],
        [64, 80, 52, 52, 40, 52, 24, 58, 58, 58, 58, 52, 66], 7,
        c =>
        [
            c.ClaimNumber ?? "", c.ClaimantName ?? "", Date(c.DateOfInjury), Date(c.DateReported),
            c.Status?.ToString() ?? "", c.ClaimType == ClaimType.MedicalOnly ? "Med Only" : "Indemnity", c.LitigationFlag == true ? "Y" : "N",
            Money(c.Indemnity?.Paid), Money(c.Indemnity?.Reserve), Money(c.Medical?.Paid), Money(c.Medical?.Reserve),
            Money(c.Expense?.Paid), Money(c.Total?.Incurred),
        ]);

    public static byte[] LossRunPdf(LossRunDocument d, Layout? layout = null)
    {
        using var pdf = new PdfDocument();
        pdf.Info.Title = "Workers' Compensation Loss Run (synthetic)";
        var page = pdf.AddPage();
        page.Width = XUnit.FromInch(11);
        page.Height = XUnit.FromInch(8.5);
        using (var g = XGraphics.FromPdfPage(page))
        {
            DrawLossRun(g, d, layout ?? Original);
        }

        return Save(pdf);
    }

    private static void DrawLossRun(XGraphics g, LossRunDocument d, Layout layout)
    {
        var title = new XFont("Arial", 14, XFontStyleEx.Bold);
        var bold = new XFont("Arial", 8, XFontStyleEx.Bold);
        var body = new XFont("Arial", 8, XFontStyleEx.Regular);
        double left = 36, y = 40;

        g.DrawString($"{d.CarrierName}", title, XBrushes.Black, left, y);
        y += 18;
        g.DrawString("Workers' Compensation Loss Run", bold, XBrushes.Black, left, y);
        y += 14;
        g.DrawString($"Insured: {d.InsuredName}", body, XBrushes.Black, left, y);
        g.DrawString($"Valued as of: {d.ValuationDate:MM/dd/yyyy}", body, XBrushes.Black, left + 360, y);
        g.DrawString($"Run Date: {d.ReportRunDate:MM/dd/yyyy}", body, XBrushes.Black, left + 560, y);
        y += 20;

        var (headers, widths, text) = (layout.Headers, layout.Widths, layout.TextColumns);
        string[] Totals(string label, Totals s) =>
        [
            label, .. Enumerable.Repeat("", text - 1),
            Money(s.Indemnity?.Paid), Money(s.Indemnity?.Reserve), Money(s.Medical?.Paid), Money(s.Medical?.Reserve),
            Money(s.Expense?.Paid), Money(s.Total?.Incurred),
        ];

        foreach (var period in d.PolicyPeriods)
        {
            g.DrawString($"Policy {period.PolicyNumber}   Policy Period {period.EffectiveDate:MM/dd/yyyy} - {period.ExpirationDate:MM/dd/yyyy}", bold, XBrushes.Black, left, y);
            y += 6;
            y = Row(g, headers, widths, left, y, bold, header: true, textColumns: text);
            foreach (var c in period.Claims)
            {
                y = Row(g, layout.Cells(c), widths, left, y, body, textColumns: text);
            }

            var s = period.Subtotals!;
            y = Row(g, Totals($"Subtotal ({s.ClaimCount} claims: {s.OpenCount} open / {s.ClosedCount} closed)", s), widths, left, y, bold, textColumns: text);
            y += 16;
        }

        var t = d.ReportTotals!;
        Row(g, Totals($"GRAND TOTAL ({t.ClaimCount} claims: {t.OpenCount} open / {t.ClosedCount} closed)", t), widths, left, y, bold, textColumns: text);
        g.DrawString("Synthetic test document - not a real loss run.", body, XBrushes.Gray, left, 8.5 * 72 - 30);
    }

    private static double Row(XGraphics g, string[] cells, double[] widths, double left, double y, XFont font, bool header = false, int textColumns = 6,
        double height = 16)
    {
        var x = left;
        for (var i = 0; i < cells.Length; i++)
        {
            var rect = new XRect(x, y, widths[i], height);
            if (header)
            {
                g.DrawRectangle(XBrushes.Gainsboro, rect);
            }

            g.DrawRectangle(XPens.DarkGray, rect);
            var numeric = i >= textColumns && !header;
            var format = numeric ? XStringFormats.CenterRight : XStringFormats.CenterLeft;
            var inner = new XRect(rect.X + 3, rect.Y, rect.Width - 6, rect.Height);
            if (i == 0 && cells.Length > 1 && cells[1] == "" && cells[0].Length > 12)
            {
                g.DrawString(cells[0], font, XBrushes.Black, new XRect(x + 3, y, widths.Take(textColumns).Sum() - 6, height), XStringFormats.CenterLeft);
            }
            else if (!(i > 0 && i < textColumns && cells[0].Length > 12 && cells[1] == ""))
            {
                g.DrawString(cells[i], font, XBrushes.Black, inner, format);
            }

            x += widths[i];
        }

        return y + height;
    }

    private static string Date(DateOnly? d) => d?.ToString("MM/dd/yyyy", Us) ?? "";

    private static string Money(decimal? m) => m?.ToString("#,##0.00", Us) ?? "";

    // ------------------------------------------------------------------ second carrier (brief 06)

    /// <summary>
    /// "Fictional Casualty Group" loss history: portrait, a policy summary box, one claim table per policy under a
    /// "Policy No: … Eff … Exp …" heading, abbreviated headers ("Ind. Pd", "O/S", "Accident Dt"), status codes O/C/R,
    /// "Policy Total" and "Account Total" rows with the claim count only.
    /// </summary>
    public static byte[] SecondCarrierPdf(LossRunDocument d)
    {
        using var pdf = new PdfDocument();
        pdf.Info.Title = "Loss History Report (synthetic)";
        var page = pdf.AddPage();
        page.Width = XUnit.FromInch(8.5);
        page.Height = XUnit.FromInch(11);
        using var g = XGraphics.FromPdfPage(page);
        var title = new XFont("Arial", 13, XFontStyleEx.Bold);
        var bold = new XFont("Arial", 6.5, XFontStyleEx.Bold);
        var body = new XFont("Arial", 6.5, XFontStyleEx.Regular);
        var label = new XFont("Arial", 8, XFontStyleEx.Regular);
        double left = 32, y = 46;

        g.DrawString(d.CarrierName!.ToUpperInvariant(), title, XBrushes.Black, left, y);
        y += 16;
        g.DrawString("Loss History Report - Workers Compensation", label, XBrushes.Black, left, y);
        y += 14;
        g.DrawString($"Named Insured: {d.InsuredName}", label, XBrushes.Black, left, y);
        y += 12;
        g.DrawString($"Evaluation Date: {d.ValuationDate:MM/dd/yyyy}", label, XBrushes.Black, left, y);
        g.DrawString($"Printed: {d.ReportRunDate:MM/dd/yyyy}", label, XBrushes.Black, left + 300, y);
        y += 16;

        // Policy summary box (a second table the claim-table locator must not pick).
        double[] sw = [80, 60, 60, 44, 64];
        y = Row(g, ["Policy No.", "Eff", "Exp", "Claims", "Tot. Inc"], sw, left, y, bold, header: true, textColumns: 3, height: 13);
        foreach (var p in d.PolicyPeriods)
        {
            y = Row(g, [p.PolicyNumber!, Date(p.EffectiveDate), Date(p.ExpirationDate), p.Subtotals!.ClaimCount.ToString()!, Money(p.Subtotals.Total?.Incurred)],
                sw, left, y, body, textColumns: 3, height: 13);
        }

        y += 18;
        string[] headers = ["Claim No", "Employee", "Accident Dt", "Rptd", "Cls Dt", "St", "Ind. Pd", "Ind. Res", "Med. Pd", "Med. Res", "Exp. Pd", "O/S", "Tot. Inc"];
        double[] widths = [52, 74, 44, 40, 40, 16, 40, 40, 40, 40, 38, 40, 44];
        string[] Totals(string text, Totals s) =>
        [
            text, "", "", "", "", "", Money(s.Indemnity?.Paid), Money(s.Indemnity?.Reserve), Money(s.Medical?.Paid), Money(s.Medical?.Reserve),
            Money(s.Expense?.Paid), Money(s.Total?.Reserve), Money(s.Total?.Incurred),
        ];

        foreach (var p in d.PolicyPeriods)
        {
            g.DrawString($"Policy No: {p.PolicyNumber}    Eff {p.EffectiveDate:MM/dd/yyyy}    Exp {p.ExpirationDate:MM/dd/yyyy}", bold, XBrushes.Black, left, y);
            y += 5;
            y = Row(g, headers, widths, left, y, bold, header: true, textColumns: 6, height: 13);
            foreach (var c in p.Claims)
            {
                y = Row(g,
                [
                    c.ClaimNumber!, c.ClaimantName!, Date(c.DateOfInjury), Date(c.DateReported), Date(c.DateClosed), c.RawStatus!,
                    Money(c.Indemnity?.Paid), Money(c.Indemnity?.Reserve), Money(c.Medical?.Paid), Money(c.Medical?.Reserve),
                    Money(c.Expense?.Paid), Money(c.Total?.Reserve), Money(c.Total?.Incurred),
                ], widths, left, y, body, textColumns: 6, height: 13);
            }

            y = Row(g, Totals($"Policy Total - {p.Subtotals!.ClaimCount} Claims", p.Subtotals), widths, left, y, bold, textColumns: 6, height: 13);
            y += 18;
        }

        Row(g, Totals($"Account Total - {d.ReportTotals!.ClaimCount} Claims", d.ReportTotals), widths, left, y, bold, textColumns: 6, height: 13);
        g.DrawString("Status: O = Open, C = Closed, R = Reopened.  O/S = outstanding reserves.", body, XBrushes.Black, left, y + 30);
        g.DrawString("Synthetic test document - not a real loss run.", body, XBrushes.Gray, left, 11 * 72 - 30);
        g.Dispose();
        return Save(pdf);
    }

    // ------------------------------------------------------------------ scanned copy

    public static byte[] Scanned(byte[] nativePdf, float degrees = 2f, int seed = 42)
    {
        const int dpi = 200;
        using var rendered = PDFtoImage.Conversion.ToImage(nativePdf, page: 0, options: new PDFtoImage.RenderOptions(Dpi: dpi));
        using var rotated = new SKBitmap(rendered.Width, rendered.Height);
        using (var canvas = new SKCanvas(rotated))
        {
            canvas.Clear(SKColors.White);
            canvas.RotateDegrees(degrees, rendered.Width / 2f, rendered.Height / 2f);
            using var source = SKImage.FromBitmap(rendered);
            canvas.DrawImage(source, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
            canvas.ResetMatrix();

            var random = new Random(seed); // deterministic speckle noise
            using var speck = new SKPaint { Color = new SKColor(60, 60, 60, 140) };
            for (var i = 0; i < 6000; i++)
            {
                canvas.DrawPoint(random.Next(rotated.Width), random.Next(rotated.Height), speck);
            }
        }

        using var image = SKImage.FromBitmap(rotated);
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 70);
        var jpegBytes = jpeg.ToArray();

        using var pdf = new PdfDocument();
        var page = pdf.AddPage();
        page.Width = XUnit.FromInch(rotated.Width / (double)dpi);
        page.Height = XUnit.FromInch(rotated.Height / (double)dpi);
        using (var g = XGraphics.FromPdfPage(page))
        using (var stream = new MemoryStream(jpegBytes))
        using (var xImage = XImage.FromStream(stream))
        {
            g.DrawImage(xImage, 0, 0, page.Width.Point, page.Height.Point);
        }

        return Save(pdf);
    }

    // ------------------------------------------------------------------ password-protected copy

    public static byte[] Locked(byte[] nativePdf, string password)
    {
        using var input = new MemoryStream(nativePdf);
        using var pdf = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        pdf.SecuritySettings.UserPassword = password;
        pdf.SecuritySettings.OwnerPassword = password + "-owner";
        return Save(pdf);
    }

    // ------------------------------------------------------------------ bundle: loss run + payroll + letter

    public static byte[] Bundle(byte[] nativePdf)
    {
        using var input = new MemoryStream(nativePdf);
        using var lossRun = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        using var pdf = new PdfDocument();
        pdf.AddPage(lossRun.Pages[0]);

        var payroll = pdf.AddPage();
        payroll.Width = XUnit.FromInch(8.5);
        payroll.Height = XUnit.FromInch(11);
        using (var g = XGraphics.FromPdfPage(payroll))
        {
            var h = new XFont("Arial", 14, XFontStyleEx.Bold);
            var f = new XFont("Arial", 10, XFontStyleEx.Regular);
            g.DrawString("Acme Widgets (synthetic) - Annual Payroll Report by Class Code", h, XBrushes.Black, 50, 70);
            g.DrawString("Reporting period 07/01/2023 - 06/30/2024. Prepared for workers' compensation audit.", f, XBrushes.Black, 50, 95);
            string[][] rows =
            [
                ["Class Code", "Description", "Employees", "Annual Payroll"],
                ["8810", "Clerical Office Employees", "12", "$842,000"],
                ["3632", "Machine Shop", "27", "$1,615,500"],
                ["8742", "Salespersons - Outside", "4", "$388,200"],
                ["", "Total", "43", "$2,845,700"],
            ];
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
        }

        var letter = pdf.AddPage();
        letter.Width = XUnit.FromInch(8.5);
        letter.Height = XUnit.FromInch(11);
        using (var g = XGraphics.FromPdfPage(letter))
        {
            var f = new XFont("Arial", 11, XFontStyleEx.Regular);
            string[] lines =
            [
                "Synthetic Brokerage LLC", "100 Example Street, Sacramento, CA 95814", "", "October 3, 2024", "",
                "Dear Underwriting Team,", "",
                "Please find enclosed the loss history and payroll information for Acme Widgets (synthetic)",
                "in support of the workers' compensation renewal effective December 1, 2024. Let us know",
                "if you need anything further to complete your quote.", "", "Sincerely,", "", "A. Broker (synthetic)",
            ];
            var y = 80.0;
            foreach (var line in lines)
            {
                g.DrawString(line, f, XBrushes.Black, 72, y);
                y += 18;
            }
        }

        return Save(pdf);
    }

    // ------------------------------------------------------------------ spreadsheets

    public static byte[] StackedXlsx(LossRunDocument d)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Loss Run");
        ws.Cell("A1").Value = d.CarrierName;
        ws.Cell("A2").Value = "Valued as of";
        ws.Cell("B2").Value = d.ValuationDate!.Value.ToDateTime(TimeOnly.MinValue);
        ws.Cell("B2").Style.DateFormat.Format = "mm/dd/yyyy";
        ws.Cell("A3").Value = $"Insured: {d.InsuredName}";

        // Stacked header: group row (merged) over measure row.
        string[] fixedHeaders = ["Policy Number", "Claim Number", "Claimant", "Date of Injury", "Date Reported", "Status", "Claim Type"];
        for (var i = 0; i < fixedHeaders.Length; i++)
        {
            ws.Cell(6, i + 1).Value = fixedHeaders[i];
        }

        string[] groups = ["Indemnity", "Medical", "Expense"];
        string[] measures = ["Paid", "Reserve", "Incurred"];
        for (var gi = 0; gi < groups.Length; gi++)
        {
            var first = fixedHeaders.Length + 1 + gi * 3;
            ws.Cell(5, first).Value = groups[gi];
            ws.Range(5, first, 5, first + 2).Merge();
            for (var mi = 0; mi < 3; mi++)
            {
                ws.Cell(6, first + mi).Value = measures[mi];
            }
        }

        ws.Cell(6, fixedHeaders.Length + 10).Value = "Total Incurred";
        ws.Range(5, 1, 6, fixedHeaders.Length + 10).Style.Font.Bold = true;

        var row = 7;
        foreach (var period in d.PolicyPeriods)
        {
            foreach (var c in period.Claims)
            {
                ws.Cell(row, 1).Value = period.PolicyNumber;
                ws.Cell(row, 2).Value = c.ClaimNumber;
                ws.Cell(row, 3).Value = c.ClaimantName;
                ws.Cell(row, 4).Value = c.DateOfInjury!.Value.ToDateTime(TimeOnly.MinValue);
                ws.Cell(row, 5).Value = c.DateReported!.Value.ToDateTime(TimeOnly.MinValue);
                ws.Cell(row, 6).Value = c.Status.ToString();
                ws.Cell(row, 7).Value = c.ClaimType == ClaimType.MedicalOnly ? "Medical Only" : "Indemnity";
                MoneyTriplet?[] triplets = [c.Indemnity, c.Medical, c.Expense];
                for (var gi = 0; gi < 3; gi++)
                {
                    var first = fixedHeaders.Length + 1 + gi * 3;
                    ws.Cell(row, first).Value = triplets[gi]!.Paid;
                    ws.Cell(row, first + 1).Value = triplets[gi]!.Reserve;
                    ws.Cell(row, first + 2).Value = triplets[gi]!.Incurred;
                }

                ws.Cell(row, fixedHeaders.Length + 10).Value = c.Total!.Incurred;
                ws.Range(row, 4, row, 5).Style.DateFormat.Format = "mm/dd/yyyy";
                row++;
            }
        }

        var summary = wb.AddWorksheet("Summary");
        summary.Cell("A1").Value = "Policy Number";
        summary.Cell("B1").Value = "Policy Period";
        summary.Cell("C1").Value = "Claims";
        summary.Cell("D1").Value = "Open";
        summary.Cell("E1").Value = "Closed";
        summary.Cell("F1").Value = "Total Incurred";
        var r = 2;
        foreach (var period in d.PolicyPeriods)
        {
            summary.Cell(r, 1).Value = period.PolicyNumber;
            summary.Cell(r, 2).Value = $"{period.EffectiveDate:MM/dd/yyyy} - {period.ExpirationDate:MM/dd/yyyy}";
            summary.Cell(r, 3).Value = period.Subtotals!.ClaimCount;
            summary.Cell(r, 4).Value = period.Subtotals.OpenCount;
            summary.Cell(r, 5).Value = period.Subtotals.ClosedCount;
            summary.Cell(r, 6).Value = period.Subtotals.Total!.Incurred;
            r++;
        }

        var notes = wb.AddWorksheet("Notes");
        notes.Cell("A1").Value = "Internal: synthetic workbook for ingestion tests.";
        notes.Visibility = XLWorksheetVisibility.Hidden;

        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        return stream.ToArray();
    }

    public static byte[] Csv(LossRunDocument d)
    {
        var lines = new List<string> { "Policy #,Claim #,Claimant,DOI,Report Date,Status,Type,Indemnity Paid,Indemnity Reserve,Medical Paid,Medical Reserve,Expense Paid,Total Incurred" };
        foreach (var period in d.PolicyPeriods)
        {
            foreach (var c in period.Claims)
            {
                lines.Add(string.Join(',',
                    period.PolicyNumber, c.ClaimNumber, $"\"{c.ClaimantName}\"", Date(c.DateOfInjury), Date(c.DateReported), c.Status,
                    c.ClaimType == ClaimType.MedicalOnly ? "MO" : "IND",
                    Csv(c.Indemnity?.Paid), Csv(c.Indemnity?.Reserve), Csv(c.Medical?.Paid), Csv(c.Medical?.Reserve), Csv(c.Expense?.Paid),
                    Csv(c.Total?.Incurred)));
            }
        }

        return System.Text.Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n");

        static string Csv(decimal? m) => m?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
    }

    private static byte[] Save(PdfDocument pdf)
    {
        using var stream = new MemoryStream();
        pdf.Save(stream, closeStream: false);
        return stream.ToArray();
    }
}
