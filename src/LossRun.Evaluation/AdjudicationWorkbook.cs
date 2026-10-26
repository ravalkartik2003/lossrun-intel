using System.Globalization;
using ClosedXML.Excel;

namespace LossRun.Evaluation;

/// <summary>One adjudicated row read back from <c>adjudication.xlsx</c>.</summary>
/// <param name="Status">matched | archive-only | extracted-only (the machine status; hidden in the workbook).</param>
/// <param name="Extracted">What the document printed, as extracted (the workbook's extracted columns); null for archive-only.</param>
public sealed record AdjudicatedRow(int Row, string Key, string Status, string? DocumentId, string Decision, bool Defaulted, KeyedClaim? Extracted, string? Note);

/// <summary>
/// <c>adjudication.xlsx</c> (brief 14 follow-up, Answers 1): what the evaluator decides on, written by <c>lossrun eval align</c>
/// next to the machine file <c>aligned.csv</c> and read back by <c>lossrun eval truth --adjudication</c>. One row per claim
/// that needs a decision:
/// <list type="bullet">
/// <item>a matched claim whose amounts differ from the archive at the same valuation date;</item>
/// <item>an archive claim that was not extracted;</item>
/// <item>an extracted claim that is not in the archive.</item>
/// </list>
/// Each row shows the raw claim number, carrier, policy, date of injury, both valuation dates, and the archive and
/// extracted amounts side by side, with a <c>decision</c> drop-down and a free-text note. The evaluator never sees a
/// normalized key: it sits in a hidden column, as do the document id and the machine status.
/// </summary>
public static class AdjudicationWorkbook
{
    public const string ArchiveCorrect = "archive-correct";
    public const string ExtractedCorrect = "extracted-correct";
    public const string NotComparable = "not-comparable";
    public const string Exclude = "exclude";
    public static readonly string[] Decisions = [ArchiveCorrect, ExtractedCorrect, NotComparable, Exclude];

    private const string Sheet = "Adjudication";
    private static readonly string[] Amounts = ["Indemnity paid", "Indemnity reserve", "Medical paid", "Medical reserve", "Expense paid", "Total incurred"];
    private const int FirstAmount = 10;                     // J
    private const int DecisionColumn = FirstAmount + 13;    // W (after 12 amount columns and Differences)
    private const int NoteColumn = DecisionColumn + 1;
    private const int KeyColumn = NoteColumn + 1;           // hidden
    private const int DocumentIdColumn = KeyColumn + 1;     // hidden
    private const int StatusColumn = DocumentIdColumn + 1;  // hidden

    /// <summary>The rows the evaluator decides on (matched rows that agree, or are not comparable, need no decision).</summary>
    public static bool NeedsDecision(AlignedClaim a) => a.Status != "matched" || a.Disagreements.Count > 0;

    /// <param name="documents">Document id → file (the evaluator picks files, never ids).</param>
    public static void Write(string path, IReadOnlyList<AlignedClaim> aligned, IReadOnlyDictionary<string, string> documents)
    {
        using var wb = new XLWorkbook();
        var help = wb.AddWorksheet("Instructions");
        string[] lines =
        [
            "Adjudication: one row per claim that needs a decision. Fill the Decision column (drop-down) and, where asked, the Document column.",
            "",
            "Matched, amounts differ: the extracted claim is the archive's claim, valued on the same date, but some amounts differ.",
            $"   {ArchiveCorrect} (default if left blank): the archive amounts are what the document prints.",
            $"   {ExtractedCorrect}: the document prints the extracted amounts (the archive was keyed differently).",
            $"   {NotComparable}: the archive amounts are not the printed ones (e.g. a later correction); the claim is kept, its amounts are not scored.",
            $"   {Exclude}: the claim does not belong in the truth.",
            "",
            "In the archive, not extracted: the archive has this claim; the extractor did not return it.",
            $"   If a document of the set prints it: choose that Document, then {ArchiveCorrect} (or {NotComparable} if the archive amounts are not the printed ones).",
            $"   If no document of the set prints it: {Exclude} (default if left blank).",
            "",
            "Extracted, not in the archive: the extractor returned a claim the archive does not have.",
            $"   {ExtractedCorrect}: the document does print this claim as extracted (the archive is missing it).",
            $"   {NotComparable}: the document prints the claim, but not these amounts.",
            $"   {Exclude} (default if left blank): the extractor invented it, or it is not a claim; it then counts against the extractor as an extra claim.",
            "",
            "Do not edit other columns. Save the file (keep .xlsx), then run: lossrun eval truth --archive <truth.csv> --aligned <aligned.csv> --adjudication <this file> --corpus <corpus>",
        ];
        for (var i = 0; i < lines.Length; i++)
        {
            help.Cell(i + 1, 1).Value = lines[i];
        }

        help.Column(1).Width = 140;

        var lists = wb.AddWorksheet("Lists");
        for (var i = 0; i < Decisions.Length; i++)
        {
            lists.Cell(i + 1, 1).Value = Decisions[i];
        }

        var files = documents.OrderBy(d => d.Value, StringComparer.Ordinal).ToList();
        for (var i = 0; i < files.Count; i++)
        {
            lists.Cell(i + 1, 3).Value = files[i].Value;
            lists.Cell(i + 1, 4).Value = files[i].Key;
        }

        lists.Visibility = XLWorksheetVisibility.Hidden;

        var ws = wb.AddWorksheet(Sheet);
        string[] head = ["#", "Situation", "Document", "Carrier", "Policy", "Claim number", "Date of injury", "Valued (archive)", "Valued (document)"];
        for (var i = 0; i < head.Length; i++)
        {
            ws.Cell(1, i + 1).Value = head[i];
        }

        for (var i = 0; i < Amounts.Length; i++)
        {
            ws.Cell(1, FirstAmount + 2 * i).Value = $"{Amounts[i]} (archive)";
            ws.Cell(1, FirstAmount + 2 * i + 1).Value = $"{Amounts[i]} (extracted)";
        }

        ws.Cell(1, FirstAmount + 12).Value = "Differences";
        ws.Cell(1, DecisionColumn).Value = "Decision";
        ws.Cell(1, NoteColumn).Value = "Note";
        ws.Cell(1, KeyColumn).Value = "key";
        ws.Cell(1, DocumentIdColumn).Value = "document id";
        ws.Cell(1, StatusColumn).Value = "status";
        ws.Row(1).Style.Font.Bold = true;

        var row = 2;
        foreach (var a in aligned.Where(NeedsDecision))
        {
            var archive = a.Archive;
            var extracted = a.Extracted;
            ws.Cell(row, 1).Value = row - 1;
            ws.Cell(row, 2).Value = a.Status switch
            {
                "matched" => "Matched, amounts differ",
                "archive-only" => "In the archive, not extracted",
                _ => "Extracted, not in the archive",
            };
            if (a.Document is { } doc && documents.TryGetValue(doc, out var file))
            {
                ws.Cell(row, 3).Value = file;
            }

            var shown = archive ?? extracted!;
            ws.Cell(row, 4).Value = shown.Carrier;
            ws.Cell(row, 5).Value = shown.PolicyNumber;
            ws.Cell(row, 6).Value = shown.ClaimNumber;
            Date(ws.Cell(row, 7), shown.DateOfInjury);
            Date(ws.Cell(row, 8), archive?.ValuationDate);
            Date(ws.Cell(row, 9), extracted?.ValuationDate);
            Func<KeyedClaim, decimal?>[] values = [k => k.IndemnityPaid, k => k.IndemnityReserve, k => k.MedicalPaid, k => k.MedicalReserve, k => k.ExpensePaid, k => k.TotalIncurred];
            for (var i = 0; i < values.Length; i++)
            {
                Money(ws.Cell(row, FirstAmount + 2 * i), archive is null ? null : values[i](archive));
                Money(ws.Cell(row, FirstAmount + 2 * i + 1), extracted is null ? null : values[i](extracted));
            }

            ws.Cell(row, FirstAmount + 12).Value = string.Join("; ", a.Disagreements);
            ws.Cell(row, KeyColumn).Value = a.Key;
            ws.Cell(row, DocumentIdColumn).Value = a.Document ?? "";
            ws.Cell(row, StatusColumn).Value = a.Status;
            row++;
        }

        var last = Math.Max(2, row - 1);
        ws.Range(2, DecisionColumn, last, DecisionColumn).CreateDataValidation().List(lists.Range(1, 1, Decisions.Length, 1), true);
        if (files.Count > 0)
        {
            ws.Range(2, 3, last, 3).CreateDataValidation().List(lists.Range(1, 3, files.Count, 3), true);
        }

        ws.Column(KeyColumn).Hide();
        ws.Column(DocumentIdColumn).Hide();
        ws.Column(StatusColumn).Hide();
        ws.SheetView.FreezeRows(1);
        ws.Columns(1, NoteColumn).AdjustToContents(1, Math.Min(row, 200));
        ws.Column(DecisionColumn).Width = 18;
        ws.Column(NoteColumn).Width = 40;
        ws.SetTabActive();
        wb.SaveAs(path);
    }

    /// <summary>Reads the decisions back. A blank decision takes its default (matched: archive-correct; otherwise exclude, or archive-correct when a document was chosen).</summary>
    public static List<AdjudicatedRow> Read(string path)
    {
        using var wb = new XLWorkbook(path);
        var lists = wb.Worksheet("Lists");
        var idOfFile = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var r = 1; !lists.Cell(r, 3).IsEmpty(); r++)
        {
            idOfFile[lists.Cell(r, 3).GetString()] = lists.Cell(r, 4).GetString();
        }

        var ws = wb.Worksheet(Sheet);
        var rows = new List<AdjudicatedRow>();
        for (var r = 2; !ws.Cell(r, KeyColumn).IsEmpty(); r++)
        {
            var status = ws.Cell(r, StatusColumn).GetString();
            var file = ws.Cell(r, 3).GetString().Trim();
            var documentId = file.Length > 0 ? idOfFile.GetValueOrDefault(file) ?? throw new FormatException($"row {r - 1}: '{file}' is not a document of the set (pick it from the drop-down)")
                : ws.Cell(r, DocumentIdColumn).GetString() is { Length: > 0 } hidden ? hidden : null;
            var typed = ws.Cell(r, DecisionColumn).GetString().Trim().ToLowerInvariant();
            if (typed.Length > 0 && !Decisions.Contains(typed))
            {
                throw new FormatException($"row {r - 1}: decision '{typed}' is not one of {string.Join(", ", Decisions)}");
            }

            var decision = typed.Length > 0 ? typed : status == "matched" || (status == "archive-only" && file.Length > 0) ? ArchiveCorrect : Exclude;
            KeyedClaim? extracted = status == "archive-only" ? null : new KeyedClaim(ws.Cell(r, 4).GetString(), ws.Cell(r, 5).GetString(), ws.Cell(r, 6).GetString(),
                DateOf(ws.Cell(r, 7)), DateOf(ws.Cell(r, 9)), MoneyOf(ws.Cell(r, FirstAmount + 1)), MoneyOf(ws.Cell(r, FirstAmount + 3)), MoneyOf(ws.Cell(r, FirstAmount + 5)),
                MoneyOf(ws.Cell(r, FirstAmount + 7)), MoneyOf(ws.Cell(r, FirstAmount + 9)), MoneyOf(ws.Cell(r, FirstAmount + 11)));
            rows.Add(new AdjudicatedRow(r - 1, ws.Cell(r, KeyColumn).GetString(), status, documentId, decision, typed.Length == 0, extracted,
                ws.Cell(r, NoteColumn).GetString() is { Length: > 0 } note ? note : null));
        }

        return rows;
    }

    private static void Date(IXLCell cell, DateOnly? value)
    {
        if (value is { } d)
        {
            cell.Value = d.ToDateTime(TimeOnly.MinValue);
            cell.Style.DateFormat.Format = "yyyy-mm-dd";
        }
    }

    private static void Money(IXLCell cell, decimal? value)
    {
        if (value is { } v)
        {
            cell.Value = v;
            cell.Style.NumberFormat.Format = "#,##0.00";
        }
    }

    private static DateOnly? DateOf(IXLCell cell) =>
        cell.IsEmpty() ? null : cell.DataType == XLDataType.DateTime ? DateOnly.FromDateTime(cell.GetDateTime()) : LossRun.Contracts.CanonicalComparer.NormalizeDate(cell.GetString());

    private static decimal? MoneyOf(IXLCell cell) =>
        cell.IsEmpty() ? null : cell.DataType == XLDataType.Number ? (decimal)cell.GetDouble() : LossRun.Contracts.CanonicalComparer.NormalizeCurrency(cell.GetString());
}
