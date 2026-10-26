using System.Globalization;
using System.Text.Json;
using LossRun.Contracts;
using System.Text;

namespace LossRun.Evaluation;

public sealed record Metric(string Segment, Proportion Value);

public sealed record ThresholdRow(string Criterion, string Segment, double Threshold, string Basis, Proportion Measured, bool Pass, bool RobustPass);

public sealed record LatencyRow(string Segment, int Docs, double P50Seconds, double P95Seconds, decimal MeanCostUsd, decimal TotalCostUsd, int LlmCalls);

public sealed record RoutingRow(string Carrier, string InputType, int Docs, IReadOnlyDictionary<string, int> Tiers);

public sealed record ErrorRow(string Segment, int Ok, int Missing, int Wrong, int WrongButPlausible);

public sealed record StratumRow(string Stratum, int Docs, Proportion QuoteSuccess, Proportion KeyFieldAccuracy, int Review);

/// <param name="PreFill">The same measure on Review documents' pre-fill (what a reviewer starts from), where it applies.</param>
public sealed record RequirementRow(string Criterion, string Measured, string ProposedTarget, string? PreFill = null);

/// <summary>One 08 Answers target: delivered value, pass, and the pre-fill alongside.</summary>
public sealed record TargetRow(string Target, string Segment, double Threshold, Proportion? Measured, bool Pass, string? Note = null);

/// <summary>
/// One row of the deck table (08b Answers 3): an input type, never a blend. Delivered = key-field accuracy on the documents
/// that left without a human; review rate = documents routed to a human; pre-fill = key-field accuracy of what the reviewer
/// starts from on those. <paramref name="Committed"/> rows are the ones the proposal commits a target to.
/// </summary>
/// <param name="ConsistentDocs">Source-consistent documents of the row (the data-quality and quote rates are over these, 08c Answers 2).</param>
/// <param name="DqDelivered">Data-quality first pass (0 hard failures) on the delivered source-consistent documents.</param>
/// <param name="QuoteDelivered">Quote success (priceable) on the delivered source-consistent documents.</param>
public sealed record DeckRow(string Row, int Docs, bool Committed, double? Target, Proportion Delivered, Proportion ReviewRate, Proportion PreFill,
    double ModelCallsPerDocument, string Note, int ConsistentDocs = 0, Proportion? DqDelivered = null, Proportion? QuoteDelivered = null,
    Proportion? ConsistentReviewRate = null)
{
    /// <summary>Fewer documents than this in a row: shown as "insufficient sample", never as a met/not-met verdict (08c Answers).</summary>
    public const int MinSample = 5;

    public bool Insufficient => Docs < MinSample;
}

/// <summary>Model calls per document for one carrier (learn / delta / repair; the classifier is CU, not a model call).</summary>
public sealed record CallsRow(string Carrier, int Docs, int LearnCalls, double PerDocument, int ZeroCallDocs);

/// <summary>The evaluation report (JSON + Markdown): every number the acceptance table and the bake-off protocol ask for.</summary>
public sealed class EvalReport
{
    public RunMetadata? Run { get; set; }

    /// <summary>Brief 31 Answers 2: the truth the numbers are scored against (generator version and seed), stamped on every table.</summary>
    public string? TruthVersion { get; set; }
    public int Documents { get; set; }
    public int FieldValues { get; set; }
    public decimal TotalCostUsd { get; set; }
    public List<Metric> KeyFieldAccuracy { get; set; } = [];
    public List<Metric> KeyFieldAccuracyByTemplate { get; set; } = [];
    public List<Metric> AllFieldAccuracy { get; set; } = [];
    public List<Metric> ClaimJointPass { get; set; } = [];
    public List<Metric> DocumentAllCorrect { get; set; } = [];
    public List<Metric> ClaimRowRecall { get; set; } = [];
    public List<Metric> ClaimRowPrecision { get; set; } = [];
    public Proportion ClassificationPrecision { get; set; } = null!;
    public Proportion ClassificationRecall { get; set; } = null!;
    public string BundleSplitExact { get; set; } = "";
    public List<Metric> DqFirstPass { get; set; } = [];
    public List<Metric> QuoteSuccess { get; set; } = [];
    public List<Metric> Stp { get; set; } = [];
    public List<Metric> StpStrict { get; set; } = [];
    public Proportion GuardrailSourceInconsistentToReview { get; set; } = null!;
    public List<ThresholdRow> Thresholds { get; set; } = [];
    public List<RequirementRow> Requirement { get; set; } = [];
    public List<Metric> FieldMatrix { get; set; } = [];
    public List<RoutingRow> Routing { get; set; } = [];
    public List<LatencyRow> LatencyByBucket { get; set; } = [];
    public List<LatencyRow> LatencyByTier { get; set; } = [];
    public List<ErrorRow> Errors { get; set; } = [];
    public List<StratumRow> Strata { get; set; } = [];
    public List<Metric> PreFillKeyFieldAccuracy { get; set; } = [];
    public List<Metric> PreFillAllFieldAccuracy { get; set; } = [];
    public List<TargetRow> Targets { get; set; } = [];
    public List<CallsRow> CallsByCarrier { get; set; } = [];
    public List<DeckRow> Deck { get; set; } = [];
}

public static class Report
{
    private static readonly string[] Buckets = ["native-pdf", "xlsx-csv", "scan"];

    public static EvalReport Build(Scorer.Scored s, int bootstrap)
    {
        Proportion P(IEnumerable<FieldObservation> obs) => Stats.Estimate([.. obs.Select(o => (o.Doc, o.Match))], bootstrap);
        Proportion D(IEnumerable<DocOutcome> docs, Func<DocOutcome, bool> success) => Stats.Estimate([.. docs.Select(d => (d.Doc, success(d)))], bootstrap);
        IEnumerable<(string Segment, Func<FieldObservation, bool> Filter)> FieldSegments() =>
        [
            ("all", _ => true), ("native-pdf", o => o.Bucket == "native-pdf"), ("xlsx-csv", o => o.Bucket == "xlsx-csv"), ("scan (all)", o => o.Bucket == "scan"),
            (InputTypes.Scan300, o => o.InputType == InputTypes.Scan300), (InputTypes.Scan200, o => o.InputType == InputTypes.Scan200), (InputTypes.Fax150, o => o.InputType == InputTypes.Fax150),
        ];
        IEnumerable<(string Segment, Func<DocOutcome, bool> Filter)> DocSegments() =>
        [
            ("all", _ => true), ("native-pdf", d => d.Bucket == "native-pdf"), ("xlsx-csv", d => d.Bucket == "xlsx-csv"), ("scan (all)", d => d.Bucket == "scan"),
            (InputTypes.Scan300, d => d.InputType == InputTypes.Scan300), (InputTypes.Scan200, d => d.InputType == InputTypes.Scan200), (InputTypes.Fax150, d => d.InputType == InputTypes.Fax150),
        ];

        var key = s.Fields.Where(f => Scorer.KeyFields.Contains(f.Field)).ToList();
        var claims = s.Fields.Where(f => f.Scope == "claim").GroupBy(f => (f.Doc, f.Row)).Select(g => (g.Key.Doc, Bucket: g.First().Bucket, InputType: g.First().InputType,
            Match: g.All(x => x.Match))).ToList();
        var consistent = s.Docs.Where(d => d.SourceConsistent).ToList();
        var r = new EvalReport
        {
            Run = s.Run,
            TruthVersion = s.TruthVersion,
            Documents = s.Docs.Count,
            FieldValues = s.Fields.Count,
            TotalCostUsd = s.Docs.Sum(d => d.CostUsd),
            KeyFieldAccuracy = [.. FieldSegments().Select(x => new Metric(x.Segment, P(key.Where(x.Filter))))],
            KeyFieldAccuracyByTemplate = [.. new[] { "seen", "first-seen", "unseen-repeat" }.SelectMany(t => Buckets.Select(b =>
                new Metric($"{t} · {b}", P(key.Where(o => o.Template == t && o.Bucket == b))))).Where(m => m.Value.N > 0)],
            AllFieldAccuracy = [.. FieldSegments().Select(x => new Metric(x.Segment, P(s.Fields.Where(x.Filter))))],
            ClaimJointPass = [.. new (string, Func<(string Doc, string Bucket, string InputType, bool Match), bool>)[]
            {
                ("all", _ => true), ("native-pdf", c => c.Bucket == "native-pdf"), ("xlsx-csv", c => c.Bucket == "xlsx-csv"), ("scan (all)", c => c.Bucket == "scan"),
                (InputTypes.Scan300, c => c.InputType == InputTypes.Scan300), (InputTypes.Scan200, c => c.InputType == InputTypes.Scan200), (InputTypes.Fax150, c => c.InputType == InputTypes.Fax150),
            }.Select(x => new Metric(x.Item1, Stats.Estimate([.. claims.Where(x.Item2).Select(c => (c.Doc, c.Match))], bootstrap)))],
            DocumentAllCorrect = [.. DocSegments().Select(x => new Metric(x.Segment, D(s.Docs.Where(x.Filter), d => d.AllFieldsCorrect)))],
            ClaimRowRecall = [.. DocSegments().Select(x => new Metric(x.Segment, Ratio(s.Docs.Where(x.Filter), d => d.MatchedClaims, d => d.TruthClaims)))],
            ClaimRowPrecision = [.. DocSegments().Select(x => new Metric(x.Segment, Ratio(s.Docs.Where(x.Filter), d => d.MatchedClaims, d => d.PredictedClaims)))],
            ClassificationPrecision = Counts(s.Classification.TruePositive, s.Classification.TruePositive + s.Classification.FalsePositive),
            ClassificationRecall = Counts(s.Classification.TruePositive, s.Classification.TruePositive + s.Classification.FalseNegative),
            BundleSplitExact = $"{s.Classification.BundleSplitsExact}/{s.Classification.Bundles}",
            DqFirstPass = [.. DocSegments().Select(x => new Metric(x.Segment, D(consistent.Where(x.Filter), d => d.DqPass)))],
            QuoteSuccess = [.. DocSegments().Select(x => new Metric(x.Segment, D(consistent.Where(x.Filter), d => d.QuoteSuccess)))],
            Stp = [.. new[] { "seen", "first-seen", "unseen-repeat" }.Select(t => new Metric(t, D(consistent.Where(d => d.Template == t), d => d.Stp)))],
            StpStrict = [.. new[] { "seen", "first-seen", "unseen-repeat" }.Select(t => new Metric(t, D(consistent.Where(d => d.Template == t), d => d.StpStrict)))],
            GuardrailSourceInconsistentToReview = D(s.Docs.Where(d => !d.SourceConsistent), d => !d.Stp),
            FieldMatrix = [.. s.Fields.GroupBy(f => (f.Field, f.Bucket)).OrderBy(g => g.Key.Field).ThenBy(g => g.Key.Bucket)
                .Select(g => new Metric($"{g.Key.Field} · {g.Key.Bucket}", P(g)))],
            Routing = [.. s.Docs.GroupBy(d => (d.Carrier, d.InputType)).OrderBy(g => g.Key)
                .Select(g => new RoutingRow(g.Key.Carrier, g.Key.InputType, g.Count(), g.GroupBy(d => d.FinalTier).ToDictionary(t => t.Key, t => t.Count())))],
            // Segments with no document are left out: an empty percentile is NaN, which JSON cannot carry (a --limit run or an import).
            LatencyByBucket = [.. DocSegments().Select(x => Latency(x.Segment, s.Docs.Where(x.Filter).ToList())).Where(r => r.Docs > 0)],
            LatencyByTier = [.. s.Docs.GroupBy(d => d.FinalTier).OrderBy(g => g.Key).Select(g => Latency(g.Key, [.. g]))],
            Errors = [.. FieldSegments().Select(x =>
            {
                var obs = s.Fields.Where(x.Filter).ToList();
                return new ErrorRow(x.Segment, obs.Count(o => o.Error == "ok"), obs.Count(o => o.Error == "missing"), obs.Count(o => o.Error == "wrong"),
                    obs.Count(o => o.Error == "wrong-but-plausible"));
            })],
            Strata = [.. Strata.EdgeCases.Concat([Strata.Password, Strata.Bundle, Strata.NonWcRows]).Distinct().Select(st =>
            {
                var docs = s.Docs.Where(d => d.Strata.Contains(st)).ToList();
                return new StratumRow(st, docs.Count, D(docs, d => d.QuoteSuccess), P(key.Where(o => docs.Any(d => d.Doc == o.Doc))), docs.Count(d => d.FinalTier == "Review"));
            }).Where(x => x.Docs > 0)],
        };

        var preFill = s.PreFillFields ?? [];
        var preKey = preFill.Where(f => Scorer.KeyFields.Contains(f.Field)).ToList();
        r.PreFillKeyFieldAccuracy = [.. FieldSegments().Select(x => new Metric(x.Segment, P(preKey.Where(x.Filter))))];
        r.PreFillAllFieldAccuracy = [.. FieldSegments().Select(x => new Metric(x.Segment, P(preFill.Where(x.Filter))))];
        r.CallsByCarrier = [.. s.Docs.GroupBy(d => d.Carrier).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new CallsRow(g.Key, g.Count(), g.Sum(d => d.LearnCalls), Math.Round((double)g.Sum(d => d.LearnCalls) / g.Count(), 3), g.Count(d => d.LearnCalls == 0)))];

        r.Deck = Deck(s, key, preKey, bootstrap);

        // 08 Answers targets (what "fixed" means for 08b): delivered values, recurring templates where stated.
        var recurring = key.Where(o => o.Template != "first-seen").ToList();
        Proportion Key(Func<FieldObservation, bool> filter) => P(key.Where(filter));
        var c12 = s.Docs.Where(d => d.Carrier is "c1" or "c2").ToList();
        var c12Calls = c12.Count == 0 ? 0 : (double)c12.Sum(d => d.LearnCalls) / c12.Count;
        r.Targets =
        [
            Target("Recurring-template delivered key-field accuracy", "native PDF", 0.99, P(recurring.Where(o => o.Bucket == "native-pdf"))),
            Target("Key fields", "scan ≥ 300 dpi", 0.95, Key(o => o.InputType == InputTypes.Scan300)),
            Target("Key fields", "scan 200 dpi rotated", 0.90, Key(o => o.InputType == InputTypes.Scan200)),
            new("Key fields (reported, not promised)", "fax 150 dpi", 0, Key(o => o.InputType == InputTypes.Fax150), true, "reported"),
            Target("DQ first pass (source-consistent)", "excluding narrative", 0.85, D(consistent.Where(d => d.Layout != "narrative"), d => d.DqPass)),
            Target("STP", "recurring templates", 0.85, D(consistent.Where(d => d.Template != "first-seen"), d => d.Stp)),
            Target("STP (context: fax is reported, not promised)", "recurring templates excluding fax", 0.85,
                D(consistent.Where(d => d.Template != "first-seen" && d.InputType != InputTypes.Fax150), d => d.Stp)),
            Target("DQ first pass (context)", "excluding narrative and fax", 0.85, D(consistent.Where(d => d.Layout != "narrative" && d.InputType != InputTypes.Fax150), d => d.DqPass)),
            Target("Recurring-template delivered key-field accuracy (context)", "native PDF, source-consistent", 0.99,
                P(recurring.Where(o => o.Bucket == "native-pdf" && s.Docs.First(d => d.Doc == o.Doc).SourceConsistent))),
            new("Learn/repair model calls per document", "c1 + c2", 0.3, null, c12Calls < 0.3, $"{c12Calls:0.000} per document ({c12.Sum(d => d.LearnCalls)} calls / {c12.Count} documents); target < 0.3"),
        ];

        Proportion Fields(string segment, params string[] names) => P(s.Fields.Where(o => names.Contains(o.Field) && Match(segment, o)));
        Proportion Joint(string segment) => r.ClaimJointPass.First(m => m.Segment == segment).Value;
        static bool Match(string segment, FieldObservation o) => segment switch
        {
            "native (PDF + XLSX/CSV)" => o.Bucket is "native-pdf" or "xlsx-csv",
            "scan ≥ 300 dpi" => o.InputType == InputTypes.Scan300,
            "scan < 200 dpi / fax" => o.InputType is InputTypes.Scan200 or InputTypes.Fax150,
            "scans" => o.Bucket == "scan",
            _ => true,
        };
        ThresholdRow Row(string criterion, string segment, double threshold, Proportion measured) =>
            new(criterion, segment, threshold, "evaluation-and-wc-domain.md A2 (Inferences)", measured, measured.N > 0 && measured.Value >= threshold, measured.N > 0 && measured.Lower >= threshold);

        string[] numbers = ["claim.claimNumber", "claim.dateOfInjury"];
        string[] money = ["claim.indemnity.paid", "claim.indemnity.reserve", "claim.medical.paid", "claim.medical.reserve"];
        r.Thresholds =
        [
            Row("Claim # and DOI, normalized exact", "native (PDF + XLSX/CSV)", 0.98, Fields("native (PDF + XLSX/CSV)", numbers)),
            Row("Claim # and DOI, normalized exact", "scan ≥ 300 dpi", 0.95, Fields("scan ≥ 300 dpi", numbers)),
            Row("Claim # and DOI, normalized exact", "scan < 200 dpi / fax", 0.90, Fields("scan < 200 dpi / fax", numbers)),
            Row("Indemnity/medical paid and reserve per claim", "native (PDF + XLSX/CSV)", 0.97, Fields("native (PDF + XLSX/CSV)", money)),
            Row("Indemnity/medical paid and reserve per claim", "scans", 0.93, Fields("scans", money)),
            Row("Claim-level completeness (all printed fields right)", "native (PDF + XLSX/CSV)", 0.90,
                Stats.Estimate([.. claims.Where(c => c.Bucket is "native-pdf" or "xlsx-csv").Select(c => (c.Doc, c.Match))], bootstrap)),
            Row("Claim-level completeness (all printed fields right)", "scans", 0.80, Joint("scan (all)")),
            Row("Straight-through (no human touch)", "recurring template (seen + unseen repeats)", 0.80,
                D(consistent.Where(d => d.Template != "first-seen"), d => d.Stp)),
            Row("Straight-through (no human touch)", "first-seen template", 0.65, r.Stp.First(m => m.Segment == "first-seen").Value),
            Row("Auto-classification precision (loss run vs other)", "bundles + singles", 0.97, r.ClassificationPrecision),
            Row("Auto-classification recall (loss run vs other)", "bundles + singles", 0.95, r.ClassificationRecall),
            Row("Data-quality first-pass rate (hard rules, consistent docs)", "all", 0.85, r.DqFirstPass.First(m => m.Segment == "all").Value),
        ];

        var latencyAll = r.LatencyByBucket.First(l => l.Segment == "all");
        string DeckCell(Func<DeckRow, Proportion> value) => string.Join("; ", r.Deck.Where(d => value(d).N > 0).Select(d => $"{d.Row} {value(d).Format()}"));
        r.Requirement =
        [
            new("Extraction accuracy (key fields: Claim #, DOI, paid/reserve splits) — by input type, see the deck table",
                DeckCell(d => d.Delivered), "committed rows: ≥ 99% native recurring, ≥ 98% native first-seen and XLSX/CSV, ≥ 95% scans ≥ 300 dpi",
                DeckCell(d => d.PreFill)),
            new("End-to-end latency, ≤ 15 MB upload (per document, in-process pipeline incl. CU)",
                $"p50 {latencyAll.P50Seconds:0.0} s, p95 {latencyAll.P95Seconds:0.0} s over {latencyAll.Docs} documents (deployed API figures: the API work)", "≤ 60 s p95 (proposed)"),
            new("Auto-classification precision / recall", $"{r.ClassificationPrecision.Format()} / {r.ClassificationRecall.Format()}; bundle split exact {r.BundleSplitExact}",
                "≥ 97% / ≥ 95% (A2)"),
            new("Data-quality validation pass rate, first pass",
                $"{r.DqFirstPass.First(m => m.Segment == "all").Value.Format()} — blended, mixes the by-design review rows (fax, narrative); not for the deck: use the by-input-type table", "≥ 85% (A2)"),
            new("Quote success rate (priceable submissions)",
                $"{r.QuoteSuccess.First(m => m.Segment == "all").Value.Format()} — blended, mixes the by-design review rows; not for the deck: use the by-input-type table", "≥ 90% of valid uploads (proposed)"),
        ];
        return r;
    }

    /// <summary>Deck rows, in deck order; every document falls in exactly one (narrative first, whatever its rendering).</summary>
    public static readonly string[] DeckRows =
        ["native PDF, recurring template", "native PDF, first-seen template", "XLSX / CSV", "scan ≥ 300 dpi", "scan 200 dpi rotated", "fax < 200 dpi", "narrative statement", "ZIP package",
         "construct: two-line records (c6)", "construct: one table per claim (c7)", "construct: stacked cells (c8)", "construct: component-sum headers (c9)",
         "construct: measures as rows (c10)"];

    // Corpus v4 (brief 19 Answers 5): the ADR 0018 construct carriers are their own rows, so the table shows them explicitly.
    public static string DeckRowOf(DocOutcome d) => d switch
    {
        _ when d.Strata.Contains(Strata.TwoLineRecords) => DeckRows[8],
        _ when d.Strata.Contains(Strata.ClaimPerTable) => DeckRows[9],
        _ when d.Strata.Contains(Strata.StackedColumns) => DeckRows[10],
        _ when d.Strata.Contains(Strata.ComponentSum) => DeckRows[11],
        _ when d.Strata.Contains(Strata.MeasuresAsRows) => DeckRows[12],
        { Layout: "narrative" } => DeckRows[6],
        { Bucket: "native-pdf", Template: "first-seen" } => DeckRows[1],
        { Bucket: "native-pdf" } => DeckRows[0],
        { Bucket: "xlsx-csv" } => DeckRows[2],
        { InputType: InputTypes.Scan300 } => DeckRows[3],
        { InputType: InputTypes.Scan200 } => DeckRows[4],
        { InputType: InputTypes.Zip } => DeckRows[7],
        _ => DeckRows[5],
    };

    private static List<DeckRow> Deck(Scorer.Scored s, List<FieldObservation> key, List<FieldObservation> preKey, int bootstrap)
    {
        var rows = new List<DeckRow>();
        foreach (var name in DeckRows)
        {
            var docs = s.Docs.Where(d => DeckRowOf(d) == name).ToList();
            if (docs.Count == 0)
            {
                continue;
            }

            var delivered = docs.Where(d => d.Extracted).Select(d => d.Doc).ToHashSet(StringComparer.Ordinal);
            var all = docs.Select(d => d.Doc).ToHashSet(StringComparer.Ordinal);
            var deliveredAccuracy = Stats.Estimate([.. key.Where(o => delivered.Contains(o.Doc)).Select(o => (o.Doc, o.Match))], bootstrap);
            var reviewRate = Stats.Estimate([.. docs.Select(d => (d.Doc, !d.Extracted))], bootstrap);
            var preFill = Stats.Estimate([.. preKey.Where(o => all.Contains(o.Doc)).Select(o => (o.Doc, o.Match))], bootstrap);
            var calls = Math.Round((double)docs.Sum(d => d.LearnCalls) / docs.Count, 3);
            var (committed, target) = name switch
            {
                "native PDF, recurring template" => (true, 0.99),
                "native PDF, first-seen template" or "XLSX / CSV" => (true, 0.98),
                "scan ≥ 300 dpi" => (true, 0.95),
                _ => (false, (double?)null),
            };
            var note = committed && docs.Count < DeckRow.MinSample
                ? $"insufficient sample (n = {docs.Count} document{(docs.Count == 1 ? "" : "s")}): no verdict"
                : committed
                ? deliveredAccuracy.N == 0 ? "nothing delivered" : deliveredAccuracy.Value >= target ? deliveredAccuracy.Lower >= target ? "met" : "met (CI lower bound below target)" : "**not met**"
                : name.StartsWith("construct:", StringComparison.Ordinal)
                    ? $"measured, not committed (construct coverage: first document learned, then replayed); review rate {Pct(reviewRate.Value)}"
                : name is "scan 200 dpi rotated" or "ZIP package"
                    ? $"measured, not committed; review rate {Pct(reviewRate.Value)}, pre-fill {(preFill.N == 0 ? "—" : Pct(preFill.Value))}"
                    : name == "narrative statement"
                        ? "routed to review by design; pre-fill is the statement's paragraphs, no structured values"
                        : $"routed to review by design; pre-fill {(preFill.N == 0 ? "—" : Pct(preFill.Value))}";
            var consistent = docs.Where(d => d.SourceConsistent).ToList();
            var consistentDelivered = consistent.Where(d => d.Extracted).ToList();
            rows.Add(new DeckRow(name, docs.Count, committed, target, deliveredAccuracy, reviewRate, preFill, calls, note, consistent.Count,
                Stats.Estimate([.. consistentDelivered.Select(d => (d.Doc, d.DqPass))], bootstrap), Stats.Estimate([.. consistentDelivered.Select(d => (d.Doc, d.QuoteSuccess))], bootstrap),
                Stats.Estimate([.. consistent.Select(d => (d.Doc, !d.Extracted))], bootstrap)));
        }

        return rows;
    }

    private static string Pct(double value) => (value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    /// <summary>The synthetic-corpus caveat that sits on the same slide as the deck table (08c Answers).</summary>
    public const string DeckCaveat =
        "Synthetic corpus: generated documents from five fictional carriers, not client documents. Real carrier documents will be messier; " +
        "the sealed blind set on the client's archive is the number that counts. Rows with fewer than 5 documents are an insufficient sample, not a result.";

    /// <summary>
    /// The versions a table was produced with: run time, code, mode, models, prompt, gate, catalog, prices, the run's corpus and (brief 31 Answers 2) the
    /// truth it is scored against. Null without a run.json and a truth version.
    /// </summary>
    public static string? Stamp(EvalReport r)
    {
        var run = r.Run;
        var truth = r.TruthVersion is { } t ? $"truth `{t}`" : null;
        if (run is null)
        {
            return truth is null ? null : $"Scored against {truth}";
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"Run {run.StartedAt:yyyy-MM-dd HH:mm}–{run.FinishedAt:HH:mm} UTC · git `{run.GitSha[..Math.Min(10, run.GitSha.Length)]}` · mode {run.Mode}{(run.CuInline ? " (CU inline)" : " (CU async)")} · "
            + $"learn `{run.LearnModel}@{run.LearnModelVersion}` · prompt `{run.PromptVersion}` · gate `{run.LearnGateVersion}` · catalog `{run.CatalogVersion}` · prices `{run.PriceTableVersion}` · "
            + $"corpus `{run.CorpusGeneratorVersion}`{(run.CorpusSeed is { } seed ? $" seed {seed}" : "")}{(truth is null ? "" : $" · {truth}")}");
    }

    /// <summary>The deck table on its own (deck-table.md): rows by input type, three columns with CIs, no blended headline.</summary>
    public static string DeckMarkdown(EvalReport r)
    {
        static string Cell(Proportion? p, DeckRow row, string none) =>
            p is null || p.N == 0 ? none : row.Insufficient ? $"insufficient sample (n = {row.Docs} document{(row.Docs == 1 ? "" : "s")})" : p.Format();

        var sb = new StringBuilder();
        if (Stamp(r) is { } stamp)
        {
            sb.AppendLine(stamp).AppendLine();
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"> {DeckCaveat}");
        sb.AppendLine();
        sb.AppendLine("| Input type | Docs | Delivered key-field accuracy | Review rate | Pre-fill key-field accuracy (reviewed docs) | Learn calls / doc | Commitment |");
        sb.AppendLine("|---|---:|---|---|---|---:|---|");
        foreach (var d in r.Deck)
        {
            var commitment = d.Committed ? $"≥ {d.Target:P0} delivered — {d.Note}" : d.Note;
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {d.Row} | {d.Docs} | {Cell(d.Delivered, d, "— (none delivered)")} | {Cell(d.ReviewRate, d, "—")} | {Cell(d.PreFill, d, "— (none reviewed)")} | {d.ModelCallsPerDocument:0.000} | {commitment} |");
        }

        sb.AppendLine();
        sb.AppendLine("Data-quality first pass and quote success by input type (source-consistent documents; rates on the delivered ones):");
        sb.AppendLine();
        sb.AppendLine("| Input type | Docs | DQ first pass (delivered) | Quote success (delivered) | Review rate | n delivered |");
        sb.AppendLine("|---|---:|---|---|---|---:|");
        foreach (var d in r.Deck)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {d.Row} | {d.ConsistentDocs} | {Cell(d.DqDelivered, d, "— (none delivered)")} | {Cell(d.QuoteDelivered, d, "— (none delivered)")} | {Cell(d.ConsistentReviewRate, d, "—")} | {d.DqDelivered?.N ?? 0} |");
        }

        return sb.ToString();
    }

    private static TargetRow Target(string target, string segment, double threshold, Proportion measured) =>
        new(target, segment, threshold, measured, measured.N > 0 && measured.Value >= threshold, measured.N > 0 && measured.Lower < threshold && measured.Value >= threshold ? "CI lower bound below target" : null);

    private static Proportion Counts(int successes, int n) => Stats.Estimate([.. Enumerable.Range(0, n).Select(i => ($"x{i}", i < successes))], 0);

    private static Proportion Ratio(IEnumerable<DocOutcome> docs, Func<DocOutcome, int> num, Func<DocOutcome, int> den)
    {
        var obs = new List<(string, bool)>();
        foreach (var d in docs)
        {
            var hits = num(d);
            var total = Math.Max(den(d), hits);
            obs.AddRange(Enumerable.Range(0, total).Select(i => (d.Doc, i < hits)));
        }

        return Stats.Estimate(obs, 200);
    }

    private static LatencyRow Latency(string segment, List<DocOutcome> docs) =>
        new(segment, docs.Count, Math.Round(Stats.Percentile([.. docs.Select(d => d.Seconds)], 0.5), 1), Math.Round(Stats.Percentile([.. docs.Select(d => d.Seconds)], 0.95), 1),
            docs.Count == 0 ? 0 : Math.Round(docs.Average(d => d.CostUsd), 5), Math.Round(docs.Sum(d => d.CostUsd), 4), docs.Sum(d => d.LlmCalls));

    // ------------------------------------------------------------------ Markdown

    public static string Markdown(EvalReport r)
    {
        var sb = new StringBuilder();
        var run = r.Run;
        sb.AppendLine("# Evaluation report — synthetic corpus");
        sb.AppendLine();
        if (Stamp(r) is { } stamp)
        {
            sb.AppendLine(stamp);
            sb.AppendLine();
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{r.Documents} documents · {r.FieldValues:N0} scored field values · metered cost ${r.TotalCostUsd:0.00}");
        sb.AppendLine();
        sb.AppendLine("Intervals: 95% Agresti-Coull on the cluster-adjusted effective n (document-level bootstrap design effect).");
        sb.AppendLine();
        sb.AppendLine("## Deck table — by input type (`docs/evaluation/results/2026-10-22-corpus-v2/`)");
        sb.AppendLine();
        sb.AppendLine("Every document is in exactly one row; no blended headline. Delivered = key fields (Claim #, DOI, indemnity/medical paid and reserve) on documents that left without a human. " +
            "Review rate = documents routed to a human. Pre-fill = key fields of what the reviewer starts from on those documents. Committed rows carry a proposal target; the others carry the measured numbers.");
        sb.AppendLine();
        sb.Append(DeckMarkdown(r));
        sb.AppendLine();
        sb.AppendLine("## Requirement acceptance criteria (placeholders filled)");
        sb.AppendLine();
        sb.AppendLine("Delivered = values that leave without a human (a Review document delivers nothing). Pre-fill = the same measure on Review documents' pre-fill, what the reviewer starts from (scoring-protocol.md §3).");
        sb.AppendLine();
        sb.AppendLine("| Criterion | Delivered (no human) | Pre-fill (Review documents) | Proposed target |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var row in r.Requirement)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {row.Criterion} | {row.Measured} | {row.PreFill ?? "—"} | {row.ProposedTarget} |");
        }

        sb.AppendLine();
        sb.AppendLine("## The evaluation harness targets");
        sb.AppendLine();
        sb.AppendLine("| Target | Segment | Threshold | Measured (delivered) | Result |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var t in r.Targets)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {t.Target} | {t.Segment} | {(t.Threshold == 0 ? "—" : t.Threshold.ToString(t.Threshold < 1 && t.Target.StartsWith("Learn", StringComparison.Ordinal) ? "0.0" : "P0", CultureInfo.InvariantCulture))} | {t.Measured?.Format() ?? t.Note} | {(t.Note == "reported" ? "reported" : t.Pass ? "met" : "**not met**")}{(t.Note is { } n && n != "reported" && t.Measured is not null ? $" ({n})" : "")} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Model calls per document by carrier (learn / delta / repair)");
        sb.AppendLine();
        sb.AppendLine("| Carrier | Docs | Calls | Per document | Docs with 0 calls |");
        sb.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var c in r.CallsByCarrier)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {c.Carrier} | {c.Docs} | {c.LearnCalls} | {c.PerDocument:0.000} | {c.ZeroCallDocs} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Thresholds (research A2) — pass/fail");
        sb.AppendLine();
        sb.AppendLine("| Criterion | Segment | Threshold | Measured | Pass | CI lower ≥ threshold |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var t in r.Thresholds)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {t.Criterion} | {t.Segment} | {t.Threshold:P0} | {t.Measured.Format()} | {(t.Pass ? "pass" : "**fail**")} | {(t.RobustPass ? "yes" : "no")} |");
        }

        void Metrics(string title, IEnumerable<Metric> metrics)
        {
            sb.AppendLine();
            sb.AppendLine($"## {title}");
            sb.AppendLine();
            sb.AppendLine("| Segment | Value |");
            sb.AppendLine("|---|---|");
            foreach (var m in metrics.Where(m => m.Value.N > 0))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {m.Segment} | {m.Value.Format()} |");
            }
        }

        Metrics("Key-field accuracy (Claim #, DOI, indemnity/medical paid and reserve)", r.KeyFieldAccuracy);
        Metrics("Key-field accuracy by template (seen / first-seen / unseen repeats)", r.KeyFieldAccuracyByTemplate);
        Metrics("Pre-fill key-field accuracy (Review documents: what the reviewer starts from)", r.PreFillKeyFieldAccuracy);
        Metrics("Pre-fill, all printed fields (Review documents)", r.PreFillAllFieldAccuracy);
        Metrics("All printed fields", r.AllFieldAccuracy);
        Metrics("Claim joint pass (every printed field of the claim right)", r.ClaimJointPass);
        Metrics("Document: every printed field right", r.DocumentAllCorrect);
        Metrics("Claim rows: recall (truth rows found)", r.ClaimRowRecall);
        Metrics("Claim rows: precision (extracted rows that exist)", r.ClaimRowPrecision);
        Metrics("Data-quality first pass (0 hard failures; source-consistent documents)", r.DqFirstPass);
        Metrics("Quote success (priceable: replay/learn/delta, 0 hard failures)", r.QuoteSuccess);
        Metrics("Straight-through (no document review)", r.Stp);
        Metrics("Straight-through, strict (also no confirm-format item)", r.StpStrict);
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Classification precision {r.ClassificationPrecision.Format()}, recall {r.ClassificationRecall.Format()}; bundle loss-run split exact {r.BundleSplitExact}. " +
            $"Guardrail: source-inconsistent documents kept out of straight-through {r.GuardrailSourceInconsistentToReview.Format()}.");

        sb.AppendLine();
        sb.AppendLine("## Routing by carrier and input type");
        sb.AppendLine();
        sb.AppendLine("| Carrier | Input | Docs | Final tiers |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var row in r.Routing)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {row.Carrier} | {row.InputType} | {row.Docs} | {string.Join(", ", row.Tiers.OrderByDescending(t => t.Value).Select(t => $"{t.Key} {t.Value}"))} |");
        }

        void Latency(string title, IEnumerable<LatencyRow> rows)
        {
            sb.AppendLine();
            sb.AppendLine($"## {title}");
            sb.AppendLine();
            sb.AppendLine("| Segment | Docs | p50 s | p95 s | Mean cost $ | Total cost $ | LLM calls |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var row in rows.Where(x => x.Docs > 0))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {row.Segment} | {row.Docs} | {row.P50Seconds:0.0} | {row.P95Seconds:0.0} | {row.MeanCostUsd:0.0000} | {row.TotalCostUsd:0.00} | {row.LlmCalls} |");
            }
        }

        Latency("Latency and cost by input type (per document, in-process)", r.LatencyByBucket);
        Latency("Latency and cost by final path", r.LatencyByTier);

        sb.AppendLine();
        sb.AppendLine("## Error types (field values)");
        sb.AppendLine();
        sb.AppendLine("| Segment | ok | missing | wrong (caught by a hard rule) | wrong but plausible (silent) |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var e in r.Errors)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {e.Segment} | {e.Ok:N0} | {e.Missing:N0} | {e.Wrong:N0} | {e.WrongButPlausible:N0} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Edge-case strata");
        sb.AppendLine();
        sb.AppendLine("| Stratum | Docs | Quote success | Key-field accuracy | Review |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var st in r.Strata)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {st.Stratum} | {st.Docs} | {st.QuoteSuccess.Format()} | {st.KeyFieldAccuracy.Format()} | {st.Review} |");
        }

        return sb.ToString();
    }

    public static void WriteCsv(string outDir, Scorer.Scored s, int bootstrap)
    {
        var fieldCsv = new StringBuilder("field,carrier,input_type,n,accuracy\n");
        foreach (var g in s.Fields.GroupBy(f => (f.Field, f.Carrier, f.InputType)).OrderBy(g => g.Key))
        {
            fieldCsv.AppendLine(CultureInfo.InvariantCulture, $"{g.Key.Field},{g.Key.Carrier},{g.Key.InputType},{g.Count()},{(double)g.Count(x => x.Match) / g.Count():0.0000}");
        }

        File.WriteAllText(Path.Combine(outDir, "field-by-carrier-input.csv"), fieldCsv.ToString());
        var docCsv = new StringBuilder("doc,carrier,input_type,template,strata,final_tier,extracted,hard_failures,stp,quote_success,seconds,cost_usd,llm_calls,truth_claims,matched_claims,predicted_claims,all_correct,error\n");
        foreach (var d in s.Docs)
        {
            docCsv.AppendLine(CultureInfo.InvariantCulture,
                $"{d.Doc},{d.Carrier},{d.InputType},{d.Template},{string.Join("|", d.Strata)},{d.FinalTier},{d.Extracted},{d.HardFailures},{d.Stp},{d.QuoteSuccess},{d.Seconds:0.0},{d.CostUsd:0.000000},{d.LlmCalls},{d.TruthClaims},{d.MatchedClaims},{d.PredictedClaims},{d.AllFieldsCorrect},\"{d.Error?.Replace("\"", "'")}\"");
        }

        File.WriteAllText(Path.Combine(outDir, "documents.csv"), docCsv.ToString());
        var errCsv = new StringBuilder("doc,field,row,error,expected,actual\n");
        foreach (var f in s.Fields.Where(f => !f.Match))
        {
            errCsv.AppendLine(CultureInfo.InvariantCulture, $"{f.Doc},{f.Field},{f.Row},{f.Error},\"{f.Expected?.Replace("\"", "'")}\",\"{f.Actual?.Replace("\"", "'")}\"");
        }

        File.WriteAllText(Path.Combine(outDir, "field-errors.csv"), errCsv.ToString());

        // Foundry evaluation hook (brief 08 §6): custom code evaluators are a Python SDK feature, so the scorer's verdicts
        // are exported as an evaluation dataset (one row per scored value) for the Foundry portal's dataset evaluation.
        var foundry = new StringBuilder();
        foreach (var f in s.Fields)
        {
            foundry.AppendLine(JsonSerializer.Serialize(new
            {
                query = $"{f.Doc} {f.Field}{(f.Row is null ? "" : $" [{f.Row}]")}", response = f.Actual, ground_truth = f.Expected, f.Carrier, input_type = f.InputType,
                f.Template, exact_match = f.Match ? 1 : 0, error_type = f.Error,
            }, CanonicalJson.Options));
        }

        File.WriteAllText(Path.Combine(outDir, "foundry-dataset.jsonl"), foundry.ToString());
    }
}
