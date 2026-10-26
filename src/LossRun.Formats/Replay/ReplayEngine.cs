using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LossRun.Contracts;
using LossRun.Rules;

namespace LossRun.Formats.Replay;

/// <param name="SummaryRows">Rows read as period-summary records (<c>tableLocator.rowShape: periodSummary</c>, ADR 0017).</param>
/// <param name="NoLossesPeriods">Periods carrying a "no losses" statement (ADR 0016).</param>
public sealed record ReplayStats(int TablesUsed, int ClaimRows, int SubtotalRows, int GrandTotalRows, int LineFallbackBlocks, int LineFallbackRows, int SkippedRows,
    int SummaryRows = 0, int NoLossesPeriods = 0, int KeptRows = 0);

/// <summary>Output of a deterministic replay. Contains no clock or random values: same graph + spec → identical bytes.</summary>
public sealed class ReplayResult
{
    public string FormatRef { get; init; } = "";
    public string DocumentId { get; init; } = "";
    public string CatalogVersion { get; init; } = "";
    public LossRunDocument Document { get; init; } = new();

    /// <summary>Printed headers of used tables that the format neither maps nor ignores (drives ReplayWithDelta).</summary>
    public List<string> UnmappedHeaders { get; init; } = [];

    /// <summary>Values the format could not normalize or locate (e.g. an enum value missing from the glossary).</summary>
    public List<string> Issues { get; init; } = [];

    /// <summary>
    /// Brief 30 item 4: the printed values that did not normalize, each with its cell (page and polygon, or sheet and cell). The
    /// value is dropped from the document, so the router sends the document to Review instead of accepting it with a gap.
    /// </summary>
    public List<NormalizationIssue> Unnormalized { get; init; } = [];

    /// <summary>
    /// Brief 20 (learn feedback only, not serialized): the printed lines, as replay reads them, of the first claim records
    /// whose <c>lineFields</c> claim-number pattern matched none of their lines; parts of a line are joined with " | ".
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<string> UnmatchedClaimNumberRecords { get; init; } = [];

    public IReadOnlyList<RuleOutcome> RuleOutcomes { get; init; } = [];
    public ReplayStats Stats { get; init; } = new(0, 0, 0, 0, 0, 0, 0);

    /// <summary>OCR-misread headers read as the spec source they nearly match ("Clalm No" → "Claim No"; scans only).</summary>
    public Dictionary<string, string> OcrHeaderMatches { get; init; } = [];

    /// <summary>
    /// Deterministic repairs of the table structure CU returned (brief 08c): a blank header cell read as the source its
    /// sibling tables print there, a total row whose money cells sit one column off read where it reconciles.
    /// </summary>
    public List<string> StructuralRepairs { get; init; } = [];

    /// <summary>A header was repaired (blank cell inferred or merged cell split): header drift it explains is not a new layout.</summary>
    public bool HeadersRepaired { get; init; }

    /// <summary>
    /// Columns mapped through a conditional component word without the column that gives it a reading (brief 17 Answers 2:
    /// "Loss Paid" → indemnity with no separate medical column). Not publishable or acceptable unless printed totals confirm.
    /// </summary>
    public List<string> AmbiguousComposites { get; init; } = [];
    public double MinFieldConfidence { get; init; }
    public double MeanFieldConfidence { get; init; }

    /// <summary>Always 0: replay never calls a model (brief 05).</summary>
    public int ModelCalls { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, CanonicalJson.Options);
}

/// <summary>
/// Executes a CarrierFormat against a document graph with no model call: locates claim tables (or rebuilds them from
/// OCR lines when CU returned none for a block), maps columns through normalizers into canonical paths, applies derived
/// rules, records lineage (page+polygon or sheet+A1, formatId@version, extractor) and runs the rule catalog.
/// </summary>
public static partial class ReplayEngine
{
    private static readonly string[] MoneyRoots = ["indemnity.", "medical.", "expense.", "total."];

    public static ReplayResult Execute(DocumentGraph graph, CarrierFormat spec, RuleContext? context = null, Glossary? glossary = null, RuleEvaluator? rules = null,
        SubmissionContext? submission = null)
    {
        glossary ??= CanonicalData.Glossary;
        rules ??= RuleEvaluator.Default;
        spec = ExpandMeasureColumns(spec);
        var run = new Run(graph, spec, glossary, submission);
        run.Build();

        var document = TypedDocument(run);
        document.Lineage = run.Lineage;
        var evaluation = rules.Evaluate(document, context);
        var confidences = run.Lineage.Values.Select(l => l.Confidence ?? 1.0).ToList();

        return new ReplayResult
        {
            FormatRef = spec.Ref,
            DocumentId = graph.DocumentId,
            CatalogVersion = evaluation.CatalogVersion,
            Document = document,
            UnmappedHeaders = run.Unmapped,
            OcrHeaderMatches = run.OcrHeaderMatches,
            StructuralRepairs = run.StructuralRepairs,
            HeadersRepaired = run.HeadersRepaired,
            AmbiguousComposites = AmbiguousComposites(spec, glossary),
            Issues = run.Issues,
            Unnormalized = run.Unnormalized,
            UnmatchedClaimNumberRecords = run.UnmatchedClaimNumberRecords,
            RuleOutcomes = evaluation.Outcomes,
            Stats = run.Stats(),
            MinFieldConfidence = confidences.Count == 0 ? 0 : Math.Round(confidences.Min(), 4),
            MeanFieldConfidence = confidences.Count == 0 ? 0 : Math.Round(confidences.Average(), 4),
            ModelCalls = 0,
        };
    }

    /// <summary>The canonical money components a records-mode-<c>measures</c> column may name without a measure.</summary>
    public static readonly string[] MeasureComponents = ["claim.indemnity", "claim.medical", "claim.expense", "claim.total"];

    /// <summary>
    /// Records mode <c>measures</c> (brief 22 Answers 1): each column-map entry naming a component without a measure
    /// (<c>claim.indemnity</c>) becomes one entry per measure (<c>claim.indemnity.paid</c>, …), each reading the value its
    /// column prints on that measure's row. The published spec is not changed: replay works on a copy.
    /// </summary>
    private static CarrierFormat ExpandMeasureColumns(CarrierFormat spec)
    {
        if (spec.TableLocator.Records is not { Mode: "measures" } || !spec.ColumnMap.Any(c => MeasureComponents.Contains(c.Target)))
        {
            return spec;
        }

        var copy = FormatJson.Parse(FormatJson.Write(spec));
        copy.ColumnMap = [.. copy.ColumnMap.SelectMany(c => MeasureComponents.Contains(c.Target)
            ? new[] { "paid", "reserve", "incurred" }.Select(m => new ColumnMapping
            {
                Source = c.Source, Target = $"{c.Target}.{m}", Normalizer = c.Normalizer, NullWhen = c.NullWhen, Components = c.Components, RowMeasure = m,
            })
            : [c])];
        return copy;
    }

    /// <summary>Column-map entries that read a conditional component word without the column it requires (brief 17 Answers 2).</summary>
    private static List<string> AmbiguousComposites(CarrierFormat spec, Glossary glossary) =>
        spec.ColumnMap
            .Select(c => (Column: c, Match: glossary.ResolveHeader(c.Source)))
            .Where(x => x.Match is { RequiresComponent: { } required } m
                && x.Column.Target.StartsWith(m.Field[..m.Field.LastIndexOf('.')], StringComparison.Ordinal)
                && !spec.ColumnMap.Any(c => c.Target.StartsWith($"claim.{required}.", StringComparison.Ordinal)))
            .Select(x => $"ambiguous-composite: \"{x.Column.Source}\" read as {x.Column.Target} without a separate {x.Match!.RequiresComponent} column")
            .ToList();

    /// <summary>
    /// Deserializes the replayed document. A value whose type does not fit its canonical field (e.g. a date read with
    /// a text normalizer) is dropped and reported as an issue naming the field, instead of failing the whole replay
    /// (brief 08: learned candidates did this, and the bare serializer message gave the model nothing to fix).
    /// </summary>
    private static LossRunDocument TypedDocument(Run run)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return CanonicalJson.Deserialize(run.Document.ToJsonString(CanonicalJson.Options));
            }
            catch (JsonException e) when (e.Path is { Length: > 2 } path && attempt < 50)
            {
                if (path.Contains(".claims[", StringComparison.Ordinal))
                {
                    // A column of wrong-typed values fails on every row: clean each claim once (linear), not one row per
                    // full re-parse (08b: a 60-row CSV with an unconvertible claimType exceeded the retry cap and crashed).
                    CleanClaims(run);
                    continue;
                }

                Drop(run, path[2..]);
            }
        }
    }

    private static void Drop(Run run, string lineagePath)
    {
        var value = RemovePath(run.Document, lineagePath);
        var lineage = run.Lineage.GetValueOrDefault(lineagePath);
        run.Lineage.Remove(lineagePath);
        run.Issues.Add($"{lineagePath}: value {value?.ToJsonString() ?? "?"} does not fit the canonical field's type (check the normalizer: dates need 'date', amounts 'currency', counts 'integer', enums a glossary value, WCIO codes 'code' into <field>.code)");
        // Brief 32 item 2: a printed value dropped on the type check is routed like any other unnormalized value (brief 30 item 4), with its cell.
        run.Unnormalized.Add(new NormalizationIssue(lineagePath, lineage?.SourceText ?? value?.ToJsonString() ?? "", "type", lineage?.Page, lineage?.Polygon, lineage?.Sheet, lineage?.Cell));
    }

    private static void CleanClaims(Run run)
    {
        var periods = run.Document["policyPeriods"] as JsonArray ?? [];
        for (var p = 0; p < periods.Count; p++)
        {
            var claims = periods[p]?["claims"] as JsonArray ?? [];
            for (var c = 0; c < claims.Count; c++)
            {
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    try
                    {
                        _ = claims[c]!.Deserialize<Claim>(CanonicalJson.Options);
                        break;
                    }
                    catch (JsonException e) when (e.Path is { Length: > 2 } inner)
                    {
                        Drop(run, $"policyPeriods[{p}].claims[{c}].{inner[2..]}");
                    }
                }
            }
        }
    }

    /// <summary>Removes the node at a serializer path such as <c>policyPeriods[0].claims[3].dateClosed</c>; returns it.</summary>
    private static JsonNode? RemovePath(JsonObject root, string path)
    {
        JsonNode? node = root;
        var parts = path.Split('.');
        for (var i = 0; i < parts.Length; i++)
        {
            var name = parts[i].Split('[')[0];
            var indexes = parts[i].Split('[').Skip(1).Select(x => int.Parse(x.TrimEnd(']'), CultureInfo.InvariantCulture)).ToList();
            if (i == parts.Length - 1 && indexes.Count == 0)
            {
                var parent = (JsonObject)node!;
                var removed = parent[name];
                parent.Remove(name);
                return removed;
            }

            node = (node as JsonObject)?[name];
            foreach (var index in indexes)
            {
                node = (node as JsonArray)?[index];
            }
        }

        throw new JsonException($"Cannot remove {path}.");
    }

    private sealed class Block
    {
        public GraphParagraph? Anchor { get; init; }
        public Match? AnchorMatch { get; init; }
        public List<GraphTable> Tables { get; } = [];
        public List<SourceRow> Rows { get; } = [];
    }

    private sealed class PeriodBuild
    {
        public JsonObject Node { get; } = [];
        public List<JsonObject> Claims { get; } = [];

        /// <summary>Brief 23 §2: rows of another line of business, kept, not extracted.</summary>
        public List<JsonObject> KeptRows { get; } = [];
        public JsonObject? Subtotals { get; set; }
        public int Index { get; init; }
        public string? Key { get; init; }

        /// <summary>The period prints a "no losses" statement (ADR 0016).</summary>
        public bool NoLosses { get; set; }
    }

    private sealed partial class Run(DocumentGraph graph, CarrierFormat spec, Glossary glossary, SubmissionContext? submission)
    {
        private readonly List<PeriodBuild> _periods = [];
        private readonly Dictionary<int, List<GraphWord>> _wordsByPage = graph.Words.GroupBy(w => w.Page).ToDictionary(g => g.Key, g => g.ToList());
        // Headers a used table prints on its key line (ADR 0018: a record line ≥ 1 is addressed through its line-0 header).
        private readonly HashSet<string> _sources = spec.ColumnMap.Select(c => Key(c.Line > 0 && c.Under is { } under ? under : c.Source)).ToHashSet(StringComparer.Ordinal);
        private readonly Dictionary<string, string[][]> _headerRows = new(StringComparer.Ordinal);
        private readonly HashSet<string> _ignored = spec.IgnoredHeaders.Select(Key).ToHashSet(StringComparer.Ordinal);
        private readonly Dictionary<string, string?[]> _columnKeys = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<int, string[]>> _splits = new(StringComparer.Ordinal);
        private readonly bool _scanned = OcrHeaders.IsScanned(graph);
        private JsonObject? _reportTotals;
        private readonly List<(SourceRow Row, PeriodBuild? Period, string BasePath, JsonObject Totals)> _totalsRows = [];
        private int _tablesUsed, _claimRows, _subtotalRows, _grandRows, _fallbackBlocks, _fallbackRows, _skipped, _summaryRows;
        private readonly Regex _noLosses = new(spec.Periods.NoLossesPattern ?? NoLossesDefault(glossary), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private bool PeriodSummary => spec.TableLocator.RowShape == "periodSummary";

        public JsonObject Document { get; } = [];
        public Dictionary<string, LineageRecord> Lineage { get; } = new(StringComparer.Ordinal);
        public List<string> Unmapped { get; } = [];
        public Dictionary<string, string> OcrHeaderMatches { get; } = new(StringComparer.Ordinal);
        public List<string> StructuralRepairs { get; } = [];
        public bool HeadersRepaired { get; private set; }
        public List<string> Issues { get; } = [];
        public List<NormalizationIssue> Unnormalized { get; } = [];
        public List<string> UnmatchedClaimNumberRecords { get; } = [];

        /// <summary>Brief 30 item 14: the WCIO table a coded field's <c>.code</c> declares (canonical-schema.json), used when the spec names none.</summary>
        private static readonly Dictionary<string, string> DeclaredCodeSystems = new(StringComparer.Ordinal)
        {
            ["claim.natureOfInjury.code"] = "WCIO-Nature", ["claim.causeOfInjury.code"] = "WCIO-Cause", ["claim.partOfBody.code"] = "WCIO-PartOfBody",
            ["claim.classCode"] = "WCIRB-ClassCode",
        };

        private static NormalizerSpec Coded(NormalizerSpec normalizer, string target) =>
            normalizer is { Type: "code", CodeSystem: null } && DeclaredCodeSystems.TryGetValue(target, out var system)
                ? new NormalizerSpec { Type = normalizer.Type, GlossaryField = normalizer.GlossaryField, Pattern = normalizer.Pattern, Format = normalizer.Format, CodeSystem = system }
                : normalizer;

        /// <summary>A code read into <c>{field}.code</c> carries its system in <c>{field}.codeSystem</c> (brief 30 item 14).</summary>
        private void SetCodeSystem(JsonObject claim, string path, NormalizerSpec normalizer)
        {
            if (normalizer is { Type: "code", CodeSystem: { } system } && path.EndsWith(".code", StringComparison.Ordinal) && Get(claim, path[..^5] + ".codeSystem") is null)
            {
                Set(claim, path[..^5] + ".codeSystem", JsonValue.Create(system));
            }
        }

        /// <summary>Brief 30 item 4: an issue the reviewer sees and the router acts on, with the cell it came from.</summary>
        private void Unnormalizable(string fieldPath, string raw, string normalizer, SourceCell? source, string issue)
        {
            Issues.Add(issue);
            Unnormalized.Add(new NormalizationIssue(fieldPath, raw, normalizer, source?.Sheet is null ? source?.Page : null, source?.Polygon, source?.Sheet, source?.A1));
        }

        public ReplayStats Stats() => new(_tablesUsed, _claimRows, _subtotalRows, _grandRows, _fallbackBlocks, _fallbackRows, _skipped, _summaryRows,
            _periods.Count(p => p.NoLosses), _keptRows);

        /// <summary>
        /// The default per-period "no losses" pattern (ADR 0016): the glossary's multi-word statement aliases plus the
        /// phrasings real runs print ("No claims exist for this policy year", "No claims found", "Loss free"). A spec
        /// overrides it with <c>periods.noLossesPattern</c>. A one-word alias ("NONE") is too weak to scan for.
        /// </summary>
        private static string NoLossesDefault(Glossary glossary)
        {
            var aliases = glossary.Fields.TryGetValue("document.noLossesStatement", out var field)
                ? field.Aliases.Select(a => a.Header.Trim()).Where(h => h.Contains(' ', StringComparison.Ordinal)).Select(Regex.Escape)
                : [];
            return string.Join("|", aliases.Append(@"\bno\s+(?:reported\s+)?(?:claims?|losses)\b(?:\s+(?:exist|found|reported|filed|on\s+file|to\s+report))?")
                .Append(@"\bloss[\s-]free\b"));
        }

        /// <summary>A row that is a "no losses" statement, not a claim: it matches the pattern and prints no amount.</summary>
        private bool NoLossesRow(SourceRow row) => _noLosses.IsMatch(row.Text) && !Amount().IsMatch(row.Text);

        private void MarkNoLosses(PeriodBuild period, SourceCell? source, string text, string extractor = "replay")
        {
            if (period.NoLosses)
            {
                return;
            }

            period.NoLosses = true;
            SetLineage($"policyPeriods[{period.Index}].noLossesStatement", source, extractor, text, source?.Confidence ?? 1.0);
        }

        /// <summary>
        /// Block-anchored runs print the statement as a paragraph under the period heading ("No claims exist for this policy
        /// year"): any paragraph after the block's anchor and before the next one that matches marks the block's period.
        /// </summary>
        private void NoLossesParagraphs(List<Block> blocks, int index, PeriodBuild period)
        {
            var anchor = blocks[index].Anchor!;
            var next = blocks.Skip(index + 1).FirstOrDefault(b => b.Anchor is not null)?.Anchor;
            static (int, double) At(GraphParagraph p) => (p.Page, Top(p.Polygon));
            bool After((int Page, double Top) a, (int Page, double Top) b) => a.Page > b.Page || (a.Page == b.Page && a.Top > b.Top);
            var statement = graph.Paragraphs
                .Where(p => p != anchor && After(At(p), At(anchor)) && (next is null || After(At(next), At(p))))
                .OrderBy(p => p.Page).ThenBy(p => Top(p.Polygon))
                .FirstOrDefault(p => _noLosses.IsMatch(p.Text) && !Amount().IsMatch(p.Text));
            if (statement is not null)
            {
                MarkNoLosses(period, FromParagraph(statement, statement.Text), statement.Text);
            }
        }

        /// <summary>
        /// The document-level "no losses" locator found the statement: true, or (a learned spec reading it with a text locator,
        /// brief 19 constructs run) a non-empty printed text.
        /// </summary>
        private bool StatementPrinted() => Document["noLossesStatement"] is JsonValue v
            && (v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text));

        /// <summary>
        /// The document-level statement (documentFields locator) becomes per-period (ADR 0016): when no period carries its
        /// own, it belongs to the periods without claim rows, or, when every period has rows, to all of them (the run says
        /// "no losses" and lists claims: R-14 fails each period). The document flag is then true only when every period says so.
        /// </summary>
        private void AttributeNoLosses()
        {
            if (_periods.Count == 0)
            {
                return;
            }

            var unattributed = false;
            if (StatementPrinted() && !_periods.Any(p => p.NoLosses))
            {
                var empty = _periods.Where(p => p.Claims.Count == 0).ToList();
                var source = Lineage.GetValueOrDefault("noLossesStatement");
                // With one period the statement is that period's (rows under it fail R-14). With several, every one listing
                // rows, it is about a term the replay did not read as a period (brief 18 real-sample run): it stays
                // unattributed rather than failing every period.
                var targets = empty.Count > 0 ? empty : _periods.Count == 1 ? _periods : [];
                unattributed = targets.Count == 0;
                if (unattributed)
                {
                    Document["unattributedNoLossesStatement"] = true; // R-28 flags it for the reviewer (brief 18 Answers 3)
                    SetLineage("unattributedNoLossesStatement", null, "replay", "a 'no losses' statement no policy period could take", 1.0);
                }
                foreach (var period in targets)
                {
                    MarkNoLosses(period, null, $"document-level statement \"{source?.SourceText}\" applied to the period{(empty.Count > 0 ? " (no claim rows)" : " (the only period)")}", "spec");
                }
            }

            var all = _periods.All(p => p.NoLosses);
            if (!(Document["noLossesStatement"] is JsonValue current && current.TryGetValue<bool>(out var flag) && flag == all))
            {
                Document["noLossesStatement"] = all;
                SetLineage("noLossesStatement", null, "replay", unattributed
                    ? "a 'no losses' statement was printed but every policy period lists claims: not attributed to a period (ADR 0016)"
                    : all ? "every policy period states no losses" : "not every policy period states no losses (ADR 0016: the statement is per period)", 1.0);
            }
        }

        public void Build()
        {
            Document["schemaVersion"] = CanonicalData.SchemaVersion;
            Document["documentId"] = graph.DocumentId;
            DocumentFields();
            TotalComposition();

            var blocks = spec.FileKind == "spreadsheet" ? SpreadsheetBlocks() : PdfBlocks();
            for (var b = 0; b < blocks.Count; b++)
            {
                var block = blocks[b];
                PeriodBuild? blockPeriod = block.AnchorMatch is null ? null : AnchorPeriod(block);
                if (blockPeriod is not null)
                {
                    NoLossesParagraphs(blocks, b, blockPeriod);
                }

                foreach (var row in Records(blocks, b))
                {
                    if (NoLossesRow(row))
                    {
                        // "2021-2022  No claims exist for this policy year": the period's statement, not a claim (ADR 0016).
                        if ((PeriodSummary ? null : blockPeriod ?? ColumnPeriod(row)) is { } stated)
                        {
                            MarkNoLosses(stated, row.Label is { } label ? label with { Text = row.Text } : null, row.Text);
                            RowLevelValues(row, stated); // an export's statement row still carries the period's dates
                        }

                        _skipped++;
                        continue;
                    }

                    var kind = spec.TableLocator.RowKinds.FirstOrDefault(k => SpecRegex.IsMatch(row.Text, k.Regex));
                    switch (kind?.Kind)
                    {
                        case "subtotal" when PeriodSummary:
                            // A period-summary table has no claims period for a subtotal row to close: the row is a period's
                            // summary (brief 18 real-sample run: a learned spec classified a run's 36 year rows so).
                            SummaryRow(row);
                            continue;
                        case "subtotal":
                            _subtotalRows++;
                            if (blockPeriod is not null)
                            {
                                blockPeriod.Subtotals = TotalsRow(row, kind, $"policyPeriods[{blockPeriod.Index}].subtotals");
                                _totalsRows.Add((row, blockPeriod, $"policyPeriods[{blockPeriod.Index}].subtotals", blockPeriod.Subtotals));
                            }

                            continue;
                        case "grandTotal":
                            _grandRows++;
                            _reportTotals = TotalsRow(row, kind, "reportTotals");
                            _totalsRows.Add((row, null, "reportTotals", _reportTotals));
                            continue;
                        case "skip":
                            _skipped++;
                            continue;
                    }

                    if (PeriodSummary)
                    {
                        SummaryRow(row);
                        continue;
                    }

                    var period = blockPeriod ?? ColumnPeriod(row);
                    if (period is null)
                    {
                        _skipped++;
                        continue;
                    }

                    ClaimRow(row, period);
                }
            }

            HeaderPeriodFields();
            if (spec.Periods.Lookup is not null)
            {
                ApplyLookup(spec.Periods.Lookup);
            }

            foreach (var totals in _totalsRows)
            {
                RealignTotals(totals.Row, totals.Period, totals.BasePath, totals.Totals);
            }

            ApplySubmissionContext();
            AttributeNoLosses();

            var periods = new JsonArray();
            foreach (var period in _periods)
            {
                var claims = new JsonArray();
                foreach (var claim in period.Claims)
                {
                    claims.Add(claim);
                }

                // A period that states no losses is a claims period with zero rows (ADR 0016), not a summary.
                period.Node["noLossesStatement"] = period.NoLosses;
                if (!period.NoLosses)
                {
                    SetLineage($"policyPeriods[{period.Index}].noLossesStatement", null, "spec", "no 'no losses' statement for the period (default false)", 1.0);
                }

                if (spec.Periods.Kind is { } periodKind && period.Node["periodKind"] is null)
                {
                    period.Node["periodKind"] = periodKind;
                    SetLineage($"policyPeriods[{period.Index}].periodKind", null, "spec", $"periods.kind {periodKind}", 1.0);
                }

                period.Node["detailLevel"] = period.Claims.Count > 0 || period.NoLosses ? "claims" : "summaryOnly";
                SetLineage($"policyPeriods[{period.Index}].detailLevel", null, "spec",
                    PeriodSummary ? "period-summary row (tableLocator.rowShape periodSummary)" : $"{period.Claims.Count} claim row(s) read", 1.0);
                period.Node["claims"] = claims;
                if (period.KeptRows.Count > 0)
                {
                    period.Node["keptRows"] = new JsonArray([.. period.KeptRows]);
                }
                if (period.Subtotals is not null)
                {
                    Derive(period.Subtotals, $"policyPeriods[{period.Index}].subtotals", "totals");
                    period.Node["subtotals"] = period.Subtotals;
                }

                periods.Add(period.Node);
            }

            Document["policyPeriods"] = periods;
            if (_reportTotals is not null)
            {
                Derive(_reportTotals, "reportTotals", "totals");
                Document["reportTotals"] = _reportTotals;
            }
        }

        // ------------------------------------------------------------------ document-level fields

        private void DocumentFields()
        {
            Document["noLossesStatement"] = false;
            Document["expenseTreatment"] = "unknown";
            foreach (var field in spec.DocumentFields.Where(f => !f.Target.StartsWith("period.", StringComparison.Ordinal)))
            {
                var path = Strip(field.Target, "document.");
                if (path == "carrierName" && field.Locator.Type == "const" && submission?.CarrierHint is { Length: > 0 } hint)
                {
                    // Letterhead-less exports: the submission's carrier wins over the format's default (brief 05 Answers 3).
                    Document["carrierName"] = hint;
                    SetLineage(path, null, "context", "submission context carrierHint", 1.0);
                    continue;
                }

                var (value, source, extractor) = Locate(field);
                if (value is null)
                {
                    Issues.Add($"document field {path}: not found ({field.Locator.Type})");
                    continue;
                }

                var normalized = field.Locator.Type is "const" or "regexPresent" ? value : ValueNormalizer.Normalize(field.Normalizer, value.GetValue<string>(), glossary);
                // Brief 31 Answers 1: a headerLabel read already repaired the name; its source cell keeps the printed text.
                var repairedName = field.Locator.Type == "headerLabel" && source?.Text is { } printedText && printedText != value.GetValue<string>();
                if (path is "insuredName" or "carrierName" && field.Locator.Type is not ("const" or "regexPresent") && graph.SourceFile.TextLayer != true
                    && normalized is JsonValue name && name.TryGetValue<string>(out var printedName) && HeaderFields.TitleCaseNameRepair(printedName) is var repaired && repaired != printedName)
                {
                    normalized = JsonValue.Create(repaired); // brief 31: OCR read the name's capital I as l (the printed text stays in lineage)
                    repairedName = true;
                }
                if (normalized is null)
                {
                    Unnormalizable(path, value.ToString(), field.Normalizer.Type, source, $"document field {path}: could not normalize '{value}'");
                    continue;
                }

                Set(Document, path, normalized);
                SetLineage(path, source, extractor, field.Locator.Type == "const" ? field.Reason : source?.Text, source?.Confidence ?? 1.0);
                if (repairedName)
                {
                    Lineage[path].Repair = "ocrRepair: l→I";
                }

                if (path == "valuationDate")
                {
                    Document["valuationDateSource"] = "printed";
                    SetLineage("valuationDateSource", source, extractor, "valuation date printed on the run", source?.Confidence ?? 1.0);
                }
            }

            if (!Lineage.ContainsKey("noLossesStatement"))
            {
                SetLineage("noLossesStatement", null, "spec", "no 'no losses' locator matched (default false)", 1.0);
            }

            Document["unattributedNoLossesStatement"] = false;
            SetLineage("unattributedNoLossesStatement", null, "spec", "every 'no losses' statement is tied to a period, or none is printed (default false)", 1.0);

            if (!Lineage.ContainsKey("expenseTreatment"))
            {
                SetLineage("expenseTreatment", null, "spec", "format does not declare expense treatment (default unknown)", 1.0);
            }
        }

        /// <summary>
        /// ADR 0018: a total column whose header names its components ("Total Incurred (Ind+Med)") sets what the document's
        /// totals sum; R-20 (all components) or R-29 (indemnity + medical) checks it on every claim.
        /// </summary>
        private void TotalComposition()
        {
            // Brief 19 Answers 1: a component the run prints no column for is recorded as absent, not as zero or null.
            var absent = spec.AbsentComponents.Select(c => c.Trim().ToLowerInvariant()).Where(c => c is "indemnity" or "medical" or "expense").Distinct().ToList();
            if (absent.Count > 0)
            {
                Document["componentPresence"] = new JsonObject(absent.Select(c => KeyValuePair.Create(c, (JsonNode?)new JsonObject { ["present"] = false })));
                foreach (var component in absent)
                {
                    SetLineage($"componentPresence.{component}.present", null, "spec", $"absentComponents: the run prints no {component} column", 1.0);
                }
            }

            if (spec.ColumnMap.FirstOrDefault(c => c.Target.StartsWith("claim.total.", StringComparison.Ordinal) && c.Components is { Count: >= 2 }) is not { } composite)
            {
                return;
            }

            var components = composite.Components!.Select(c => c.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            Document["totalComposition"] = components.SetEquals(["indemnity", "medical"]) ? "indemnityMedical" : "allComponents";
            SetLineage("totalComposition", null, "spec", $"\"{composite.Source}\" names {string.Join(" + ", components.Order(StringComparer.Ordinal))}", 1.0);
        }

        private (JsonNode? Value, SourceCell? Source, string Extractor) Locate(DocumentFieldSpec field)
        {
            var locator = field.Locator;
            switch (locator.Type)
            {
                case "const":
                    return (locator.Value is { } v ? JsonNode.Parse(v.GetRawText()) : null, null, "spec");
                case "paragraphRole":
                    var paragraph = graph.Paragraphs.FirstOrDefault(p => p.Role == locator.Role);
                    return paragraph is null ? (null, null, "replay") : (JsonValue.Create(paragraph.Text), FromParagraph(paragraph, paragraph.Text), "replay");
                case "regex":
                case "regexPresent":
                    foreach (var (text, source) in Texts())
                    {
                        var m = SpecRegex.Match(text, locator.Pattern!);
                        if (m.Success && locator.Type == "regexPresent")
                        {
                            return (JsonValue.Create(true), source with { Text = m.Value }, "replay");
                        }

                        if (m.Success && ValueOf(m) is { } found)
                        {
                            return (JsonValue.Create(found), source with { Text = found }, "replay");
                        }
                    }

                    return locator.Type == "regexPresent" ? (JsonValue.Create(false), null, "spec") : (null, null, "replay");
                case "cell":
                    var cell = graph.Cells.FirstOrDefault(c => SameSheet(c.Sheet, locator.Sheet) && c.A1 == locator.A1);
                    return cell is null ? (null, null, "replay") : (JsonValue.Create(Value(cell)), FromCell(cell), "replay");
                case "labelNext":
                    // ADR 0018: label and value printed as two paragraphs ("Valuation Date" / "9/30/2024"): the first of the next
                    // three paragraphs on the label's page that the field's normalizer accepts (a name: the next paragraph).
                    var labelIndex = graph.Paragraphs.FindIndex(p => Key(p.Text) == Key(locator.Label!));
                    if (labelIndex >= 0)
                    {
                        var labelParagraph = graph.Paragraphs[labelIndex];
                        foreach (var next in graph.Paragraphs.Skip(labelIndex + 1).Where(p => p.Page == labelParagraph.Page).Take(field.Normalizer.Type == "date" ? 3 : 1))
                        {
                            if (ValueNormalizer.Normalize(field.Normalizer, next.Text, glossary) is not null)
                            {
                                return (JsonValue.Create(next.Text.Trim()), FromParagraph(next, next.Text.Trim()), "replay");
                            }
                        }
                    }

                    return (null, null, "replay");
                case "headerLabel":
                    // Brief 23 §1: the value printed after a header-block label on its line, by the field's kind (HeaderFields).
                    return HeaderFields.Read(graph, locator.Label ?? "", field.Target) is { } header
                        ? (JsonValue.Create(header.Value), new SourceCell(header.Printed ?? header.Value, header.Page, header.Polygon, null, null, header.Confidence), "replay")
                        : (null, null, "replay");
                case "labelRight":
                    var label = graph.Cells.FirstOrDefault(c => SameSheet(c.Sheet, locator.Sheet) && Key(c.RawValue) == Key(locator.Label!));
                    var right = label is null ? null : graph.Cells.FirstOrDefault(c => c.Sheet == label.Sheet && c.Row == label.Row && c.Column == label.Column + 1);
                    return right is null ? (null, null, "replay") : (JsonValue.Create(Value(right)), FromCell(right), "replay");
                default:
                    throw new InvalidOperationException($"Unknown locator '{locator.Type}'.");
            }
        }

        /// <summary>The <c>value</c> group, or the whole match when the pattern has none (as lineFields read; brief 20). Blank is not a value.</summary>
        private static string? ValueOf(Match m) => (m.Groups["value"].Success ? m.Groups["value"].Value : m.Value).Trim() is { Length: > 0 } v ? v : null;

        /// <summary>Searchable texts in reading order: paragraphs (PDF) or cells (spreadsheet).</summary>
        private IEnumerable<(string Text, SourceCell Source)> Texts() =>
            spec.FileKind == "spreadsheet"
                ? graph.Cells.OrderBy(c => c.Page).ThenBy(c => c.Row).ThenBy(c => c.Column).Select(c => (c.RawValue, FromCell(c)))
                : graph.Paragraphs.OrderBy(p => p.Page).ThenBy(p => p.SpanOffset ?? 0).Select(p => (p.Text, FromParagraph(p, p.Text)))
                    .Concat(PrintedLinesOfDocument().SelectMany(l => (l.Parts.Count > 1 ? l.Parts : []).Append(l.Text)
                        .Select(t => (t, new SourceCell(t, l.Page, [.. l.Polygon], null, null, l.Confidence)))));

        private List<PrintedLine>? _printedLines;

        /// <summary>
        /// Brief 20: after the paragraphs, the printed lines rebuilt from the OCR words, so a regex anchored on a printed label
        /// still matches when CU merged that line into the paragraph before it. Values printed apart on one line ("Insured: …
        /// Valued as of: …") are tried part by part before the whole line. Paragraphs come first, so a spec that matched a
        /// paragraph before still reads the same value.
        /// </summary>
        private List<PrintedLine> PrintedLinesOfDocument() => _printedLines ??= PrintedText.Lines(graph);

        // ------------------------------------------------------------------ blocks and rows

        private List<Block> PdfBlocks()
        {
            var tables = graph.Tables.Where(Qualifies).ToList();
            _tablesUsed = tables.Count;
            CollectUnmapped(tables);

            var blocks = new List<Block>();
            if (spec.TableLocator.BlockAnchor is { } anchorPattern)
            {
                foreach (var paragraph in graph.Paragraphs.OrderBy(p => p.Page).ThenBy(p => Top(p.Polygon)))
                {
                    var m = SpecRegex.Match(paragraph.Text, anchorPattern);
                    if (m.Success)
                    {
                        blocks.Add(new Block { Anchor = paragraph, AnchorMatch = m });
                    }
                }
            }

            if (blocks.Count == 0)
            {
                blocks.Add(new Block());
            }

            foreach (var table in tables)
            {
                var owner = blocks.LastOrDefault(b => b.Anchor is null || b.Anchor.Page < table.Page || (b.Anchor.Page == table.Page && Top(b.Anchor.Polygon) < Top(table.Polygon)))
                    ?? blocks[0];
                owner.Tables.Add(table);
            }

            for (var i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                foreach (var table in block.Tables)
                {
                    block.Rows.AddRange(TableRows(table));
                }

                if (block.Tables.Count == 0 && block.Anchor is { } anchor && spec.TableLocator.LineFallback is { Enabled: true } fallback)
                {
                    var page = graph.Pages.First(p => p.Number == anchor.Page);
                    var next = i + 1 < blocks.Count && blocks[i + 1].Anchor is { } n && n.Page == anchor.Page ? n.Polygon : null;
                    // Words inside a table CU did return (assigned to another block) are not re-read as lines (brief 08: a rotated
                    // multi-page scan read 65 rows for 47 claims, duplicate claim numbers).
                    var tableBoxes = graph.Tables.Where(t => t.Page == anchor.Page && t.Polygon is { Count: >= 8 }).Select(t => Geometry.Box(t.Polygon!)).ToList();
                    var free = (_wordsByPage.GetValueOrDefault(anchor.Page) ?? []).Where(w => w.Polygon is not { Count: >= 8 } || !tableBoxes.Any(b =>
                    {
                        var box = Geometry.Box(w.Polygon);
                        var (cx, cy) = ((box.MinX + box.MaxX) / 2, (box.MinY + box.MaxY) / 2);
                        return cx >= b.MinX && cx <= b.MaxX && cy >= b.MinY && cy <= b.MaxY;
                    })).ToList();
                    var rows = anchor.Polygon is null ? [] : LineFallbackReader.Read(page, free, anchor.Polygon, next,
                        spec.ColumnMap.Select(c => c.Source).Distinct(StringComparer.Ordinal).ToList(), fallback);
                    if (rows.Count > 0)
                    {
                        _fallbackBlocks++;
                        _fallbackRows += rows.Count;
                        block.Rows.AddRange(rows);
                    }
                }
            }

            return blocks;
        }

        private List<Block> SpreadsheetBlocks()
        {
            var tables = graph.Tables.Where(t => (spec.TableLocator.Sheet is null || SameSheet(t.SheetName, spec.TableLocator.Sheet)) && Qualifies(t)).ToList();
            _tablesUsed = tables.Count;
            CollectUnmapped(tables);
            var block = new Block();
            foreach (var table in tables)
            {
                block.Rows.AddRange(TableRows(table));
            }

            return [block];
        }

        private bool Qualifies(GraphTable table)
        {
            var headers = ColumnKeys(table).OfType<string>().Concat(_splits.GetValueOrDefault(table.Id)?.Values.SelectMany(k => k) ?? []).ToHashSet(StringComparer.Ordinal);
            return _sources.Count > 0 && (double)_sources.Count(headers.Contains) / _sources.Count >= spec.TableLocator.HeaderMatchMin;
        }

        /// <summary>Per column: the source (or ignored header) it is read as; exact, or an OCR near-miss on scans (<see cref="OcrHeaders"/>).</summary>
        private string?[] ColumnKeys(GraphTable table)
        {
            if (!_columnKeys.TryGetValue(table.Id, out var keys))
            {
                var printed = table.HeaderTexts.Select(Key).ToList();
                keys = _columnKeys[table.Id] = OcrHeaders.Resolve(printed, _sources.Concat(_ignored).ToHashSet(StringComparer.Ordinal), _scanned);
                for (var i = 0; i < keys.Length; i++)
                {
                    if (keys[i] is { } key && key != printed[i])
                    {
                        OcrHeaderMatches.TryAdd(table.HeaderTexts[i], spec.ColumnMap.FirstOrDefault(c => Key(c.Source) == key)?.Source ?? key);
                    }
                }

                if (spec.TableLocator.Records is not null && HeaderRows(table) is { Length: > 1 } rows)
                {
                    // A record layout prints one header row per record line ("Loss State:" over "Loss Paid:"): the composed
                    // header matches no source, the key line's header does (ADR 0018).
                    var known = _sources.Concat(_ignored).ToHashSet(StringComparer.Ordinal);
                    for (var i = 0; i < keys.Length; i++)
                    {
                        if (keys[i] is null && i < rows[0].Length && Key(rows[0][i]) is { Length: > 0 } first && known.Contains(first))
                        {
                            keys[i] = first;
                        }
                    }
                }

                SplitMergedHeaders(table, keys);
                InferBlankHeaders(table, keys);
            }

            return keys;
        }

        /// <summary>
        /// A cell's printed lines, top first: its OCR words clustered by height (CU's line breaks inside a cell do not follow
        /// the printed lines: "Claimant: … Claim Type: …\nSPC-25-00018"); its line breaks when it has no word geometry.
        /// </summary>
        private string[] PrintedLines(SourceCell cell) => [.. PrintedLineParts(cell).Select(l => l.Text)];

        /// <summary>
        /// <see cref="PrintedLines"/> with each line's parts: split at gaps wider than two word heights (brief 20: a claim number and a
        /// file number printed apart on one line, inside a table CU drew over the loose lines).
        /// </summary>
        private List<(string Text, List<string> Parts)> PrintedLineParts(SourceCell cell)
        {
            List<(string, List<string>)> Breaks() => [.. (cell.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } b ? b : [cell.Text.Trim()])
                .Select(t => (t, new List<string> { t }))];
            if (cell.Polygon is not { Count: >= 8 } || !_wordsByPage.TryGetValue(cell.Page, out var pageWords))
            {
                return Breaks();
            }

            var box = Geometry.Box(cell.Polygon);
            var words = pageWords.Where(w => w.Polygon is { Count: >= 8 }).Select(w => (Word: w, Box: Geometry.Box(w.Polygon!)))
                .Where(w => (w.Box.MinX + w.Box.MaxX) / 2 >= box.MinX && (w.Box.MinX + w.Box.MaxX) / 2 <= box.MaxX
                    && (w.Box.MinY + w.Box.MaxY) / 2 >= box.MinY && (w.Box.MinY + w.Box.MaxY) / 2 <= box.MaxY)
                .OrderBy(w => w.Box.MinY).ToList();
            if (words.Count == 0)
            {
                return Breaks();
            }

            var tolerance = words.Select(w => w.Box.MaxY - w.Box.MinY).Order().ElementAt(words.Count / 2) * 0.5;
            var lines = new List<List<(GraphWord Word, (double MinX, double MinY, double MaxX, double MaxY) Box)>>();
            foreach (var word in words)
            {
                if (lines.Count == 0 || Math.Abs(word.Box.MinY - lines[^1][0].Box.MinY) > tolerance)
                {
                    lines.Add([]);
                }

                lines[^1].Add(word);
            }

            var height = tolerance * 2;
            var result = new List<(string Text, List<string> Parts)>();
            foreach (var line in lines)
            {
                var byX = line.OrderBy(w => w.Box.MinX).ToList();
                var parts = new List<List<GraphWord>> { new() };
                for (var i = 0; i < byX.Count; i++)
                {
                    if (i > 0 && byX[i].Box.MinX - byX[i - 1].Box.MaxX > height * 2)
                    {
                        parts.Add([]);
                    }

                    parts[^1].Add(byX[i].Word);
                }

                result.Add((PrintedText.JoinWords([.. byX.Select(w => w.Word)]), [.. parts.Select(p => PrintedText.JoinWords(p))]));
            }

            return result;
        }

        /// <summary>The table's header text per header row and column (a merged cell repeated over its span), top row first.</summary>
        private string[][] HeaderRows(GraphTable table)
        {
            if (!_headerRows.TryGetValue(table.Id, out var rows))
            {
                var cells = graph.Cells.Where(c => c.TableId == table.Id && (table.SheetName is null || c.Sheet == table.SheetName)).ToList();
                var top = table.SheetName is null || cells.Count == 0 ? 0 : cells.Min(c => c.Row);
                var left = table.SheetName is null || cells.Count == 0 ? 0 : cells.Min(c => c.Column);
                rows = _headerRows[table.Id] = [.. table.HeaderRows.Order().Select(r =>
                {
                    var texts = new string[table.HeaderTexts.Count];
                    Array.Fill(texts, "");
                    foreach (var cell in cells.Where(c => c.Row == r + top))
                    {
                        for (var k = 0; k < Math.Max(1, cell.ColumnSpan); k++)
                        {
                            var i = cell.Column - left + k;
                            if (i >= 0 && i < texts.Length && texts[i].Length == 0)
                            {
                                texts[i] = cell.RawValue.Trim();
                            }
                        }
                    }

                    return texts;
                })];
            }

            return rows;
        }

        /// <summary>
        /// The printed cell a column-map entry reads (ADR 0018): line 0 by its source; record line n under its line-0
        /// header (<c>under</c>) or under its own header on header row n; a stacked cell's part.
        /// </summary>
        /// <param name="totalsRow">A total row prints a record's line-n amounts on one line, in the columns of their line-0 headers.</param>
        private bool TryCell(SourceRow row, ColumnMapping column, out SourceCell cell, bool totalsRow = false)
        {
            var key = column.RowMeasure is { } measure ? $"M:{measure}|{Key(column.Source)}"
                : column.Line > 0 ? $"L{column.Line}|{Key(column.Under ?? column.Source)}" : Key(column.Source);
            if (!row.Cells.TryGetValue(key, out var found) && !(totalsRow && column.Line > 0 && row.Cells.TryGetValue(Key(column.Under ?? column.Source), out found)))
            {
                cell = null!;
                return false;
            }

            if (column.Part is not { } part)
            {
                cell = found;
                return true;
            }

            string? text;
            if (column.PartPattern is { } pattern)
            {
                var m = SpecRegex.Match(found.Text, pattern);
                text = m.Success && m.Groups[$"p{part}"].Success ? m.Groups[$"p{part}"].Value : null;
            }
            else
            {
                // The cell's printed lines: its line breaks, else its OCR words clustered by height (CU joins a stacked
                // cell's lines with a space: "IC2100001 Medical Only").
                var parts = PrintedLines(found);
                text = part < parts.Length ? parts[part] : null;
            }

            cell = found with { Text = text ?? "", Part = part };
            return text is not null;
        }

        /// <summary>
        /// Two printed headers CU returned as one cell, one per line (08c corpus v2: "Reported\nStatus" on a 300 dpi c1 scan,
        /// with every body cell "10/01/2022\nClosed"; the column went unmapped, a delta and a 5-iteration learn followed).
        /// When each line of an unresolved header is a spec source the table does not otherwise read, the column is read as
        /// those sources in order, each body cell split the same way (<see cref="TableRows"/>). No model call.
        /// </summary>
        private void SplitMergedHeaders(GraphTable table, string?[] keys)
        {
            var sources = _sources.Concat(_ignored).ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < keys.Length; i++)
            {
                var lines = table.HeaderTexts[i].Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (keys[i] is not null || lines.Length < 2)
                {
                    continue;
                }

                var parts = OcrHeaders.Resolve([.. lines.Select(Key)], sources, _scanned);
                if (parts.Any(p => p is null) || parts.Distinct(StringComparer.Ordinal).Count() != parts.Length || parts.Any(p => keys.Contains(p)))
                {
                    continue;
                }

                if (!_splits.TryGetValue(table.Id, out var split))
                {
                    _splits[table.Id] = split = [];
                }

                split[i] = parts!;
                keys[i] = parts[0];
                HeadersRepaired = true;
                StructuralRepairs.Add($"merged header in {table.Id} column {i + 1} split into {string.Join(" | ", parts.Select(p => $"\"{SourceName(p!)}\""))}");
            }
        }

        /// <summary>
        /// A header cell CU returned blank (08b corpus v2: "Med. Pd" missing from the first table of c2-003 and c2-011
        /// natives — the column was dropped, its period's subtotal could not tie, and the document went to Review). The
        /// blank column is read as the source a sibling table of the same document prints at that position, when the
        /// sibling's other headers agree; failing that, as the one source the table lacks when exactly one column is
        /// blank and the source sits there in the spec's column order. Never a source the table already reads.
        /// </summary>
        private void InferBlankHeaders(GraphTable table, string?[] keys)
        {
            var blanks = Enumerable.Range(0, keys.Length).Where(i => keys[i] is null && table.HeaderTexts[i].Trim().Length == 0).ToList();
            foreach (var column in blanks)
            {
                var sibling = SiblingHeader(table, keys, column);
                var inferred = sibling ?? (blanks.Count == 1 ? GapHeader(keys, column) : null);
                if (inferred is not null && !keys.Contains(inferred))
                {
                    keys[column] = inferred;
                    HeadersRepaired = true;
                    StructuralRepairs.Add($"blank header in {table.Id} column {column + 1} read as \"{SourceName(inferred)}\" ({(sibling is null ? "the one source the table lacks" : "as a sibling table prints it")})");
                }
            }
        }

        private string? SiblingHeader(GraphTable table, string?[] keys, int column)
        {
            var sources = _sources.Concat(_ignored).ToHashSet(StringComparer.Ordinal);
            var found = graph.Tables
                .Where(t => t.Id != table.Id && t.SheetName == table.SheetName && t.HeaderTexts.Count == keys.Length)
                .Select(t => OcrHeaders.Resolve(t.HeaderTexts.Select(Key).ToList(), sources, _scanned))
                .Where(s => s[column] is { } k && _sources.Contains(k)
                    && Enumerable.Range(0, keys.Length).All(j => j == column || keys[j] is null || s[j] is null || keys[j] == s[j])
                    && Enumerable.Range(0, keys.Length).Count(j => j != column && keys[j] is not null && keys[j] == s[j]) >= Math.Max(2, keys.Count(k => k is not null) - 1))
                .Select(s => s[column]!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return found.Count == 1 ? found[0] : null;
        }

        private string? GapHeader(string?[] keys, int column)
        {
            var order = spec.ColumnMap.Select(c => Key(c.Source)).Distinct(StringComparer.Ordinal).ToList();
            var missing = order.Where(s => !keys.Contains(s)).ToList();
            if (missing.Count != 1)
            {
                return null;
            }

            var position = order.IndexOf(missing[0]);
            var left = keys.Take(column).LastOrDefault(k => k is not null && order.Contains(k));
            var right = keys.Skip(column + 1).FirstOrDefault(k => k is not null && order.Contains(k));
            return (left is null || order.IndexOf(left) < position) && (right is null || order.IndexOf(right) > position) ? missing[0] : null;
        }

        private string SourceName(string key) => spec.ColumnMap.FirstOrDefault(c => Key(c.Source) == key)?.Source ?? key;

        private void CollectUnmapped(IEnumerable<GraphTable> tables)
        {
            foreach (var table in tables)
            {
                var keys = ColumnKeys(table);
                for (var i = 0; i < table.HeaderTexts.Count; i++)
                {
                    var header = table.HeaderTexts[i];
                    if (header.Trim().Length > 0 && keys[i] is null && !Unmapped.Contains(header))
                    {
                        Unmapped.Add(header);
                    }
                }
            }
        }

        private IEnumerable<SourceRow> TableRows(GraphTable table)
        {
            var cells = graph.Cells.Where(c => c.TableId == table.Id && (table.SheetName is null || c.Sheet == table.SheetName)).ToList();
            if (cells.Count == 0)
            {
                yield break;
            }

            var top = cells.Min(c => c.Row);
            var left = table.SheetName is null ? 0 : cells.Min(c => c.Column);
            var headerRows = table.HeaderRows.Select(r => r + (table.SheetName is null ? 0 : top)).ToHashSet();
            foreach (var group in cells.Where(c => !headerRows.Contains(c.Row)).GroupBy(c => c.Row).OrderBy(g => g.Key))
            {
                var ordered = group.OrderBy(c => c.Column).Where(c => c.RawValue.Trim().Length > 0).ToList();
                if (ordered.Count == 0)
                {
                    continue;
                }

                var sourceCells = ordered.Select(c => (Cell: c, Source: FromCell(c))).ToList();
                var keys = ColumnKeys(table);
                var row = new SourceRow
                {
                    Text = string.Join(" ", ordered.Select(c => c.RawValue.Trim())), Label = sourceCells[0].Source, Origin = table.Id, Keys = keys,
                    Page = ordered[0].Page, Top = ordered.Where(c => c.Polygon is { Count: >= 8 }).Select(c => Top(c.Polygon)).DefaultIfEmpty(Top(table.Polygon)).Min(),
                };
                var split = _splits.GetValueOrDefault(table.Id);
                foreach (var (cell, source) in sourceCells)
                {
                    var index = cell.Column - left;
                    if (split?.GetValueOrDefault(index) is { } parts)
                    {
                        var values = source.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                        if (values.Length == parts.Length)
                        {
                            for (var k = 0; k < parts.Length; k++)
                            {
                                row.Cells.TryAdd(parts[k], source with { Text = values[k] });
                            }
                        }
                        else
                        {
                            // Brief 32 item 2: the merged headers' values on this row are not read; the reviewer gets the cell.
                            Unnormalizable($"{table.Id} row {cell.Row}: {string.Join(" + ", parts)}", source.Text.Replace('\n', ' '), "merged-split", source,
                                $"{table.Id} row {cell.Row}: merged cell '{source.Text.Replace('\n', ' ')}' has {values.Length} line(s) for {parts.Length} merged header(s); not split");
                        }

                        row.Columns.Add((index, source));
                        continue;
                    }

                    row.Columns.Add((index, source));
                    if (index >= 0 && index < table.HeaderTexts.Count && (keys[index] is not null || table.HeaderTexts[index].Trim().Length > 0))
                    {
                        row.Cells.TryAdd(keys[index] ?? Key(table.HeaderTexts[index]), source);
                    }
                }

                yield return row;
            }
        }

        // ------------------------------------------------------------------ periods

        private PeriodBuild AnchorPeriod(Block block)
        {
            var m = block.AnchorMatch!;
            if (_periods.Count > 0 && _periods[^1].Key == m.Groups["policyNumber"].Value && m.Groups["policyNumber"].Value.Length > 0
                && _periods[^1].Node["effectiveDate"]?.GetValue<string>() is { } effective
                && CanonicalComparer.NormalizeDate(m.Groups["effectiveDate"].Value)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == effective)
            {
                // A continuation heading ("Policy X … (continued)" on the next page) that the anchor also matches: the same
                // period goes on (brief 19 constructs run: the learned c6 anchor split every period at its page break).
                return _periods[^1];
            }

            var period = NewPeriod(m.Groups["policyNumber"].Value);
            var source = FromParagraph(block.Anchor!, block.Anchor!.Text);
            SetPeriodField(period, "policyNumber", JsonValue.Create(m.Groups["policyNumber"].Value), source with { Text = m.Groups["policyNumber"].Value });
            SetPeriodDate(period, "effectiveDate", m.Groups["effectiveDate"].Value, source, inclusiveEnd: false);
            SetPeriodDate(period, "expirationDate", m.Groups["expirationDate"].Value, source, inclusiveEnd: spec.PeriodBoundaries.ExpirationInclusive);
            return period;
        }

        /// <summary>Brief 30 item 20: the row just placed in the period above because its policy cell was blank (read by <see cref="ClaimRow"/>).</summary>
        private SourceCell? _filledDown;
        private bool _filledDownRow;

        private PeriodBuild? ColumnPeriod(SourceRow row)
        {
            _filledDownRow = false;
            if (spec.Periods.Source != "column" || spec.Periods.Column is null)
            {
                return _periods.Count > 0 ? _periods[^1] : NewPeriod(null);
            }

            if (!row.Cells.TryGetValue(Key(spec.Periods.Column), out var cell) || cell.Text.Trim().Length == 0)
            {
                // A blank policy cell: the period above, as before, but said (a fill-down is a guess unless the spec declares the layout).
                (_filledDownRow, _filledDown) = (_periods.Count > 0, row.Cells.Values.FirstOrDefault());
                return _periods.Count > 0 ? _periods[^1] : NewPeriod(null);
            }

            var policy = cell.Text.Trim();
            var existing = _periods.FirstOrDefault(p => p.Key == policy);
            if (existing is not null)
            {
                return existing;
            }

            var period = NewPeriod(policy);
            SetPeriodField(period, "policyNumber", JsonValue.Create(policy), cell);
            return period;
        }

        /// <summary>
        /// Brief 23 §1: <c>period.*</c> document fields (locator <c>headerLabel</c>) fill a single-period run's policy number and
        /// dates from its header block, where the period has none of its own. Several periods read: an issue, nothing applied.
        /// </summary>
        private void HeaderPeriodFields()
        {
            foreach (var field in spec.DocumentFields.Where(f => f.Target.StartsWith("period.", StringComparison.Ordinal)))
            {
                var path = Strip(field.Target, "period.");
                if (_periods.Count != 1)
                {
                    if (_periods.Count > 1)
                    {
                        Issues.Add($"document field {field.Target}: a header period field applies to a single-period run ({_periods.Count} periods read)");
                    }

                    continue;
                }

                var period = _periods[0];
                if (period.Node[path] is not null)
                {
                    continue;
                }

                var (value, source, _) = Locate(field);
                if (value is null || source is null)
                {
                    Issues.Add($"document field {field.Target}: not found ({field.Locator.Type})");
                    continue;
                }

                if (path == "policyNumber")
                {
                    SetPeriodField(period, path, JsonValue.Create(source.Text.Trim()), source);
                }
                else
                {
                    SetPeriodDate(period, path, source.Text, source, inclusiveEnd: path == "expirationDate" && spec.PeriodBoundaries.ExpirationInclusive);
                }
            }
        }

        private PeriodBuild NewPeriod(string? key)
        {
            var period = new PeriodBuild { Index = _periods.Count, Key = key };
            _periods.Add(period);
            return period;
        }

        private void SetPeriodField(PeriodBuild period, string field, JsonNode value, SourceCell source)
        {
            period.Node[field] = value;
            SetLineage($"policyPeriods[{period.Index}].{field}", source, "replay", source.Text, source.Confidence);
        }

        private void SetPeriodDate(PeriodBuild period, string field, string raw, SourceCell source, bool inclusiveEnd)
        {
            if (CanonicalComparer.NormalizeDate(raw) is not { } date)
            {
                // Brief 32 item 2: a printed period date that does not parse leaves the period without it; routed, with the cell.
                Unnormalizable($"policyPeriods[{period.Index}].{field}", raw, "date", source, $"policyPeriods[{period.Index}].{field}: could not parse '{raw}'");
                return;
            }

            var value = inclusiveEnd ? date.AddDays(1) : date;
            SetPeriodField(period, field, JsonValue.Create(value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), source with { Text = raw });
        }

        /// <summary>
        /// A CSV has one "sheet" named after its file, so a spec learned from one export must not pin that name (eval run:
        /// every later CSV of the carrier read 0 rows). For CSV any sheet reference means the only sheet.
        /// </summary>
        private bool SameSheet(string? actual, string? wanted) => graph.SourceFile.Kind == FileKind.Csv || actual == wanted;

        private void ApplyLookup(PeriodLookup lookup)
        {
            var table = graph.Tables.FirstOrDefault(t => SameSheet(t.SheetName, lookup.Sheet) && t.HeaderTexts.Any(h => Key(h) == Key(lookup.KeyHeader)));
            if (table is null)
            {
                // Brief 32 item 2: the format reads its period dates and subtotals from this table; without it they are all absent.
                Unnormalizable("period lookup", lookup.KeyHeader, "lookup", null, $"period lookup: no table on sheet '{lookup.Sheet}' with header '{lookup.KeyHeader}'");
                return;
            }

            foreach (var row in TableRows(table))
            {
                if (!row.Cells.TryGetValue(Key(lookup.KeyHeader), out var keyCell))
                {
                    continue;
                }

                var period = _periods.FirstOrDefault(p => p.Key == keyCell.Text.Trim());
                if (period is null)
                {
                    period = NewPeriod(keyCell.Text.Trim());
                    SetPeriodField(period, "policyNumber", JsonValue.Create(keyCell.Text.Trim()), keyCell);
                }

                if (lookup.RangeHeader is not null && lookup.RangeRegex is not null && row.Cells.TryGetValue(Key(lookup.RangeHeader), out var range)
                    && SpecRegex.Match(range.Text, lookup.RangeRegex) is { Success: true } m)
                {
                    SetPeriodDate(period, "effectiveDate", m.Groups["effectiveDate"].Value, range, inclusiveEnd: false);
                    SetPeriodDate(period, "expirationDate", m.Groups["expirationDate"].Value, range, inclusiveEnd: spec.PeriodBoundaries.ExpirationInclusive);
                }

                if (lookup.EffectiveHeader is not null && row.Cells.TryGetValue(Key(lookup.EffectiveHeader), out var effective))
                {
                    SetPeriodDate(period, "effectiveDate", effective.Text, effective, inclusiveEnd: false);
                }

                if (lookup.ExpirationHeader is not null && row.Cells.TryGetValue(Key(lookup.ExpirationHeader), out var expiration))
                {
                    SetPeriodDate(period, "expirationDate", expiration.Text, expiration, inclusiveEnd: spec.PeriodBoundaries.ExpirationInclusive);
                }

                foreach (var (header, target) in lookup.Subtotals.OrderBy(s => s.Key, StringComparer.Ordinal))
                {
                    if (!row.Cells.TryGetValue(Key(header), out var cell))
                    {
                        continue;
                    }

                    var normalizer = new NormalizerSpec { Type = target.EndsWith("Count", StringComparison.Ordinal) ? "integer" : "currency" };
                    if (ValueNormalizer.Normalize(normalizer, cell.Text, glossary) is { } value)
                    {
                        period.Subtotals ??= [];
                        Set(period.Subtotals, target, value);
                        SetLineage($"policyPeriods[{period.Index}].subtotals.{target}", cell, "replay", cell.Text, cell.Confidence);
                    }
                }
            }
        }

        /// <summary>
        /// Values the document does not print but the submission states (06b Answers 1): valuation date and policy dates.
        /// Never overrides a printed value; lineage extractor <c>context</c>.
        /// </summary>
        private void ApplySubmissionContext()
        {
            if (submission is null)
            {
                return;
            }

            if (Document["valuationDate"] is null && submission.ValuationDate is { } valuation)
            {
                Document["valuationDate"] = valuation.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                SetLineage("valuationDate", null, "context", "submission context valuationDate", 1.0);
                // Brief 23 Answers 2: never silently; R-32 flags it, the quote payload and the memo state it.
                Document["valuationDateSource"] = "submission";
                SetLineage("valuationDateSource", null, "context", "valuation date as stated by the submitter (the run prints none)", 1.0);
            }

            foreach (var period in _periods)
            {
                var number = period.Node["policyNumber"]?.GetValue<string>();
                var stated = number is null ? null : submission.PolicyPeriods?.FirstOrDefault(p => Key(p.PolicyNumber) == Key(number));
                if (stated is null)
                {
                    continue;
                }

                foreach (var (field, value) in new[] { ("effectiveDate", stated.EffectiveDate), ("expirationDate", stated.ExpirationDate) })
                {
                    if (period.Node[field] is null && value is { } date)
                    {
                        period.Node[field] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        SetLineage($"policyPeriods[{period.Index}].{field}", null, "context", $"submission context policy {stated.PolicyNumber}", 1.0);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ claim and totals rows

        // ------------------------------------------------------------------ multi-line claim records (ADR 0018)

        /// <summary>The block's rows with multi-line claim records assembled; unchanged without <c>tableLocator.records</c>.</summary>
        private IEnumerable<SourceRow> Records(List<Block> blocks, int index) => spec.TableLocator.Records switch
        {
            { Mode: "rows" } r => RecordRows(blocks[index].Rows, r),
            { Mode: "lines" } r => RecordLines(blocks, index, r),
            { Mode: "measures" } r => RecordMeasures(blocks[index].Rows, r),
            _ => blocks[index].Rows,
        };

        /// <summary>
        /// <c>records.mode: measures</c> (brief 22 Answers 1): a key row (a value under the key source) or a total row opens a
        /// record; each row carries a measure label ("Inc:", "Pd:", "O/S:") in one of its cells, and its cells are filed on the
        /// open record under <c>M:{measure}|{header}</c>, the opening row's own included. A row with neither a key nor a label
        /// (an accident-description line) is skipped while a record is open.
        /// </summary>
        private List<SourceRow> RecordMeasures(List<SourceRow> rows, RecordSpec records)
        {
            var keySource = Key(records.KeySource ?? spec.ColumnMap.FirstOrDefault(c => c.Target == "claim.claimNumber")?.Source ?? "");
            var labels = (records.MeasureLabels ?? new MeasureLabels()).Patterns().ToList();
            string? MeasureOf(SourceRow row) => row.Columns
                .Select(c => c.Cell.Text.Trim())
                .Select(t => labels.FirstOrDefault(l => SpecRegex.IsMatch(t, l.Pattern)).Measure)
                .FirstOrDefault(m => m is not null);

            void FileMeasure(SourceRow record, SourceRow row, string measure)
            {
                foreach (var (key, cell) in row.Cells.Where(c => !c.Key.StartsWith("M:", StringComparison.Ordinal)).ToList())
                {
                    record.Cells.TryAdd($"M:{measure}|{key}", cell);
                }
            }

            var result = new List<SourceRow>();
            SourceRow? open = null;
            foreach (var row in rows)
            {
                if (CaptionsOnly(row))
                {
                    _skipped++;
                    continue;
                }

                var measure = MeasureOf(row);
                // Only a total row or a key row opens a record. A labelled row is a measure row even when a skip row kind
                // matches it (brief 23: a learn added skip kinds for "Inc:", "Pd:", "O/S:", and every measure row was dropped).
                var opens = TotalsKind(row.Text) || row.Cells.TryGetValue(keySource, out var key) && key.Text.Trim().Length > 0;
                if (opens)
                {
                    open = row;
                    result.Add(row);
                    if (measure is not null)
                    {
                        FileMeasure(row, row, measure);
                    }

                    continue;
                }

                if (open is not null)
                {
                    if (measure is not null)
                    {
                        FileMeasure(open, row, measure);
                    }
                    else
                    {
                        _skipped++;
                    }

                    continue;
                }

                result.Add(row);
            }

            return result;
        }

        /// <summary>Brief 30 item 41: a total row ties when R-10 would pass it (the catalog's tolerance, ±$1), not at a stricter cent.</summary>
        private static readonly decimal TotalsTolerance = LossRun.Rules.RuleCatalog.Embedded.ToleranceOf("R-10");

        private bool TotalsKind(string text) => spec.TableLocator.RowKinds.Any(k => k.Kind is "subtotal" or "grandTotal" && SpecRegex.IsMatch(text, k.Regex));

        /// <summary>A row of nothing but captions ("Loss Paid: / Expenses Paid:"): the record lines' labels, not a record line.</summary>
        private static bool CaptionsOnly(SourceRow row) => row.Columns.Count > 0
            && row.Columns.All(c => c.Cell.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).All(l => l.EndsWith(':')));

        /// <summary>
        /// <c>records.mode: rows</c>: a row with a value under the key source opens a record; following rows without one are its
        /// lines 1, 2, … until a row-kind row, the next key row or <c>maxLines</c>. A line's cells are filed under
        /// <c>L{n}|{line-0 header}</c> and <c>L{n}|{its own header row n}</c>, for column-map entries with <c>line</c>.
        /// </summary>
        private List<SourceRow> RecordRows(List<SourceRow> rows, RecordSpec records)
        {
            var keySource = Key(records.KeySource ?? spec.ColumnMap.FirstOrDefault(c => c.Target == "claim.claimNumber")?.Source ?? "");
            var result = new List<SourceRow>();
            SourceRow? open = null;
            var line = 0;
            foreach (var row in rows)
            {
                if (CaptionsOnly(row))
                {
                    _skipped++;
                    continue;
                }

                if (spec.TableLocator.RowKinds.Any(k => SpecRegex.IsMatch(row.Text, k.Regex)))
                {
                    open = null;
                    result.Add(row);
                    continue;
                }

                if (row.Cells.TryGetValue(keySource, out var key) && key.Text.Trim().Length > 0)
                {
                    (open, line) = (row, 0);
                    result.Add(row);
                    continue;
                }

                if (open is null || line + 1 >= records.MaxLines)
                {
                    open = null;
                    result.Add(row);
                    continue;
                }

                line++;
                var headers = row.Origin is { } id && graph.Tables.FirstOrDefault(t => t.Id == id) is { } table ? HeaderRows(table) : [];
                foreach (var (i, cell) in row.Columns)
                {
                    var under = open.Keys is { } keys && i >= 0 && i < keys.Length ? keys[i] : null;
                    under ??= headers.Length > 0 && i >= 0 && i < headers[0].Length ? Key(headers[0][i]) : null;
                    if (under is { Length: > 0 })
                    {
                        open.Cells.TryAdd($"L{line}|{under}", cell);
                    }

                    if (headers.Length > line && i >= 0 && i < headers[line].Length && Key(headers[line][i]) is { Length: > 0 } own)
                    {
                        open.Cells.TryAdd($"L{line}|{own}", cell);
                    }
                }
            }

            return result;
        }

        private sealed record LooseLine(string Text, SourceCell Source, int Page, double Top, List<(double X, SourceCell Cell)> Parts);

        /// <summary>
        /// <c>records.mode: lines</c>: each claim row owns the loose lines printed after it (paragraphs outside the used tables,
        /// clustered into printed lines) until the next claim row, a total line or <c>maxLines</c>. A loose line a total row
        /// kind matches becomes a total row whose amounts are assigned to the preceding table's columns by position.
        /// </summary>
        private List<SourceRow> RecordLines(List<Block> blocks, int index, RecordSpec records)
        {
            var lines = LooseLines(blocks[index].Anchor, blocks.Skip(index + 1).FirstOrDefault(b => b.Anchor is not null)?.Anchor);
            var result = new List<SourceRow>();
            SourceRow? open = null, lastTableRow = null;
            var next = 0;
            void Attach(LooseLine line)
            {
                if (TotalsKind(line.Text))
                {
                    if (lastTableRow is not null)
                    {
                        result.Add(LineRow(line, lastTableRow));
                    }

                    open = null;
                    return;
                }

                if (open is not null && open.Lines.Count < records.MaxLines)
                {
                    open.Lines.Add((line.Text, line.Source));
                    open.LineParts.Add([.. line.Parts.Select(p => p.Cell)]);
                }
            }

            var mapped = spec.ColumnMap.Where(c => c.Target.StartsWith("claim.", StringComparison.Ordinal)).Select(c => Key(c.Source)).Distinct().Count();
            foreach (var row in blocks[index].Rows.OrderBy(r => r.Page).ThenBy(r => r.Top))
            {
                while (next < lines.Count && (lines[next].Page < row.Page || (lines[next].Page == row.Page && lines[next].Top < row.Top)))
                {
                    Attach(lines[next++]);
                }

                if (!TotalsKind(row.Text) && row.Columns.Count < Math.Max(2, mapped / 2))
                {
                    // CU drew the table over the loose lines below it (a row "Claimant: … Claim Type: … SPC-25-00018" in the
                    // first column): its printed lines are loose lines, not a claim row.
                    foreach (var (_, cell) in row.Columns)
                    {
                        foreach (var (text, parts) in PrintedLineParts(cell))
                        {
                            Attach(new LooseLine(text, cell with { Text = text }, row.Page, row.Top, [.. parts.Select(part => (0.0, cell with { Text = part }))]));
                        }
                    }

                    continue;
                }

                open = TotalsKind(row.Text) ? null : row;
                lastTableRow = row.Keys is null ? lastTableRow : row;
                result.Add(row);
            }

            while (next < lines.Count)
            {
                Attach(lines[next++]);
            }

            return result;
        }

        /// <summary>
        /// The printed lines of the block outside the used tables, rebuilt from the OCR words (CU merges consecutive printed
        /// lines into one paragraph: "Claimant: … Claim Type: … SPC-23-00004"). Words of page headers and footers are left out.
        /// A line splits into parts at gaps wider than two word heights, so a total line's amounts keep their positions.
        /// </summary>
        private List<LooseLine> LooseLines(GraphParagraph? anchor, GraphParagraph? nextAnchor)
        {
            static bool After((int Page, double Top) a, (int Page, double Top) b) => a.Page > b.Page || (a.Page == b.Page && a.Top > b.Top);
            static bool Inside((double MinX, double MinY, double MaxX, double MaxY) box, (double X, double Y) point) =>
                point.X >= box.MinX && point.X <= box.MaxX && point.Y >= box.MinY && point.Y <= box.MaxY;
            var excluded = graph.Tables.Where(t => t.Polygon is { Count: >= 8 } && Qualifies(t)).Select(t => (t.Page, Box: Geometry.Box(t.Polygon!)))
                .Concat(graph.Paragraphs.Where(p => p.Role is "pageHeader" or "pageFooter" or "pageNumber" && p.Polygon is { Count: >= 8 }).Select(p => (p.Page, Box: Geometry.Box(p.Polygon!))))
                .ToList();
            var words = graph.Words.Where(w => w.Polygon is { Count: >= 8 } && w.Text.Trim().Length > 0)
                .Select(w => (Word: w, Box: Geometry.Box(w.Polygon!)))
                .Where(w => (anchor is null || After((w.Word.Page, w.Box.MinY), (anchor.Page, Top(anchor.Polygon) + 0.01)))
                    && (nextAnchor is null || After((nextAnchor.Page, Top(nextAnchor.Polygon) - 0.01), (w.Word.Page, w.Box.MinY))))
                .Where(w =>
                {
                    var centre = ((w.Box.MinX + w.Box.MaxX) / 2, (w.Box.MinY + w.Box.MaxY) / 2);
                    return !excluded.Any(e => e.Page == w.Word.Page && Inside(e.Box, centre));
                })
                .ToList();
            var lines = new List<LooseLine>();
            foreach (var page in words.GroupBy(w => w.Word.Page).OrderBy(g => g.Key))
            {
                var ordered = page.OrderBy(w => w.Box.MinY).ToList();
                var height = ordered.Select(w => w.Box.MaxY - w.Box.MinY).Order().ElementAt(ordered.Count / 2);
                var clusters = new List<List<(GraphWord Word, (double MinX, double MinY, double MaxX, double MaxY) Box)>>();
                foreach (var word in ordered)
                {
                    if (clusters.Count == 0 || Math.Abs((word.Box.MinY + word.Box.MaxY) / 2 - (clusters[^1][0].Box.MinY + clusters[^1][0].Box.MaxY) / 2) > height * 0.5)
                    {
                        clusters.Add([]);
                    }

                    clusters[^1].Add(word);
                }

                foreach (var cluster in clusters)
                {
                    var byX = cluster.OrderBy(w => w.Box.MinX).ToList();
                    var parts = new List<(double X, SourceCell Cell)>();
                    var current = new List<(GraphWord Word, (double MinX, double MinY, double MaxX, double MaxY) Box)>();
                    void FlushPart()
                    {
                        if (current.Count == 0)
                        {
                            return;
                        }

                        var (minX, minY, maxX, maxY) = (current.Min(w => w.Box.MinX), current.Min(w => w.Box.MinY), current.Max(w => w.Box.MaxX), current.Max(w => w.Box.MaxY));
                        var text = PrintedText.JoinWords([.. current.Select(w => w.Word)]);
                        parts.Add(((minX + maxX) / 2, new SourceCell(text, page.Key, Geometry.Rectangle(minX, minY, maxX, maxY), null, null, WordConfidence.Min(current.Select(w => w.Word)))));
                        current.Clear();
                    }

                    foreach (var word in byX)
                    {
                        if (current.Count > 0 && word.Box.MinX - current[^1].Box.MaxX > height * 2)
                        {
                            FlushPart();
                        }

                        current.Add(word);
                    }

                    FlushPart();
                    var lineText = string.Join(" ", parts.Select(p => p.Cell.Text));
                    var (lx1, ly1, lx2, ly2) = (byX.Min(w => w.Box.MinX), byX.Min(w => w.Box.MinY), byX.Max(w => w.Box.MaxX), byX.Max(w => w.Box.MaxY));
                    lines.Add(new LooseLine(lineText, parts[0].Cell with { Text = lineText, Polygon = Geometry.Rectangle(lx1, ly1, lx2, ly2) }, page.Key, ly1, parts));
                }
            }

            return lines;
        }

        /// <summary>A loose total line ("Policy Total: $… $… $…") read as a row of the table above it, each amount in the column it sits under.</summary>
        private SourceRow LineRow(LooseLine line, SourceRow tableRow)
        {
            var keys = tableRow.Keys!;
            var columns = graph.Cells.Where(c => c.TableId == tableRow.Origin && c.Polygon is { Count: >= 8 }).GroupBy(c => c.Column)
                .Select(g => (Column: g.Key, MinX: g.Min(c => Geometry.Box(c.Polygon!).MinX), MaxX: g.Max(c => Geometry.Box(c.Polygon!).MaxX))).ToList();
            var row = new SourceRow { Text = line.Text, Label = line.Parts[0].Cell, Origin = tableRow.Origin, Keys = keys, Page = line.Page, Top = line.Top };
            foreach (var (x, cell) in line.Parts)
            {
                if (columns.Count == 0)
                {
                    break;
                }

                var column = columns.FirstOrDefault(c => x >= c.MinX && x <= c.MaxX);
                if (column == default)
                {
                    column = columns.MinBy(c => Math.Abs((c.MinX + c.MaxX) / 2 - x));
                }

                row.Columns.Add((column.Column, cell));
                if (column.Column >= 0 && column.Column < keys.Length && keys[column.Column] is { } key)
                {
                    row.Cells.TryAdd(key, cell);
                }
            }

            return row;
        }

        private void ClaimRow(SourceRow row, PeriodBuild period)
        {
            var claim = new JsonObject();
            // Brief 23 §2 (ADR 0020 amendment): a row of another line of business is kept, not a claim.
            var kept = RowLineOfBusiness(row);
            var claimIndex = kept is null ? period.Claims.Count : period.KeptRows.Count;
            var basePath = kept is null ? $"policyPeriods[{period.Index}].claims[{claimIndex}]" : $"policyPeriods[{period.Index}].keptRows[{claimIndex}]";
            if (_filledDownRow)
            {
                // Brief 30 item 20: the lineage says how the row got its period; without fillDown the reviewer decides.
                SetLineage($"{basePath}.periodAssignment", _filledDown, "fill-down", $"periodAssignment: filled-down from the row above (policy {period.Key})", 1.0);
                if (!spec.Periods.FillDown)
                {
                    Unnormalizable($"{basePath}.periodAssignment", "", UnnormalizedReasonText.FillDown, _filledDown,
                        $"{basePath}: blank policy cell; attached to the period above ({period.Key}) — the spec does not declare fillDown");
                }

                _filledDownRow = false;
            }
            var any = false;
            foreach (var column in spec.ColumnMap.Where(c => c.Target.StartsWith("claim.", StringComparison.Ordinal)))
            {
                if (!TryCell(row, column, out var cell) || column.NullWhen.Contains(cell.Text.Trim()))
                {
                    continue;
                }

                var path = Strip(column.Target, "claim.");
                var normalizer = Coded(column.Normalizer, column.Target);
                var value = ValueNormalizer.Normalize(normalizer, cell.Text, glossary);
                if (value is null)
                {
                    if (kept is null || column.Target is not ("claim.claimType" or "claim.lineOfBusiness"))
                    {
                        Unnormalizable($"{basePath}.{path}", cell.Text, column.Normalizer.Type, cell, $"{basePath}.{path}: could not normalize '{cell.Text}' ({column.Normalizer.Type})");
                    }

                    continue;
                }

                Set(claim, path, value);
                SetLineage($"{basePath}.{path}", cell, "replay", cell.Text, cell.Confidence);
                SetCodeSystem(claim, path, normalizer);
                any = true;
                if (column.Normalizer is { Type: "enum", GlossaryField: { } field })
                {
                    // Brief 22 Answers 4: a value that also states a flag (status "X", census only → reportOnly).
                    foreach (var (flag, set) in glossary.ValueSets(field, cell.Text).Where(f => f.Key.StartsWith("claim.", StringComparison.Ordinal)))
                    {
                        var flagPath = Strip(flag, "claim.");
                        if (Get(claim, flagPath) is null)
                        {
                            Set(claim, flagPath, JsonValue.Create(set));
                            SetLineage($"{basePath}.{flagPath}", cell, "replay", $"{cell.Text} ({field} value alias sets {flagPath})", cell.Confidence);
                        }
                    }
                }
            }

            if (!any)
            {
                _skipped++;
                return;
            }

            foreach (var field in spec.TableLocator.Records?.LineFields ?? [])
            {
                var path = Strip(field.Target, "claim.");
                if (path == "claimNumber" && Get(claim, path) is null && UnmatchedClaimNumberRecords.Count < 3 && !row.Lines.Any(l => SpecRegex.IsMatch(l.Text, field.Pattern))
                    && !row.LineParts.SelectMany(parts => parts).Any(part => SpecRegex.IsMatch(part.Text, field.Pattern)))
                {
                    UnmatchedClaimNumberRecords.Add(string.Join(" / ", row.Lines.Select((l, i) => row.LineParts.ElementAtOrDefault(i) is { Count: > 1 } parts
                        ? string.Join(" | ", parts.Select(part => part.Text)) : l.Text).Select(t => $"«{t}»")));
                }

                var indices = Enumerable.Range(0, row.Lines.Count).ToList();
                var chosen = field.Offset is not { } offset ? indices
                    : offset >= 0 ? indices.Skip(offset).Take(1).ToList()
                    : indices.Skip(Math.Max(0, row.Lines.Count + offset)).Take(1).ToList();
                var candidates = chosen.SelectMany(i => row.LineParts.ElementAtOrDefault(i) is { Count: > 1 } parts
                    ? [row.Lines[i], .. parts.Select(p => (p.Text, p))]
                    : new[] { row.Lines[i] });
                foreach (var (text, source) in candidates)
                {
                    var m = SpecRegex.Match(text, field.Pattern);
                    if (!m.Success)
                    {
                        continue;
                    }

                    var raw = m.Groups["value"].Success ? m.Groups["value"].Value.Trim() : m.Value.Trim();
                    if (ValueNormalizer.Normalize(field.Normalizer, raw, glossary) is not { } value)
                    {
                        Unnormalizable($"{basePath}.{path}", raw, field.Normalizer.Type, source, $"{basePath}.{path}: could not normalize '{raw}' ({field.Normalizer.Type}) from a record line");
                        break;
                    }

                    if (Get(claim, path) is null)
                    {
                        Set(claim, path, value);
                        SetLineage($"{basePath}.{path}", source with { Text = raw }, "replay", raw, source.Confidence);
                    }

                    break;
                }
            }

            if (spec.StatusFrom is { } statusFrom && claim["status"] is null)
            {
                // Brief 19 Answers 2: a per-format convention, verified by the learn loop (closeDate) or not assumed (unknown).
                var status = statusFrom == "closeDate" ? claim["dateClosed"] is not null ? "closed" : "open" : "unknown";
                claim["status"] = status;
                SetLineage($"{basePath}.status", null, "derived", statusFrom == "closeDate"
                    ? $"statusFrom closeDate: {(status == "closed" ? "close date printed" : "no close date printed")}"
                    : "statusFrom unknown: the format prints no status", statusFrom == "closeDate" ? 0.9 : 1.0);
            }

            RowLevelValues(row, period);

            Derive(claim, basePath, "claim");
            if (kept is { } other)
            {
                claim["keptReason"] = $"line of business: {LineWords(other.Line)} ({other.Evidence})";
                SetLineage($"{basePath}.keptReason", other.Cell, "replay", other.Cell.Text, other.Cell.Confidence);
                period.KeptRows.Add(claim);
                _keptRows++;
                return;
            }

            period.Claims.Add(claim);
            _claimRows++;
        }

        private int _keptRows;

        /// <summary>
        /// Brief 23 §2: the line of business a claim row prints in its line-of-business or claim-type column, when it is not the
        /// POC's scope (line-of-business.json: glossary value aliases, line terms, claim-type code patterns such as file
        /// prefixes); null for an in-scope or unlabelled row.
        /// </summary>
        private (string Line, string Evidence, SourceCell Cell)? RowLineOfBusiness(SourceRow row)
        {
            var vocabulary = CanonicalData.LineOfBusiness;
            foreach (var column in spec.ColumnMap.Where(c => c.Target is "claim.lineOfBusiness" or "claim.claimType"))
            {
                if (TryCell(row, column, out var cell) && vocabulary.RowLine(column.Target, cell.Text, glossary) is { } found && found.Line != vocabulary.InScope)
                {
                    return (found.Line, found.Evidence, cell);
                }
            }

            return null;
        }

        private static string LineWords(string line) => Regex.Replace(line, "(?<=[a-z])([A-Z])", " $1").ToLowerInvariant();

        /// <summary>
        /// Exports repeat period and document values on every row ("Pol Eff", "Valuation Date"). Column-map targets
        /// <c>period.effectiveDate|expirationDate</c> fill the row's period, and <c>document.*</c> fills the document,
        /// from the first row that has them (brief 08: the CSV carrier could not otherwise be learned).
        /// </summary>
        private void RowLevelValues(SourceRow row, PeriodBuild period)
        {
            foreach (var column in spec.ColumnMap.Where(c => (c.Target.StartsWith("period.", StringComparison.Ordinal) && !c.Target.StartsWith("period.subtotals.", StringComparison.Ordinal))
                || c.Target.StartsWith("document.", StringComparison.Ordinal)))
            {
                if (!TryCell(row, column, out var cell) || cell.Text.Trim().Length == 0 || column.NullWhen.Contains(cell.Text.Trim()))
                {
                    continue;
                }

                var isPeriod = column.Target.StartsWith("period.", StringComparison.Ordinal);
                var field = Strip(column.Target, isPeriod ? "period." : "document.");
                if ((field == "policyNumber" && !PeriodSummary) || (isPeriod ? period.Node[field] : Document[field]) is not null)
                {
                    continue;
                }

                var value = ValueNormalizer.Normalize(column.Normalizer, cell.Text, glossary);
                if (value is null)
                {
                    Unnormalizable(isPeriod ? $"policyPeriods[{period.Index}].{field}" : field, cell.Text, column.Normalizer.Type, cell,
                        $"{column.Target}: could not normalize '{cell.Text}' ({column.Normalizer.Type})");
                    continue;
                }

                if (isPeriod)
                {
                    var date = CanonicalComparer.NormalizeDate(cell.Text);
                    var adjusted = field == "expirationDate" && spec.PeriodBoundaries.ExpirationInclusive && date is { } d
                        ? JsonValue.Create(d.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) : value;
                    period.Node[field] = adjusted;
                    SetLineage($"policyPeriods[{period.Index}].{field}", cell, "replay", cell.Text, cell.Confidence);
                }
                else
                {
                    Document[field] = value;
                    SetLineage(field, cell, "replay", cell.Text, cell.Confidence);
                }
            }
        }

        /// <summary>
        /// One row of a summary-only table (ADR 0017, <c>tableLocator.rowShape: periodSummary</c>): the row is a period of its
        /// own (a fiscal, report or policy year), its money and count columns its printed subtotals, its <c>period.*</c>
        /// columns the period's label and dates. A row reading none of the spec's sources is skipped.
        /// </summary>
        private void SummaryRow(SourceRow row)
        {
            if (!spec.ColumnMap.Any(c => row.Cells.ContainsKey(Key(c.Source))))
            {
                _skipped++;
                return;
            }

            var period = NewPeriod(null);
            var basePath = $"policyPeriods[{period.Index}].subtotals";
            period.Subtotals = TotalsRow(row, new RowKindRule { Kind = "subtotal" }, basePath);
            RowLevelValues(row, period);
            _summaryRows++;
        }

        private JsonObject TotalsRow(SourceRow row, RowKindRule kind, string basePath)
        {
            var totals = new JsonObject();
            foreach (var column in spec.ColumnMap)
            {
                // claim.* money columns also fill total rows; period.subtotals.* (counts, recoveries by kind) only total rows.
                var subtotal = column.Target.StartsWith("period.subtotals.", StringComparison.Ordinal);
                var path = subtotal ? Strip(column.Target, "period.subtotals.") : Strip(column.Target, "claim.");
                if (!subtotal && !MoneyRoots.Any(r => path.StartsWith(r, StringComparison.Ordinal)) && path != "recoverySubrogation")
                {
                    continue;
                }

                if (!TryCell(row, column, out var cell, totalsRow: true) || cell.Text.Trim().Length == 0 || column.NullWhen.Contains(cell.Text.Trim()))
                {
                    continue; // brief 30 item 41: a total row honours nullWhen like a claim row
                }

                if (ValueNormalizer.Normalize(column.Normalizer, cell.Text, glossary) is { } value)
                {
                    Set(totals, path, value);
                    SetLineage($"{basePath}.{path}", cell, "replay", cell.Text, cell.Confidence);
                }
                else
                {
                    Unnormalizable($"{basePath}.{path}", cell.Text, column.Normalizer.Type, cell, $"{basePath}.{path}: could not normalize '{cell.Text}' ({column.Normalizer.Type})");
                }
            }

            if (kind.CountsRegex is not null && SpecRegex.Match(row.Text, kind.CountsRegex) is { Success: true } m)
            {
                foreach (var name in new[] { "claimCount", "openCount", "closedCount" })
                {
                    if (!m.Groups[name].Success)
                    {
                        continue;
                    }

                    // Brief 30 item 38: a learned counts pattern can capture text (an OCR'd "l2"); that is an issue, not a crash.
                    if (decimal.TryParse(m.Groups[name].Value.Replace(",", "", StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
                    {
                        totals[name] = ValueNormalizer.Number(count);
                        SetLineage($"{basePath}.{name}", row.Label, "replay", m.Value, row.Label?.Confidence ?? 1.0);
                    }
                    else
                    {
                        Unnormalizable($"{basePath}.{name}", m.Groups[name].Value, "integer", row.Label, $"{basePath}.{name}: could not normalize '{m.Groups[name].Value}' (integer) from the counts pattern");
                    }
                }
            }

            return totals;
        }

        /// <summary>
        /// A total row whose label CU split across cells can carry every money cell one column off (08b Answers 1). When
        /// the row as read does not reconcile with the claims it totals, but read one column to the left or right every
        /// money value it prints does, the shifted reading replaces it. Replay-only, no model call; a row that ties
        /// neither way is left as read for the rules to fail.
        /// </summary>
        private void RealignTotals(SourceRow row, PeriodBuild? period, string basePath, JsonObject totals)
        {
            if (row.Keys is not { } keys)
            {
                return;
            }

            var claims = period?.Claims ?? _periods.SelectMany(p => p.Claims).ToList();
            var money = spec.ColumnMap
                .Select(c => (Column: c, Path: Strip(c.Target, "claim."), Index: Array.IndexOf(keys, Key(c.Source))))
                .Where(m => m.Index >= 0 && MoneyRoots.Any(r => m.Path.StartsWith(r, StringComparison.Ordinal)))
                .Select(m => (m.Column, m.Path, m.Index, Expected: Sum(claims, m.Path)))
                .Where(m => m.Expected is not null)
                .ToList();
            if (claims.Count == 0 || money.Count < 2)
            {
                return;
            }

            (int Tied, int Read, List<(string Path, JsonNode Value, SourceCell Cell)> Values) Reading(int offset)
            {
                var values = new List<(string, JsonNode, SourceCell)>();
                var tied = 0;
                foreach (var m in money)
                {
                    var cell = row.Columns.FirstOrDefault(c => c.Index == m.Index + offset).Cell;
                    if (cell is null || ValueNormalizer.Normalize(m.Column.Normalizer, cell.Text, glossary) is not { } value)
                    {
                        continue;
                    }

                    values.Add((m.Path, value, cell));
                    tied += Math.Abs(value.GetValue<decimal>() - m.Expected!.Value) <= TotalsTolerance ? 1 : 0;
                }

                return (tied, values.Count, values);
            }

            var asRead = Reading(0);
            if (asRead.Tied == asRead.Read)
            {
                return;
            }

            foreach (var offset in new[] { -1, 1 })
            {
                var shifted = Reading(offset);
                if (shifted.Read >= 2 && shifted.Tied == shifted.Read && shifted.Read >= asRead.Read)
                {
                    foreach (var m in money)
                    {
                        RemovePath(totals, m.Path);
                        Lineage.Remove($"{basePath}.{m.Path}");
                    }

                    foreach (var (path, value, cell) in shifted.Values)
                    {
                        Set(totals, path, value);
                        SetLineage($"{basePath}.{path}", cell, "replay", cell.Text, cell.Confidence);
                    }

                    StructuralRepairs.Add($"{basePath}: money cells read one column {(offset < 0 ? "left" : "right")} (row label split across cells); {shifted.Tied} total(s) reconcile");
                    return;
                }
            }
        }

        private static decimal? Sum(IEnumerable<JsonObject> claims, string path)
        {
            var values = claims.Select(c => Get(c, path)).OfType<JsonNode>().Select(v => v.GetValue<decimal>()).ToList();
            return values.Count == 0 ? null : values.Sum();
        }

        private void Derive(JsonObject target, string basePath, string appliesTo)
        {
            foreach (var rule in spec.DerivedRules.Where(r => r.AppliesTo.Contains(appliesTo)))
            {
                if (rule.When == "absent" && Get(target, rule.Target) is not null)
                {
                    continue;
                }

                var operands = rule.Sum.Select(p => Get(target, p)).ToList();
                if (operands.Any(o => o is null))
                {
                    continue;
                }

                var sum = operands.Sum(o => o!.GetValue<decimal>());
                Set(target, rule.Target, ValueNormalizer.Number(sum));
                var confidence = rule.Sum.Select(p => Lineage.TryGetValue($"{basePath}.{p}", out var l) ? l.Confidence ?? 1.0 : 1.0).Min();
                SetLineage($"{basePath}.{rule.Target}", null, "derived", string.Join(" + ", rule.Sum.Select(p => $"{basePath}.{p}")), confidence);
            }
        }

        // ------------------------------------------------------------------ helpers

        private void SetLineage(string path, SourceCell? source, string extractor, string? sourceText, double confidence) =>
            Lineage[path] = new LineageRecord
            {
                DocumentId = graph.DocumentId,
                Page = source?.Sheet is null ? source?.Page : null,
                Polygon = source?.Polygon,
                Sheet = source?.Sheet,
                Cell = source?.A1,
                SourceText = sourceText,
                Extractor = extractor,
                FormatId = spec.FormatId,
                FormatVersion = spec.Version.ToString(CultureInfo.InvariantCulture),
                Confidence = Math.Round(confidence, 4),
                Part = source?.Part,
            };

        private SourceCell FromCell(GraphCell cell) =>
            new(cell.RawValue.Trim(), cell.Page, cell.Polygon, cell.Sheet, cell.A1,
                cell.Sheet is not null ? 1.0 : Geometry.MinWordConfidence(_wordsByPage.GetValueOrDefault(cell.Page) ?? [], cell.Polygon));

        private SourceCell FromParagraph(GraphParagraph paragraph, string text) =>
            new(text, paragraph.Page, paragraph.Polygon, null, null, Geometry.MinWordConfidence(_wordsByPage.GetValueOrDefault(paragraph.Page) ?? [], paragraph.Polygon));

        private static string Value(GraphCell cell) => cell.ValueType == CellValueType.Date ? cell.TypedValue ?? cell.RawValue : cell.RawValue;

        /// <summary>
        /// Y of the top-left corner (CU/DI polygons start top-left, clockwise). On a skewed scan the bounding-box top of a
        /// full-width table can sit above the heading that precedes it; top-left corners, both near the left margin, do not
        /// (brief 08: a 2° scan attached a period's table to the previous period).
        /// </summary>
        /// <summary>An amount as runs print it: a dollar sign, a thousands group or cents.</summary>
        [GeneratedRegex(@"\$\s*\d|\d,\d{3}|\d\.\d{2}\b")]
        private static partial Regex Amount();

        private static double Top(List<double>? polygon) => polygon is { Count: >= 8 } ? polygon[1] : 0;

    }

    internal static string Key(string text) => CanonicalComparer.NormalizeText(text);

    private static string Strip(string path, string prefix) => path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;

    private static void Set(JsonObject root, string path, JsonNode value)
    {
        var parts = path.Split('.');
        var node = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (node[parts[i]] is not JsonObject child)
            {
                node[parts[i]] = child = [];
            }

            node = child;
        }

        node[parts[^1]] = value;
    }

    private static JsonNode? Get(JsonObject root, string path)
    {
        JsonNode? node = root;
        foreach (var part in path.Split('.'))
        {
            node = node is JsonObject o ? o[part] : null;
        }

        return node;
    }
}
