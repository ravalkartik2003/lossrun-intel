using System.Globalization;
using System.Text;
using LossRun.Contracts;
using LossRun.Formats;
using LossRun.Rules;

namespace LossRun.Learning;

/// <summary>
/// Prompt layout for caching (carrier-format-memory.md, cost levers; azure-extraction-stack.md, prompt caching): a static
/// prefix that is byte-identical across every learn and delta call (instructions, replay semantics, rule catalog,
/// glossary, code-set names, the spec schema and a worked example — far above the 1,024-token cache minimum), then the
/// per-document part. Changing the prefix text means bumping <see cref="Version"/>.
/// </summary>
public static class PromptBuilder
{
    public const string Version = "learn-v12"; // v12 (brief 35): one label is one field, the carrier from its own label, carrierNames reviewer-owned; v11 (brief 23): headerLabel locator, header-block evidence, period targets; v10: records mode measures

    private const int MaxMarkdownChars = 12_000;
    private const int MaxParagraphs = 80;

    /// <summary>The static prefix (built once per process; identical bytes for every call).</summary>
    public static string StaticPrefix { get; } = BuildPrefix(CanonicalData.Glossary, CanonicalData.CodeSets, RuleCatalog.Embedded);

    /// <summary>ADR 0018 constructs (records, stacked cells, component sums, status rule, labelNext), with JSON examples.</summary>
    // ADR 0018 constructs: described in the document part only when the document shows them (brief 19 corpus v3 regression: in
    // the shared prefix they were applied to ordinary layouts — a status rule beside a status column, summary-box columns as
    // subtotal counts).
    private const string RecordsHint = """
            - Claim records on more than one printed line (tableLocator.records):
              mode "rows": a claim prints a key line (claim number, dates, status) and a second line of amounts in the same
              columns, often with a second header row ("Loss State:" over "Loss Paid:"). Set records = {"mode":"rows",
              "keySource":"<claim number header>"}; map key-line columns as usual and each second-line value with "line": 1 and
              "under": "<the key-line header it is printed under>", e.g. {"source":"Loss Paid:","target":"claim.indemnity.paid",
              "line":1,"under":"Loss State:","normalizer":{"type":"currency"}}.
              mode "lines": one small table per claim (or one row) and loose lines after it ("Claim Type: …", the claim
              number on a line of its own, "Location: …"). Set records = {"mode":"lines","lineFields":[{"pattern":
              "^Claim Type:\s*(?<value>.+)$","target":"claim.claimType","normalizer":{...}}, …]}; a field without a label is
              read by "offset" (0-based loose line after the claim row; -1 = the last). Loose total lines ("Policy Total: …")
              are total rows when a rowKinds regex matches them; their amounts are read by column position.
            """;

    private const string StackedHint = """
            - Stacked cells: a header printing two fields in one cell ("Claim Number" over "Claim Type") is mapped twice with
              the same source and "part": 0 and "part": 1; each body cell gives its printed lines in order. When both values
              sit on one line, add "partPattern" with groups p0, p1 (e.g. "^(?<p0>\S+)\s+(?<p1>.+)$").
            """;

    private const string ComponentSumHint = """
            - Headers naming components ("Paid CLM + MED + EXP", "Total Incurred (Ind+Med)") are totals: map them to
              claim.total.<measure> with "components": ["indemnity","medical"(,"expense")] as printed (CLM / Claim / Loss =
              indemnity there). Ind+Med totals beside separate expense columns are then checked by R-29, not R-20.
            """;

    private const string StatusRuleHint = """
            - No status column: do not map one and do not guess. The pipeline sets "statusFrom": "closeDate" only when the
              printed open / closed counts confirm it, else "unknown". If the run prints no medical column at all, do not map
              one: "absentComponents": ["medical"] records it as not reported by the source.
            """;

    private const string LabelNextHint = """
            - A document value printed as a label paragraph with the value in the next paragraph ("Valuation Date" then
              "9/30/2024"): locator {"type":"labelNext","label":"Valuation Date"} instead of a regex across both.
            """;


    private static string BuildPrefix(Glossary glossary, CodeSets codeSets, RuleCatalog catalog)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"""
            # Role ({Version})
            You write and repair CarrierFormat specs for workers' compensation loss runs. A spec is a small declarative
            program that a deterministic replay engine executes on a document graph (Azure Content Understanding layout:
            paragraphs, tables, cells, OCR words; or spreadsheet cells) to produce a canonical loss-run document. The spec
            you write is replayed on the same document immediately and verified against the document's own printed
            totals and the rule catalog below; failures come back to you. Once verified it is replayed on every future
            document of this carrier format with no model call, so it must be general for the format (regexes that match
            the layout, not this document's values) and exact about the printed text.

            # How replay executes a spec (follow these semantics exactly)
            - Header matching: printed header text and columnMap.source are compared after normalization (lower case,
              punctuation removed, whitespace collapsed). Use the header text exactly as printed in the table header cells
              listed in the document part. Stacked headers are composed ("Indemnity Paid").
            - A table is used when at least tableLocator.headerMatchMin (default 0.8) of the distinct columnMap sources
              appear among its headers. Tables that do not qualify (e.g. a policy summary box) are ignored.
            - Every header of a used table must be mapped in columnMap or listed in ignoredHeaders; otherwise it is reported
              as unmapped. One source may map to several targets (e.g. Status → claim.status (enum) and claim.rawStatus (text)).
            - PDFs, periods.source = "blockAnchor": tableLocator.blockAnchor is a .NET regex matched against each paragraph's
              full text; each match opens a policy period and must capture the named groups policyNumber, effectiveDate and
              expirationDate. Tables below an anchor (same page, lower) or on later pages belong to that period. Anchor
              regexes should be anchored (^...$) and tolerant of extra spaces (\s+).
            - periods.source = "column": a column (periods.column, its printed header) holds the policy number on each row.
              Exports often repeat period and document values on every row: map such columns to period.effectiveDate,
              period.expirationDate (periods.source "column" only) or document.valuationDate | reportRunDate | carrierName |
              insuredName (spreadsheets) — the first row that has the value fills it. When another sheet summarizes the
              policies, periods.lookup reads it by policy number (keyHeader): the period dates from one range cell
              (rangeHeader + rangeRegex with effectiveDate/expirationDate groups) or from two date columns
              (effectiveHeader, expirationHeader), and summary values via subtotals.
            - Rows: each table row's text is its non-empty cells, trimmed, joined with single spaces in column order — the
              document part prints this text. tableLocator.rowKinds classifies a row by a regex over that text: subtotal
              (closes the current period's subtotals), grandTotal (report totals) or skip; first matching kind wins; any
              other row with at least one mapped value is a claim row. countsRegex (optional) reads named groups
              claimCount, openCount, closedCount from a subtotal or total row's text.
            - Money columns (targets claim.indemnity|medical|expense|total.paid|reserve|incurred, claim.recoverySubrogation)
              are also read from subtotal and grand-total rows into the period subtotals and report totals. Recovery columns only total rows
              print map to period.subtotals.recoveries.deductible|reinsurance|sif|other; never map a column of the claim
              table, or of a policy summary box, to period.subtotals in a claims-shaped spec.
            - "No losses" per period (rule R-14 is per policy period): a row or a paragraph under a period heading that says
              the period had no claims ("No claims exist for this policy year") marks that period, never a claim row. The
              default pattern covers common phrasings; set periods.noLossesPattern only for this carrier's own wording.
            - Summary-only runs (no claim rows; one row per fiscal, report or policy year with counts and totals): set
              tableLocator.rowShape = "periodSummary". Each data row is then one period: map the year column to
              period.periodLabel (or period.effectiveDate / period.expirationDate / period.policyNumber when printed), money
              columns to claim.* money paths or period.subtotals.*, count columns to period.subtotals.claimCount|openCount|
              closedCount, and set periods.kind (fiscalYear, reportYear, accidentYear, calendarYear, policyTerm). The grand
              total row (rowKinds grandTotal) must reconcile to the period rows (R-26). Never map period rows to claims.
            """);
        sb.AppendLine("- Unusual layouts (claims printed on two lines or one table per claim, stacked two-field cells, headers naming their components, no status column, a label and its value in separate paragraphs) have spec constructs; the document part describes the one to use when the document shows it. Do not use them otherwise.");
        sb.AppendLine($"""
            - Composite words: "Loss" / "Losses" (Loss Paid, Loss Reserve) mean indemnity ONLY when the same table also has a
              separate medical column; otherwise the reading is ambiguous and the document goes to review unless printed
              totals confirm it. Glossary ignoredHeaders (below) are listed in ignoredHeaders, never mapped.
            - derivedRules compute a value that is not printed: target = sum of operands (paths relative to a claim or a
              totals block, e.g. indemnity.incurred = indemnity.paid + indemnity.reserve), when "absent" (only if not
              printed), appliesTo claim and/or totals. Add them when a component's incurred (or reserve) is not printed but
              its parts are, so totals reconcile.
            - Normalizers: text; date (MM/DD/YYYY and other common forms); currency ("1,234.00", "(12.00)", "$5"); integer;
              enum (requires glossaryField whose valueAliases map printed values, e.g. claim.status: O/C/R, Open/Closed);
              claimNumber (optional pattern with named groups + format to rebuild split OCR tokens); code (codeSystem);
              boolean (Y/N, Yes/No, True/False, X).
            - nullWhen lists printed values meaning "absent" (e.g. "", "N/A", "--").
            - documentFields locators: regex (named group "value", matched against each paragraph text — or each cell for
              spreadsheets — in reading order, first match wins), regexPresent (boolean: does any text match),
              paragraphRole (title, pageHeader, sectionHeading, ...), cell (sheet + a1), labelRight (sheet + label: the cell
              right of a label cell), const (value + reason: only for facts the document does not print, e.g.
              expenseTreatment). Always locate carrierName, insuredName, valuationDate (the "as of"/evaluation date),
              reportRunDate when printed, lineOfBusiness when printed, and declare expenseTreatment (separate when expense
              has its own columns; mergedIntoMedical; excluded; unknown).
            - periodBoundaries.expirationInclusive: true only when the run prints the last covered day (e.g. 06/30) instead
              of the anniversary date (07/01).
            - tableLocator.lineFallback (PDFs): when Content Understanding returned no table for a period block (typical for
              skewed scans), rows are rebuilt from OCR words: words are clustered into lines (lineTolerance x median word
              height), the header line is the line containing at least minHeaderMatch of the columnMap sources, and each
              word goes to the rightmost column whose header starts at or left of the word's centre. Enable it (enabled,
              deskew true) for every PDF format; it is only used when a block has no table.
            - validation.expectedApplicable lists the rule ids this format makes applicable; softOverrides are rare.
            - Do not invent columns: map only headers that are printed. Do not hard-code this document's values (claim
              numbers, amounts, policy numbers, names) in regexes; constants only in const locators with a reason.

            # Verification your spec must pass (on the same document)
            0 hard rule failures; no unmapped headers; every claim row has a claim number; every period has a policy number,
            effective and expiration dates; carrierName and valuationDate located; no normalization issues; and at least
            one printed subtotal or grand total reconciles to the claim rows (R-10 or R-12 pass). A rule that is "n/a" had
            missing operands; "not-informative" passed only because a value was derived from the others.
            Lint (also verified): columnMap targets are claim.* canonical paths only (period values come from blockAnchor or
            periods, subtotal and total rows from rowKinds); derivedRules paths are component.measure without a "claim."
            prefix; constants only for {string.Join(", ", SpecLint.ConstAllowed)} and only when the value (or a glossary alias of
            it) is NOT printed on page 1 — a printed value needs a regex locator; never assume a state, insured or date;
            expenseTreatment is "separate" whenever an expense column is mapped (even expense paid alone); a regex capture
            for a name must stop before the next printed label (paragraphs may merge several printed lines).

            # Rule catalog ({catalog.CatalogVersion})
            """);
        foreach (var rule in catalog.Rules)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {rule.Id} [{rule.Severity.ToString().ToLowerInvariant()}, {rule.Scope.ToString().ToLowerInvariant()}] {rule.Name}");
        }

        sb.AppendLine();
        sb.AppendLine($"# Canonical targets and header glossary ({glossary.GlossaryVersion}; target: printed aliases | value aliases)");
        foreach (var (field, entry) in glossary.Fields.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var aliases = string.Join(", ", entry.Aliases.OrderByDescending(a => a.Confidence).Take(8).Select(a => a.Header));
            var values = entry.ValueAliases is null ? "" : " | " + string.Join("; ", entry.ValueAliases.Select(v => $"{v.Key}: {string.Join(", ", v.Value.Select(a => a.Value))}"));
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {field}: {aliases}{values}");
        }

        sb.AppendLine("- claim.rawStatus: the printed status text (normalizer text), next to claim.status (enum)");
        sb.AppendLine("Composite header words (component + measure → claim.{component}.{measure}; a measure alone → claim.total.{measure}):");
        foreach (var (name, words) in glossary.CompositeHeaders.Components.Concat(glossary.CompositeHeaders.Measures))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {name}: {string.Join(", ", words.Select(w => w.Word))}");
        }

        foreach (var word in glossary.CompositeHeaders.ConditionalComponents.Words)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- conditional: {word.Word} → {word.Component} only beside a separate {word.RequiresComponent} column");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"Ignored headers (list in ignoredHeaders, never map): {string.Join(", ", glossary.IgnoredHeaders.Headers.Select(h => h.Header))}");

        sb.AppendLine();
        sb.AppendLine($"# Code systems ({codeSets.CodeSetsVersion}; normalizer code + codeSystem)");
        foreach (var (system, table) in codeSets.Tables.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {system}: {table.Name} (width {table.CodeWidth})");
        }

        sb.AppendLine();
        sb.AppendLine("# CarrierFormat JSON Schema (source of truth; you write everything except the pipeline-owned fields "
            + string.Join(", ", ModelSchema.PipelineOwned) + ")");
        sb.AppendLine(ModelSchema.SpecSchemaText.Trim());
        sb.AppendLine();
        sb.AppendLine("# Worked example: a hand-written, verified spec for a different carrier (landscape PDF, one table per policy period)");
        sb.AppendLine(ModelSchema.ExampleSpecText.Trim());
        return sb.ToString();
    }

    // ------------------------------------------------------------------ per-document part

    /// <summary>The per-document part of a learn request (after the cached prefix).</summary>
    public static string LearnDocument(DocumentGraph graph, SubmissionContext? submission, CarrierFormat? prior, string reason)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Task");
        sb.AppendLine(prior is null
            ? "Write a CarrierFormat spec for this document's format (a carrier/layout not in format memory)."
            : $"This carrier's last known spec ({prior.Ref}) no longer fits: {reason}. Propose the minimal change to it for the new layout; keep everything that still applies.");
        AppendDocument(sb, graph, submission);
        if (ConstructHints(graph) is { Count: > 0 } hints)
        {
            sb.AppendLine("## Layout constructs this document shows (ADR 0018; INSTRUCTION lines come from ingestion evidence and are not optional)");
            hints.ForEach(h => sb.AppendLine(h));
        }

        if (graph.HeaderFields is { Count: > 0 } header)
        {
            // Brief 23 §1: the header block's label/value pairs, read deterministically at ingestion.
            sb.AppendLine("## Header block (ingestion evidence: printed label → field; locate each with documentFields {\"type\": \"headerLabel\", \"label\": <the printed label>})");
            foreach (var h in header)
            {
                sb.AppendLine(h.Field == HeaderFields.LossWindow
                    ? $"- \"{h.Label}\" (page {h.Page}): a losses window, the dates of the claims listed. It is not a policy period: never map it to period dates."
                    : $"- \"{h.Label}\" (page {h.Page}) → {h.Field}: \"{h.Value}\"{(h.Field.StartsWith("period.", StringComparison.Ordinal) ? " (a single-period run's header: map it as a documentFields entry with this target)" : "")}");
            }
        }

        if (prior is not null)
        {
            sb.AppendLine("## Prior spec");
            sb.AppendLine(FormatJson.Write(prior));
        }

        return sb.ToString();
    }

    /// <summary>
    /// ADR 0018: the construct hints the document's own signals call for — stacked headers, component-sum headers, two
    /// header rows of captions ("Loss State: Loss Paid:"), three or more small tables with the same header (one table per
    /// claim), a close date without a status column, a document label paragraph followed by its value.
    /// </summary>
    internal static List<string> ConstructHints(DocumentGraph graph)
    {
        var glossary = CanonicalData.Glossary;
        var hints = new List<string>();
        var headers = graph.Tables.SelectMany(t => t.HeaderTexts).Where(h => h.Trim().Length > 0).ToList();
        // Brief 19 Answers 3: records and stacked cells are stated as instructions when ingestion found the evidence
        // (LayoutEvidenceDetector), with the evidence itself; the remaining constructs stay hints from header signals.
        var evidence = graph.LayoutEvidence ?? [];
        var measures = evidence.FirstOrDefault(e => e.Construct == "measures-as-rows");
        if (measures is not null)
        {
            // Brief 22 Answers 1: the amounts rows under a claim row are its measures, so records-rows evidence is the same rows.
            hints.Add("- INSTRUCTION: each claim (and each total) prints one row per measure, labelled in a cell (Inc / Pd / O/S). Set tableLocator.records "
                + "with \"mode\": \"measures\" (keySource: the claim number column; measureLabels only when the labels differ from Inc / Pd / O/S). Map each "
                + "component column to the component without a measure: claim.indemnity, claim.medical, claim.expense, claim.total. A column headed \"Claim\" "
                + "or \"Loss\" beside Medical and Expense is claim.indemnity here, not the claim number. Do not map the label column (list its header in ignoredHeaders when it has one). "
                + $"Evidence from ingestion ({measures.TableId}, page {measures.Page}): {measures.Evidence}.");
        }

        foreach (var (construct, mode) in measures is null ? new[] { ("records-rows", "rows"), ("records-lines", "lines") } : [])
        {
            if (evidence.FirstOrDefault(e => e.Construct == construct) is { } found)
            {
                hints.Add($"- INSTRUCTION: this document's claim table uses the multi-line record construct. Set tableLocator.records "
                    + $"with \"mode\": \"{mode}\". Evidence from ingestion ({found.TableId}, page {found.Page}): {found.Evidence}.");
                if (mode == "lines" && found.TableId is { } evidenced && LinesBelow(graph, evidenced) is { Count: > 0 } below)
                {
                    // Brief 20 §2: the model saw CU's paragraphs; lineFields run on these lines, so it writes patterns for them.
                    hints.Add("  The printed lines below that table, exactly as replay reads them (\" | \" separates values printed apart on one line; "
                        + "a lineFields pattern is tried on each line and on each part). Write each lineFields pattern for these lines and as a shape "
                        + "(letters, digit runs, separators), never one printed value:\n" + string.Join("\n", below.Select(l => $"    {l}")));
                }

                hints.Add(RecordsHint);
                break;
            }
        }

        if (evidence.FirstOrDefault(e => e.Construct == "claims-with-period-totals") is { } periodTotals)
        {
            // Brief 20 Answers 2: a layout fact the detector states, so the learn does not alternate between the two readings.
            hints.Add("- INSTRUCTION: claim rows are the records; the period rows are totals to tie against. Keep tableLocator.rowShape \"claims\" "
                + "(never \"periodSummary\"), and read the period totals as rowKinds subtotal / grandTotal rows, or through periods.lookup.subtotals "
                + $"when they are a separate period table, so the claims reconcile to them (R-10 / R-12). Evidence from ingestion ({periodTotals.TableId}, page {periodTotals.Page}): {periodTotals.Evidence}.");
        }

        if (evidence.FirstOrDefault(e => e.Construct == "stacked") is { } stacked)
        {
            hints.Add($"- INSTRUCTION: map each stacked header twice with \"part\": 0 and \"part\": 1. Evidence from ingestion ({stacked.TableId}, page {stacked.Page}): {stacked.Evidence}.");
            hints.Add(StackedHint);
        }

        if (headers.Any(h => glossary.ResolveHeader(h) is { Method: "component-sum" }))
        {
            hints.Add(ComponentSumHint);
        }

        var fields = graph.Tables.SelectMany(t => t.HeaderFields).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (fields.Contains("claim.dateClosed") && !fields.Contains("claim.status"))
        {
            hints.Add(StatusRuleHint);
        }

        string[] labelled = ["document.valuationDate", "document.insuredName", "document.reportRunDate"];
        var labels = labelled.SelectMany(f => glossary.Fields.TryGetValue(f, out var e) ? e.Aliases.Select(a => CanonicalComparer.NormalizeText(a.Header)) : [])
            .ToHashSet(StringComparer.Ordinal);
        if (graph.Paragraphs.Any(p => labels.Contains(CanonicalComparer.NormalizeText(p.Text))))
        {
            hints.Add(LabelNextHint);
        }

        return hints;
    }

    /// <summary>The per-document part of a delta request: only the unmapped headers, sample values, and the existing column map.</summary>
    public static string DeltaDocument(DocumentGraph graph, CarrierFormat spec, IReadOnlyList<string> unmapped)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Task (delta)");
        sb.AppendLine($"Format {spec.Ref} ({spec.CarrierName}) replays this document except for the unmapped headers below. Return column-map");
        sb.AppendLine("additions for these headers only (source = the header exactly as printed) and list in ignoredHeaders any of them that");
        sb.AppendLine("carry nothing the canonical schema stores. Do not change existing entries.");
        sb.AppendLine();
        sb.AppendLine("## Unmapped headers with sample values (up to 3 claim rows each)");
        foreach (var header in unmapped)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- \"{header}\": {string.Join(" | ", SampleValues(graph, header).Select(v => $"\"{v}\""))}");
        }

        sb.AppendLine();
        sb.AppendLine("## Existing column map");
        foreach (var column in spec.ColumnMap)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- \"{column.Source}\" → {column.Target} ({column.Normalizer.Type}{(column.Normalizer.GlossaryField is { } g ? $", {g}" : "")})");
        }

        return sb.ToString();
    }

    /// <summary>First non-empty values under a printed header (claim rows of the tables that print it).</summary>
    public static IReadOnlyList<string> SampleValues(DocumentGraph graph, string header, int max = 3)
    {
        var key = CanonicalComparer.NormalizeText(header);
        var values = new List<string>();
        foreach (var table in graph.Tables)
        {
            var index = table.HeaderTexts.FindIndex(h => CanonicalComparer.NormalizeText(h) == key);
            if (index < 0)
            {
                continue;
            }

            var cells = graph.Cells.Where(c => c.TableId == table.Id && (table.SheetName is null || c.Sheet == table.SheetName)).ToList();
            var top = cells.Count == 0 ? 0 : cells.Min(c => c.Row);
            var left = table.SheetName is null ? 0 : cells.Count == 0 ? 0 : cells.Min(c => c.Column);
            var headerRows = table.HeaderRows.Select(r => r + (table.SheetName is null ? 0 : top)).ToHashSet();
            values.AddRange(cells.Where(c => !headerRows.Contains(c.Row) && c.Column - left == index && c.RawValue.Trim().Length > 0)
                .OrderBy(c => c.Row).Select(c => c.RawValue.Trim()));
        }

        return values.Distinct(StringComparer.Ordinal).Take(max).ToList();
    }

    /// <summary>Up to eight printed lines below a table, on its page, as replay rebuilds them from the words (parts joined by " | ").</summary>
    internal static List<string> LinesBelow(DocumentGraph graph, string tableId)
    {
        if (graph.Tables.FirstOrDefault(t => t.Id == tableId) is not { Polygon: { Count: >= 8 } polygon } table)
        {
            return [];
        }

        var bottom = polygon.Where((_, i) => i % 2 == 1).Max();
        return Formats.Replay.PrintedText.Lines(graph, w => w.Page == table.Page).Where(l => l.Top > bottom).OrderBy(l => l.Top).Take(8)
            .Select(l => string.Join(" | ", l.Parts)).ToList();
    }

    private static void AppendDocument(StringBuilder sb, DocumentGraph graph, SubmissionContext? submission)
    {
        var scanned = WordConfidence.Mean(graph.Words) is { } mean && mean < 0.995; // brief 31: words without a confidence are unknown, not 0
        sb.AppendLine();
        sb.AppendLine("## Document");
        sb.AppendLine(CultureInfo.InvariantCulture, $"fileKind: {(graph.SourceFile.Kind is FileKind.Xlsx or FileKind.Xls or FileKind.Csv ? "spreadsheet" : "pdf")} ({graph.SourceFile.Kind}); pages/sheets: {graph.Pages.Count}; text source: {(graph.SourceFile.Kind is not (FileKind.Xlsx or FileKind.Xls or FileKind.Xlsb or FileKind.Csv) ? scanned ? "OCR (scanned)" : "text layer" : "cells")}");
        if (submission is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"submission: carrierHint={submission.CarrierHint ?? "none"}; uploaderRole={submission.UploaderRole}");
        }

        var pages = graph.Pages.OrderBy(p => p.Number).Take(3).ToList();
        var markdown = string.Join("\n\n", pages.Where(p => p.Markdown is not null).Select(p => $"<!-- page {p.Number}{(p.SheetName is null ? "" : $" sheet {p.SheetName}")} -->\n{p.Markdown}"));
        if (markdown.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Layout markdown (first pages)");
            sb.AppendLine(markdown.Length > MaxMarkdownChars ? markdown[..MaxMarkdownChars] + "\n…(truncated)" : markdown);
        }

        var firstPages = pages.Select(p => p.Number).ToHashSet();
        var paragraphs = graph.Paragraphs.Where(p => firstPages.Contains(p.Page)).OrderBy(p => p.Page).ThenBy(p => p.SpanOffset ?? 0).Take(MaxParagraphs).ToList();
        if (paragraphs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Paragraphs (page, role: exact text) — candidates for documentFields and blockAnchor");
            sb.AppendLine("Regexes run on each paragraph's text and on the printed lines rebuilt from the words (CU merges some printed lines into one paragraph). "
                + "The role before the colon and the layout markdown's \"#\" and <!-- --> comments are markup, not printed text: never put them in a regex. "
                + "Anchor a value on its own printed label (\"^Insured:\\s*(?<value>.+?)\\s*$\") and capture a shape, never this document's value.");
            foreach (var p in paragraphs)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- p{p.Page} {p.Role ?? "-"}: {p.Text}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Tables (header cells with column index and left x; rows as replay sees them)");
        if (graph.Tables.Count == 0)
        {
            sb.AppendLine("No tables were detected. The claim rows must come from tableLocator.lineFallback (OCR lines); use the header words as printed.");
        }

        foreach (var table in graph.Tables)
        {
            var cells = graph.Cells.Where(c => c.TableId == table.Id && (table.SheetName is null || c.Sheet == table.SheetName)).ToList();
            sb.AppendLine(CultureInfo.InvariantCulture, $"### {table.Id} page {table.Page}{(table.SheetName is null ? "" : $" sheet \"{table.SheetName}\" {table.Range}")}: {table.RowCount} rows x {table.ColumnCount} columns, header rows [{string.Join(",", table.HeaderRows)}]");
            var top = cells.Count == 0 ? 0 : cells.Min(c => c.Row);
            var left = table.SheetName is null ? 0 : cells.Count == 0 ? 0 : cells.Min(c => c.Column);
            var headerRows = table.HeaderRows.Select(r => r + (table.SheetName is null ? 0 : top)).ToHashSet();
            for (var i = 0; i < table.HeaderTexts.Count; i++)
            {
                var headerCell = cells.Where(c => headerRows.Contains(c.Row) && c.Column - left == i).MaxBy(c => c.Row);
                var x = headerCell?.Polygon is { Count: >= 2 } poly ? $" x={poly.Where((_, k) => k % 2 == 0).Min():0.00}" : "";
                var resolved = table.HeaderFields.ElementAtOrDefault(i) is { } f ? $" (glossary: {f})" : "";
                sb.AppendLine(CultureInfo.InvariantCulture, $"  [{i}] \"{table.HeaderTexts[i]}\"{x}{(headerCell?.A1 is { } a1 ? $" {a1}" : "")}{resolved}");
            }

            var rows = cells.Where(c => !headerRows.Contains(c.Row)).GroupBy(c => c.Row).OrderBy(g => g.Key)
                .Select(g => string.Join(" ", g.OrderBy(c => c.Column).Where(c => c.RawValue.Trim().Length > 0).Select(c => c.RawValue.Trim())))
                .Where(t => t.Length > 0).ToList();
            var shown = rows.Count <= 8 ? rows : [.. rows.Take(5), "…", .. rows.TakeLast(3)];
            foreach (var row in shown)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  row: {row}");
            }
        }

        if (graph.SourceFile.Kind is FileKind.Xlsx or FileKind.Xls)
        {
            sb.AppendLine();
            sb.AppendLine("## Cells outside tables (sheet!A1: value) — candidates for documentFields");
            foreach (var cell in graph.Cells.Where(c => c.TableId is null).OrderBy(c => c.Sheet, StringComparer.Ordinal).ThenBy(c => c.Row).ThenBy(c => c.Column).Take(60))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {cell.Sheet}!{cell.A1}: {cell.TypedValue ?? cell.RawValue}");
            }
        }
    }
}
