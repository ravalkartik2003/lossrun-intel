using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using LossRun.Contracts;
using LossRun.Data;

namespace LossRun.Evaluation;

/// <summary>config/tuning-config.json: the stopping criteria (brief 27; tuning-loop.md §5–6).</summary>
public sealed record TuningConfig
{
    public string Version { get; init; } = "";
    public double DefaultKeyFieldAccuracyLower { get; init; } = 0.95;
    public IReadOnlyList<StratumTarget> Targets { get; init; } = [];
    public int MinStratumDocuments { get; init; } = 5;
    public double MaxReviewRate { get; init; } = 0.2;
    public bool RequireZeroTier2InLastBatch { get; init; } = true;

    /// <summary>The TCO replay cost per document (docs/poc-plan/tco.md §3: a 12-page replay, 12 × $0.0063 ≈ $0.075).</summary>
    public decimal ReplayUsdPerDocument { get; init; }

    /// <summary>The TCO learn cost per new format (tco.md §3: $0.10–0.30 on a 12-page document; the upper end).</summary>
    public decimal LearnUsdPerFormat { get; init; }

    /// <summary>Share of a batch expected to be new formats (one per 200 in production, rounded up to 10 % for a diverse tuning set).</summary>
    public decimal ExpectedNewFormatsShare { get; init; }

    /// <summary>Documents in one tuning batch (the released tuning set: 100 files, blind-run.md §3).</summary>
    public int BatchDocuments { get; init; }

    /// <summary>New formats a batch is budgeted to learn: ceil(share × documents).</summary>
    public int ExpectedNewFormats => (int)Math.Ceiling(ExpectedNewFormatsShare * BatchDocuments);

    /// <summary>Brief 31 item 4: the per-batch spend cap, derived from the TCO (replay × documents + learn × expected new formats), never typed into the config.</summary>
    public decimal BatchBudgetUsd => Math.Round(ReplayUsdPerDocument * BatchDocuments + LearnUsdPerFormat * ExpectedNewFormats, 2, MidpointRounding.AwayFromZero);

    /// <summary>The cap's formula with its inputs, as the batch summary prints it.</summary>
    public string BudgetFormula => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"{ReplayUsdPerDocument} × {BatchDocuments} documents + {LearnUsdPerFormat} × ceil({ExpectedNewFormatsShare} × {BatchDocuments}) = {ExpectedNewFormats} new formats → {BatchBudgetUsd:0.00} USD (docs/poc-plan/tco.md §3)");
    public int MaxBatches { get; init; } = 5;
    public IReadOnlyList<string> GrammarPaths { get; init; } = [];

    /// <summary>Brief 27 Answers 3: paths that count as grammar only in commits labelled <c>tier-2</c> (e.g. FormatLearner.cs, where bug fixes also land).</summary>
    public IReadOnlyList<string> LabelledGrammarPaths { get; init; } = [];
    public string NonConvergence { get; init; } = "";

    public StratumTarget Target(string stratum) => Targets.FirstOrDefault(t => t.Stratum == stratum) ?? new StratumTarget(stratum, DefaultKeyFieldAccuracyLower);

    public static TuningConfig Load(string path) =>
        JsonSerializer.Deserialize<TuningConfig>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException(path);
}

/// <param name="Measure">delivered (key fields of delivered documents) or prefill (the reviewer's pre-fill: strata routed to review by design, e.g. fax).</param>
/// <param name="MaxReviewRate">A stratum's own review-rate cap (recurring templates: 5 %), on top of the overall cap.</param>
public sealed record StratumTarget(string Stratum, double KeyFieldAccuracyLower, string Measure = "delivered", double? MaxReviewRate = null);

/// <summary>One document's failure, with the cause category (a)–(g) and the rule that chose it — identifiers only, no values.</summary>
public sealed record TuneFailure(string Doc, string Stratum, string Carrier, string Category, string Why, string Key, IReadOnlyList<string> Evidence, string Source = "run");

/// <summary>A fix-list line: one cause on one format, carrier or rule, ranked by how many documents it would clear.</summary>
public sealed record TuneFix(int Rank, string Category, int Tier, string Key, int Documents, IReadOnlyList<string> Docs, IReadOnlyList<string> Evidence, string Action);

public sealed record StratumAccuracy(string Stratum, int Docs, Proportion KeyFieldAccuracy, double Target, string Status, string Measure = "delivered", Proportion? ReviewRate = null,
    double? MaxReviewRate = null);

public sealed record GrammarChanges(string? Since, string Until, IReadOnlyList<string> Files, int Commits);

public sealed record TuneVerdict(string Status, IReadOnlyList<string> Reasons);

/// <summary>batch.json: what the next batch compares against.</summary>
public sealed record TuneBatch(int Batch, string Sha, string Mode, DateTimeOffset At, string Corpus, int Documents, IReadOnlyDictionary<string, int> Routes,
    IReadOnlyList<StratumAccuracy> Strata, Proportion ReviewRate, double ModelCallsPerDocument, decimal CostUsd, decimal? BudgetUsd, GrammarChanges Grammar,
    int Tier2Changes, int NotRun, IReadOnlyList<TuneFailure> Failures, IReadOnlyList<TuneFix> Fixes, TuneVerdict Verdict, string ConfigVersion);

/// <summary>
/// <c>lossrun tune --corpus &lt;released tuning set&gt; --truth &lt;csv&gt; --out &lt;batch dir&gt; [--mode local|api|stub] [--budget usd]
/// [--seal seal-manifest.json] [--tuning-items &lt;dir&gt;] [--cu-cache &lt;dir&gt;] [--config config/tuning-config.json]</c> (brief 27): one tuning
/// batch — run, align and build truth from the archive CSV, score (<see cref="Scorer"/>), classify every failure into the
/// tuning-loop causes deterministically, and write <c>batch-report.md</c>, <c>batch.json</c> and a pull-request body per design
/// (tier 2) fix. The previous batch is the sibling folder with the highest lower number (<c>batch-01</c> before <c>batch-02</c>).
/// It never reads a blind file: with <c>--seal</c> it runs only the seal's tuning list, and it refuses a corpus under <c>blind/</c>.
/// </summary>
public static class Tune
{
    public static readonly IReadOnlyDictionary<string, (string Name, int Tier, string Action)> Causes = new Dictionary<string, (string, int, string)>(StringComparer.Ordinal)
    {
        ["a"] = ("glossary gap", 1, "Add the header or value alias to docs/evaluation/glossary.json (or accept the learned alias), with a test; regression gate."),
        ["b"] = ("layout not expressible", 2, "A spec-language construct or lint repair (design change: ADR, corpus v5 run, human merge); meanwhile these documents go to review."),
        ["c"] = ("classifier miss", 2, "Classifier threshold or category change (design change); check the segment pages first."),
        ["d"] = ("scan quality", 2, "Leave to review with pre-fill unless a gate threshold should move (design change); re-request a native copy where possible."),
        ["e"] = ("rule too strict or loose", 2, "Rule semantics in docs/evaluation/rule-catalog.json (design change: ADR); values match the archive while the rule fails."),
        ["f"] = ("source inconsistency", 1, "No code change: the document's own totals disagree; mark it in adjudication and keep it out of the fix count."),
        ["g"] = ("tool bug", 1, "Reproduce with a failing test, fix, regression gate."),
    };

    public static async Task<int> RunAsync(string corpus, string truthCsv, string outDir, string mode, decimal? budget, string? sealPath, string? tuningItems, string? cuCache,
        string? configPath)
    {
        if (IsBlindPath(corpus) || IsBlindPath(outDir) || (tuningItems is not null && IsBlindPath(tuningItems)))
        {
            Console.Error.WriteLine("tune: refusing a path under blind/ — the tuning loop never reads the blind set (tuning-loop.md §1).");
            return 2;
        }

        var config = TuningConfig.Load(configPath ?? FindFile("config/tuning-config.json") ?? throw new FileNotFoundException("config/tuning-config.json (run from inside the repository, or pass --config)"));
        var batch = BatchNumber(outDir);
        var previous = PreviousBatch(outDir);
        IReadOnlyCollection<string>? only = sealPath is null ? null : [.. Sealing.Load(sealPath).Tuning.Select(t => t.Id)];
        Directory.CreateDirectory(outDir);
        var results = Path.Combine(outDir, "results");

        Console.WriteLine($"tune batch {batch} ({mode}){(previous is null ? "" : $", after batch {previous.Batch} ({previous.Sha[..Math.Min(8, previous.Sha.Length)]})")}");
        var exit = await EvalRunner.RunAsync(corpus, results, mode, 4, false, null, null, false, only, cuCache).ConfigureAwait(false);
        if (exit != 0)
        {
            return exit;
        }

        // With --seal the archive is cut to the tuning documents' policies first: the blind set's claims never appear as
        // archive-only rows under the tune folder (brief 30 item 21). The tuning loop accepts the default decisions.
        var archive = truthCsv;
        if (only is not null)
        {
            var policies = TuningPolicies(corpus, only, results);
            var all = Alignment.ReadArchive(File.ReadAllText(truthCsv));
            var kept = FilterArchive(all, policies);
            Directory.CreateDirectory(Path.Combine(outDir, "align"));
            archive = Path.Combine(outDir, "align", "archive-tuning.csv");
            File.WriteAllText(archive, Alignment.WriteArchive(kept));
            Console.WriteLine($"archive cut to the tuning documents' {policies.Count} polic(ies): {kept.Count} of {all.Count} claim row(s) kept; the rest are not read");
        }

        Alignment.Run(archive, results, Path.Combine(outDir, "align"), null, corpus);
        TruthBuilder.Run(archive, Path.Combine(outDir, "align", "aligned.csv"), corpus, acceptDefaults: true);
        Scorer.Run(corpus, results, Path.Combine(outDir, "score"), 200);

        var scored = Scorer.Score(corpus, results);
        var runResults = Directory.EnumerateFiles(Path.Combine(results, "docs"), "*.json")
            .Select(f => JsonSerializer.Deserialize<RunResult>(File.ReadAllText(f), CanonicalJson.Options)!)
            .Where(r => only is null || only.Contains(r.Id)).ToDictionary(r => r.Id, StringComparer.Ordinal);
        var items = tuningItems is null ? [] : LoadTuningItems(tuningItems);
        var sha = Git("rev-parse HEAD").Trim();
        var grammar = GrammarSince(config, previous?.Sha, sha);
        var summary = Summarize(batch, sha, mode, corpus, scored, runResults, items, config, budget, grammar, previous, SpecResolver(results));

        File.WriteAllText(Path.Combine(outDir, "batch.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions(CanonicalJson.Options) { WriteIndented = true }));
        File.WriteAllText(Path.Combine(outDir, "batch-report.md"), Markdown(summary, config, previous));
        var prs = Path.Combine(outDir, "pr-bodies");
        Directory.CreateDirectory(prs);
        foreach (var fix in summary.Fixes.Where(f => f.Tier == 2))
        {
            File.WriteAllText(Path.Combine(prs, $"tier2-{fix.Rank:00}-{fix.Category}.md"), PullRequestBody(summary, fix));
        }

        Console.WriteLine($"batch {batch}: {summary.Documents} documents, review rate {summary.ReviewRate.Value:P0}, {summary.Failures.Count} failure(s), " +
            $"{summary.Fixes.Count(f => f.Tier == 2)} design fix(es), grammar changes {summary.Tier2Changes} → verdict {summary.Verdict.Status}");
        Console.WriteLine($"→ {Path.Combine(outDir, "batch-report.md")}");
        return 0;
    }

    // ------------------------------------------------------------------ the pieces (pure, tested)

    public static TuneBatch Summarize(int batch, string sha, string mode, string corpus, Scorer.Scored scored, IReadOnlyDictionary<string, RunResult> runs,
        IReadOnlyList<TuningItem> items, TuningConfig config, decimal? budget, GrammarChanges grammar, TuneBatch? previous, Func<string?, LossRun.Formats.CarrierFormat?>? specs = null)
    {
        var docs = scored.Docs.Where(d => runs.ContainsKey(d.Doc)).ToList();
        var notRun = docs.Count(d => IsNotRun(runs[d.Doc]));
        var counted = docs.Where(d => !IsNotRun(runs[d.Doc])).ToList();
        var routes = counted.GroupBy(d => d.Error is { } e && e != Scorer.NoTruth ? "Error" : d.FinalTier).OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var countedIds = counted.Select(d => d.Doc).ToHashSet(StringComparer.Ordinal);
        var keyFields = scored.Fields.Where(f => Scorer.KeyFields.Contains(f.Field) && countedIds.Contains(f.Doc)).ToList();
        var preFill = (scored.PreFillFields ?? []).Where(f => Scorer.KeyFields.Contains(f.Field) && countedIds.Contains(f.Doc)).ToList();
        var strata = counted.GroupBy(d => StratumOf(d.InputType, d.Template)).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
        {
            var target = config.Target(g.Key);
            var source = target.Measure == "prefill" ? preFill : keyFields;
            var obs = source.Where(f => StratumOf(f.InputType, f.Template) == g.Key).Select(f => (f.Doc, f.Match)).ToList();
            var p = Stats.Estimate(obs, 200);
            var review = Stats.Estimate([.. g.Select(d => (d.Doc, d.FinalTier == "Review"))], 200);
            var small = g.Count() < config.MinStratumDocuments;
            var status = small ? "insufficient sample" : obs.Count == 0 ? "no truth" : p.Lower < target.KeyFieldAccuracyLower ? "not met"
                : target.MaxReviewRate is { } cap && review.Value > cap ? "review rate above cap" : "met";
            return new StratumAccuracy(g.Key, g.Count(), p, target.KeyFieldAccuracyLower, status, target.Measure, review, target.MaxReviewRate);
        }).ToList();

        var reviewRate = Stats.Estimate([.. counted.Select(d => (d.Doc, d.FinalTier == "Review"))], 200);
        var calls = counted.Count == 0 ? 0 : (double)counted.Sum(d => d.LearnCalls) / counted.Count;
        var cost = counted.Sum(d => d.CostUsd);

        var failures = counted.SelectMany(d => Classify(d, runs[d.Doc], scored.Fields.Where(f => f.Doc == d.Doc).ToList(), specs)).ToList();
        failures.AddRange(items.Select(i => new TuneFailure(i.DocumentIds.FirstOrDefault() ?? i.EscalationId, "escalation", "", i.Category,
            $"escalation {i.EscalationId} ({i.ReasonCode}) categorised by a reviewer", i.FormatRef ?? i.FormatFingerprint, [i.ReasonCode], "escalation")));
        var fixes = Rank(failures);
        var tier2 = previous is null ? 0 : grammar.Files.Count;
        var verdict = Verdict(batch, strata, reviewRate, cost, budget ?? config.BatchBudgetUsd, tier2, previous, config);
        return new TuneBatch(batch, sha, mode, DateTimeOffset.UtcNow, Path.GetFileName(Path.GetFullPath(corpus).TrimEnd(Path.DirectorySeparatorChar)), counted.Count, routes, strata,
            reviewRate, Math.Round(calls, 3), cost, budget, grammar, tier2, notRun, failures, fixes, verdict, config.Version);
    }

    /// <summary>Brief 27 Answers 1: native PDFs split by template (the acceptance table's rows); every other stratum is its input type.</summary>
    public static string StratumOf(string inputType, string template) =>
        inputType == InputTypes.NativePdf ? (template == "first-seen" ? "native-pdf/first-seen" : "native-pdf/recurring") : inputType;

    /// <summary>A stub or offline run that could not read the file (no cached Content Understanding): not a failure of the system.</summary>
    public static bool IsNotRun(RunResult r) => r.Error?.Contains("stub run", StringComparison.Ordinal) == true;

    /// <summary>
    /// The cause of one document's failure, first rule that applies wins (the order is the playbook's): source inconsistency (f),
    /// scan quality (d), classifier miss (c), glossary gap (a), layout not expressible (b), rule too strict or loose (e), and
    /// anything else, including an exception, a tool bug (g). Evidence is rule ids and scrubbed reasons only.
    /// </summary>
    public static IReadOnlyList<TuneFailure> Classify(DocOutcome doc, RunResult run, IReadOnlyList<FieldObservation> fields,
        Func<string?, LossRun.Formats.CarrierFormat?>? specs = null)
    {
        var misses = fields.Where(f => !f.Match).ToList();
        var review = doc.FinalTier == "Review";
        var hardRules = run.Documents.SelectMany(d => d.HardFailureRules).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var failed = run.Error is not null || review || hardRules.Count > 0 || misses.Count > 0 || (doc.TruthClaims > 0 && run.Documents.Count == 0);
        if (!failed)
        {
            return [];
        }

        // The verifier's counter summary ("# claim rows; # hard / # soft failures; …") says nothing once scrubbed: keep the sentences.
        var reasons = EscalationTraceText.Sanitize(run.Documents.SelectMany(d => d.Reasons).Concat(run.Error is null ? [] : [run.Error])
                .SelectMany(r => r.Split("; ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            .Where(r => !r.StartsWith('#') && !r.StartsWith("totals n/a", StringComparison.Ordinal) && !r.StartsWith("required coverage", StringComparison.Ordinal)
                && !r.StartsWith("gate ", StringComparison.Ordinal)).ToList();
        var text = string.Join(" | ", run.Documents.SelectMany(d => d.Reasons)).ToLowerInvariant();
        var key = run.Documents.Select(d => d.FormatRef).FirstOrDefault(f => f is not null) ?? $"{doc.Carrier} · {doc.InputType}";
        TuneFailure F(string category, string why, string? k = null, IEnumerable<string>? evidence = null) =>
            new(doc.Doc, doc.InputType, doc.Carrier, category, why, k ?? key, [.. (evidence ?? []).Concat(hardRules).Concat(reasons).Distinct(StringComparer.Ordinal).Take(8)]);

        if (run.Error is { } error)
        {
            return [F("g", $"the run failed: {error.Split(':')[0]}", evidence: [error.Split(':')[0]])];
        }

        if (!doc.SourceConsistent)
        {
            return [F("f", "the corpus marks this document's printed totals as inconsistent")];
        }

        if (doc.InputType.StartsWith("scan", StringComparison.Ordinal) && (review || misses.Count > 0) || text.Contains("dpi") || text.Contains("ocr"))
        {
            return [F("d", "a scanned or faxed input that went to review or misread values")];
        }

        if (doc.TruthClaims > 0 && run.Documents.Count == 0 || text.Contains("classif") || run.Segments.Count > 0 && misses.Any(m => m.Error == "missing") && doc.PredictedClaims == 0)
        {
            return [F("c", "the archive has claims but no loss-run document was extracted (classified as something else, or not split)")];
        }

        if (text.Contains("unmapped header"))
        {
            return [F("a", "unmapped headers on the document's route")];
        }

        if (review && run.Documents.Any(d => !d.LearnPublished && d.ModelCalls > 0) || review && text.Contains("not learned"))
        {
            return [F("b", "the learn did not publish a spec that verifies (the layout is not expressible yet)")];
        }

        if (hardRules.Count > 0 && misses.Count == 0)
        {
            return [F("e", "values match the archive but hard rules fail", $"{key} · {string.Join(",", hardRules)}")];
        }

        if (misses.Count > 0)
        {
            var fieldNames = misses.Select(m => m.Field).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(5).ToList();
            // Brief 27 Answers 2: read the format's column map — an alias mistake (data) and a construct gap (design) have different owners.
            if (specs?.Invoke(run.Documents.Select(d => d.FormatRef).FirstOrDefault(f => f is not null)) is { } spec)
            {
                var votes = misses.GroupBy(m => m.Field, StringComparer.Ordinal).Select(g => SpecCause(spec, g.Key, text)).ToList();
                var winner = votes.GroupBy(v => v.Category).OrderByDescending(g => g.Count()).ThenByDescending(g => Causes[g.Key].Tier).First();
                return [F(winner.Key, $"spec {spec.Ref}: {string.Join("; ", winner.Select(v => v.Why).Distinct(StringComparer.Ordinal).Take(3))}", evidence: fieldNames)];
            }

            var missing = misses.Count(m => m.Error == "missing");
            var wrong = misses.Count - missing;
            return wrong >= missing
                ? [F("a", $"{wrong} value(s) read into the wrong field or with the wrong value: a column mapping (no spec to read: first cut)", evidence: fieldNames)]
                : [F("b", $"{missing} value(s) not extracted: the spec does not reach them (no spec to read: first cut)", evidence: fieldNames)];
        }

        return [F("g", "review without a known cause (no learn attempt, no rule failure, no unmapped header)")];
    }

    /// <summary>
    /// The cause of one mismatched field from the spec that read it: a plain column mapping that read the wrong value is an
    /// alias or mapping mistake (a, data); a value read through a construct (line, under, part, components, records, a
    /// derived sum) or not reached by any column is a construct gap (b, design); no mapping while the run left headers
    /// unmapped is a glossary gap (a).
    /// </summary>
    public static (string Category, string Why) SpecCause(LossRun.Formats.CarrierFormat spec, string field, string reasonText)
    {
        var entries = spec.ColumnMap.Where(c => c.Target == field).ToList();
        var bare = field.StartsWith("claim.", StringComparison.Ordinal) ? field["claim.".Length..] : field;
        var derived = spec.DerivedRules.Any(r => r.Target == bare || r.Target == field);
        var recordShape = spec.TableLocator.Records is not null || spec.TableLocator.RowShape != "claims";
        if (entries.Count == 0 && !derived)
        {
            return reasonText.Contains("unmapped header", StringComparison.Ordinal)
                ? ("a", $"{field} has no column and the route left headers unmapped (glossary gap)")
                : ("b", $"{field} is not reached by any column or construct");
        }

        if (derived || recordShape || entries.Any(c => c.Line > 0 || c.Under is not null || c.Part is not null || c.PartPattern is not null || c.Components is { Count: > 0 }))
        {
            return ("b", $"{field} is read through a construct ({(derived ? "derived sum" : recordShape ? "record shape" : "line/under/part/components")})");
        }

        return ("a", $"{field} comes from a plain column mapping that reads the wrong value (alias or column-mapping mistake)");
    }

    /// <summary>Groups failures by (cause, key), ranks by document count (then design before data, then key), and attaches the action and tier.</summary>
    public static IReadOnlyList<TuneFix> Rank(IReadOnlyList<TuneFailure> failures) =>
        [.. failures.Where(f => f.Category != "f")
            .GroupBy(f => (f.Category, f.Key))
            .Select(g => (g.Key, Docs: g.Select(f => f.Doc).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                Evidence: g.SelectMany(f => f.Evidence).GroupBy(e => e, StringComparer.Ordinal).OrderByDescending(e => e.Count()).ThenBy(e => e.Key, StringComparer.Ordinal).Select(e => e.Key).Take(6).ToList()))
            .OrderByDescending(x => x.Docs.Count).ThenByDescending(x => Causes[x.Key.Category].Tier).ThenBy(x => x.Key.Category, StringComparer.Ordinal).ThenBy(x => x.Key.Key, StringComparer.Ordinal)
            .Select((x, i) => new TuneFix(i + 1, x.Key.Category, Causes[x.Key.Category].Tier, x.Key.Key, x.Docs.Count, x.Docs, x.Evidence, Causes[x.Key.Category].Action))];

    public static TuneVerdict Verdict(int batch, IReadOnlyList<StratumAccuracy> strata, Proportion reviewRate, decimal cost, decimal budget, int tier2, TuneBatch? previous,
        TuningConfig config)
    {
        var reasons = new List<string>();
        if (cost > budget)
        {
            return new TuneVerdict("stop: budget", [$"batch cost {cost:0.00} USD is above the {budget:0.00} USD cap; the assistant does not raise a cap (tuning-loop.md §5)"]);
        }

        if (previous is not null && tier2 > 0 && tier2 >= previous.Tier2Changes && batch >= 2)
        {
            return new TuneVerdict("stop: not converging",
                [$"grammar changes {tier2} in batch {batch} ≥ {previous.Tier2Changes} in batch {previous.Batch}: fall back to the delta path for the carrier classes in the fix list (tuning-loop.md §6)"]);
        }

        var blocking = strata.Where(s => s.Status is "not met" or "review rate above cap").ToList();
        reasons.AddRange(blocking.Select(s => s.Status == "not met"
            ? $"{s.Stratum}: {(s.Measure == "prefill" ? "pre-fill" : "delivered")} key-field accuracy lower bound {s.KeyFieldAccuracy.Lower:P1} < target {s.Target:P0}"
            : $"{s.Stratum}: review rate {s.ReviewRate!.Value:P0} > its cap {s.MaxReviewRate:P0}"));
        reasons.AddRange(strata.Where(s => s.Status is "insufficient sample" or "no truth").Select(s => $"{s.Stratum}: {s.Status} ({s.Docs} document(s)), not blocking"));
        var reviewOk = reviewRate.Value <= config.MaxReviewRate;
        if (!reviewOk)
        {
            reasons.Add($"review rate {reviewRate.Value:P0} > {config.MaxReviewRate:P0}");
        }

        var tier2Ok = !config.RequireZeroTier2InLastBatch || (previous is not null && tier2 == 0);
        if (!tier2Ok)
        {
            reasons.Add(previous is null ? "first batch: zero design changes can only be shown from the second batch" : $"{tier2} grammar change(s) since batch {previous.Batch}");
        }

        if (blocking.Count == 0 && reviewOk && tier2Ok)
        {
            return new TuneVerdict("met", ["every stratum with enough documents meets its target, the review rate is within the cap, and the last batch made no design change", .. reasons]);
        }

        return batch >= config.MaxBatches
            ? new TuneVerdict("stop: max batches", [$"batch {batch} of {config.MaxBatches}", .. reasons])
            : new TuneVerdict("continue", reasons);
    }

    // ------------------------------------------------------------------ git and files

    /// <summary>Files under the grammar paths changed since the previous batch's commit (committed, plus uncommitted edits).</summary>
    public static GrammarChanges GrammarSince(TuningConfig config, string? since, string until)
    {
        var paths = GrammarPathspecs(config);
        if (since is null || paths.Count == 0)
        {
            return new GrammarChanges(since, until, [], 0);
        }

        var spec = string.Join(' ', paths.Select(p => $"\"{p}\""));
        var committed = Git($"diff --name-only {since}..{until} -- {spec}").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var uncommitted = Git($"diff --name-only HEAD -- {spec}").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var commits = Git($"rev-list --count {since}..{until} -- {spec}").Trim();
        // Brief 27 Answers 3: labelled paths count only in commits whose message carries the tier-2 label.
        var labelled = new List<string>();
        var labelledCommits = 0;
        foreach (var path in config.LabelledGrammarPaths)
        {
            var log = Git($"log --format=%H%x1f%B%x1e {since}..{until} -- \"{path}\"").Split('\u001e', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var tagged = log.Where(c => c.Contains("tier-2", StringComparison.OrdinalIgnoreCase)).ToList();
            if (tagged.Count > 0)
            {
                labelled.Add(path);
                labelledCommits += tagged.Count;
            }
        }

        return new GrammarChanges(since, until, [.. committed.Concat(uncommitted).Concat(labelled).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            (int.TryParse(commits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0) + labelledCommits);
    }

    /// <summary>The config's grammar paths; <c>@file</c> includes a path list (the prompt-gate artefact's).</summary>
    public static IReadOnlyList<string> GrammarPathspecs(TuningConfig config)
    {
        var list = new List<string>();
        foreach (var p in config.GrammarPaths)
        {
            if (p.StartsWith('@') && FindFile(p[1..]) is { } file)
            {
                list.AddRange(File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')));
            }
            else if (!p.StartsWith('@'))
            {
                list.Add(p);
            }
        }

        return [.. list.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>A file found in the current directory or a parent (the repository's config).</summary>
    public static string? FindFile(string relative)
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, relative)))
            {
                return Path.Combine(dir.FullName, relative);
            }
        }

        return null;
    }

    /// <summary>
    /// The normalized policy numbers the tuning documents print: those the run extracted from them, plus those their text
    /// layer prints before OCR (<see cref="PolicyReader"/>, member by member for a package), so a policy the extractor
    /// missed still keeps its archive claims. Reads only the tuning entries of the corpus.
    /// </summary>
    public static HashSet<string> TuningPolicies(string corpus, IReadOnlyCollection<string> tuningIds, string resultsDir)
    {
        var policies = new HashSet<string>(StringComparer.Ordinal);
        var docs = Path.Combine(resultsDir, "docs");
        foreach (var id in tuningIds)
        {
            var path = Path.Combine(docs, id + ".json");
            if (File.Exists(path) && JsonSerializer.Deserialize<RunResult>(File.ReadAllText(path), CanonicalJson.Options) is { } run)
            {
                policies.UnionWith(run.Documents.Where(d => d.Document is not null).SelectMany(d => d.Document!.PolicyPeriods).Select(p => PolicyKey(p.PolicyNumber))
                    .Where(k => k.Length > 0));
            }
        }

        var passwords = (Environment.GetEnvironmentVariable("LOSSRUN_PASSWORDS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var entry in CorpusManifest.Load(corpus).Entries.Where(e => tuningIds.Contains(e.Id)))
        {
            var file = CorpusManifest.DocumentPath(corpus, entry.File);
            if (!File.Exists(file))
            {
                continue;
            }

            var bytes = File.ReadAllBytes(file);
            foreach (var (name, member) in entry.InputType == InputTypes.Zip ? Import.Unpack(bytes) : [(entry.File, bytes)])
            {
                policies.UnionWith(PolicyReader.Read(member, name, passwords).Policies.Select(p => PolicyKey(p.Number)).Where(k => k.Length > 0));
            }
        }

        return policies;
    }

    /// <summary>The archive rows whose policy is one of <paramref name="policies"/> (normalized; see <see cref="PolicyKey"/>).</summary>
    public static List<KeyedClaim> FilterArchive(IReadOnlyList<KeyedClaim> archive, IReadOnlySet<string> policies) =>
        [.. archive.Where(k => policies.Contains(PolicyKey(k.PolicyNumber)))];

    /// <summary>A policy number as the alignment key compares it.</summary>
    public static string PolicyKey(string? policy) => CanonicalComparer.NormalizeText(policy);

    public static bool IsBlindPath(string path) =>
        Path.GetFullPath(path).Replace('\\', '/').Split('/').Any(s => s.Equals("blind", StringComparison.OrdinalIgnoreCase));

    public static int BatchNumber(string outDir) =>
        int.TryParse(new string(Path.GetFileName(Path.GetFullPath(outDir).TrimEnd('/', '\\')).Reverse().TakeWhile(char.IsDigit).Reverse().ToArray()), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var n) ? n : 1;

    /// <summary>The sibling batch folder with the highest number below this one that has a batch.json.</summary>
    public static TuneBatch? PreviousBatch(string outDir)
    {
        var full = Path.GetFullPath(outDir).TrimEnd('/', '\\');
        var parent = Path.GetDirectoryName(full);
        var current = BatchNumber(full);
        if (parent is null || !Directory.Exists(parent))
        {
            return null;
        }

        return Directory.EnumerateDirectories(parent).Select(d => (Dir: d, N: BatchNumber(d))).Where(x => x.N < current && File.Exists(Path.Combine(x.Dir, "batch.json")))
            .OrderByDescending(x => x.N).Select(x => JsonSerializer.Deserialize<TuneBatch>(File.ReadAllText(Path.Combine(x.Dir, "batch.json")), CanonicalJson.Options)).FirstOrDefault();
    }

    /// <summary>Specs by ref: the batch's learned specs (published and best attempts) over the checked-in hand specs.</summary>
    public static Func<string?, LossRun.Formats.CarrierFormat?> SpecResolver(string resultsDir)
    {
        var specs = new Dictionary<string, LossRun.Formats.CarrierFormat>(StringComparer.Ordinal);
        void Add(string file)
        {
            try
            {
                var spec = LossRun.Formats.FormatJson.Parse(File.ReadAllText(file));
                specs.TryAdd(spec.Ref, spec);
            }
            catch (JsonException)
            {
                // a best attempt that does not parse has no column map to read
            }
        }

        foreach (var f in Directory.Exists(Path.Combine(resultsDir, "specs")) ? Directory.EnumerateFiles(Path.Combine(resultsDir, "specs"), "*.json").Order(StringComparer.Ordinal) : Enumerable.Empty<string>())
        {
            Add(f);
        }

        if (EvalRunner.FindUp("src/TestShared/formats") is { } hand)
        {
            foreach (var f in Directory.EnumerateFiles(hand, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                Add(f);
            }
        }

        return formatRef => formatRef is not null && specs.TryGetValue(formatRef, out var s) ? s : null;
    }

    private static IReadOnlyList<TuningItem> LoadTuningItems(string dir) =>
        !Directory.Exists(dir) ? []
        : [.. Directory.EnumerateFiles(dir, "*.json").Select(f => JsonSerializer.Deserialize<TuningItem>(File.ReadAllText(f), CanonicalJson.Options)!).OrderBy(i => i.CreatedAt)];

    private static string Git(string arguments)
    {
        var start = new ProcessStartInfo("git", arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output : "";
    }

    // ------------------------------------------------------------------ reports

    public static string Markdown(TuneBatch b, TuningConfig config, TuneBatch? previous)
    {
        var md = new StringBuilder();
        string P(Proportion p) => p.N == 0 ? "—" : $"{p.Value:P1} [{p.Lower:P1}, {p.Upper:P1}] (n={p.N})";
        md.AppendLine($"# Tuning batch {b.Batch}").AppendLine();
        md.AppendLine($"Generated by `lossrun tune` ({b.Mode}) at commit `{b.Sha[..Math.Min(12, b.Sha.Length)]}` on `{b.Corpus}`, tuning documents only. " +
            $"Stopping criteria: `config/tuning-config.json` {b.ConfigVersion}. Tuning numbers show what changed, never accuracy (tuning-loop.md §1). " +
            "Names categories, rule ids, field names and counts only (§7).").AppendLine();
        md.AppendLine($"**Verdict: {b.Verdict.Status}.**").AppendLine();
        foreach (var r in b.Verdict.Reasons)
        {
            md.AppendLine($"- {r}");
        }

        md.AppendLine().AppendLine("## Routes").AppendLine().AppendLine("| Route | Documents |").AppendLine("|---|---|");
        foreach (var (route, n) in b.Routes)
        {
            md.AppendLine($"| {route} | {n} |");
        }

        if (b.NotRun > 0)
        {
            md.AppendLine($"| not run (stub: no cached Content Understanding) | {b.NotRun} |");
        }

        md.AppendLine().AppendLine("## Key-field accuracy by stratum (95 % CI, clustered by document)").AppendLine()
            .AppendLine("Targets are the acceptance table's, on the lower bound; fax is measured on the reviewer's pre-fill (routed to review by design).").AppendLine()
            .AppendLine("| Stratum | Documents | Measure | Key-field accuracy | Target (lower bound) | Review rate (cap) | Status |").AppendLine("|---|---|---|---|---|---|---|");
        foreach (var s in b.Strata)
        {
            md.AppendLine($"| {s.Stratum} | {s.Docs} | {s.Measure} | {P(s.KeyFieldAccuracy)} | {s.Target:P0} | {(s.ReviewRate is { } r ? $"{r.Value:P0}" : "—")}{(s.MaxReviewRate is { } c ? $" (≤ {c:P0})" : "")} | {s.Status} |");
        }

        md.AppendLine().AppendLine("## Review rate, model calls, cost").AppendLine()
            .AppendLine("| Measure | This batch | Previous batch |").AppendLine("|---|---|---|")
            .AppendLine($"| Review rate | {P(b.ReviewRate)} | {(previous is null ? "—" : P(previous.ReviewRate))} |")
            .AppendLine($"| Model calls per document | {b.ModelCallsPerDocument:0.###} | {(previous is null ? "—" : previous.ModelCallsPerDocument.ToString("0.###", CultureInfo.InvariantCulture))} |")
            .AppendLine($"| Cost (metered) | {b.CostUsd:0.0000} USD of a {(b.BudgetUsd ?? config.BatchBudgetUsd):0.00} USD cap ({(b.BudgetUsd is null ? config.BudgetFormula : "--budget")}) | {(previous is null ? "—" : $"{previous.CostUsd:0.0000} USD")} |");

        md.AppendLine().AppendLine("## Grammar changes since the last batch").AppendLine();
        if (b.Grammar.Since is null)
        {
            md.AppendLine("First batch: the baseline. From the next batch on, this counts the files changed under the grammar paths (the learn-prompt artefact paths, the spec schema, the canonical schema, the rule catalog).");
        }
        else
        {
            md.AppendLine($"{b.Grammar.Files.Count} file(s) in {b.Grammar.Commits} commit(s) since `{b.Grammar.Since[..Math.Min(12, b.Grammar.Since.Length)]}` (design changes, tier 2):");
            foreach (var f in b.Grammar.Files)
            {
                md.AppendLine($"- `{f}`");
            }
        }

        md.AppendLine().AppendLine("## Failures by cause").AppendLine().AppendLine("| Cause | Tier | Documents |").AppendLine("|---|---|---|");
        foreach (var g in b.Failures.GroupBy(f => f.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            md.AppendLine($"| ({g.Key}) {Causes[g.Key].Name} | {Causes[g.Key].Tier} | {g.Select(f => f.Doc).Distinct().Count()} |");
        }

        md.AppendLine().AppendLine("## Ranked fix list").AppendLine()
            .AppendLine("Tier 1 = data or bug fix (the assistant may make it, with tests and the regression gate). Tier 2 = design change (ADR, pull request, a human merges; a learn-prompt change carries its corpus v5 run).").AppendLine()
            .AppendLine("| # | Cause | Tier | Format, carrier or rule | Documents | Evidence | Action |").AppendLine("|---|---|---|---|---|---|---|");
        foreach (var f in b.Fixes)
        {
            md.AppendLine($"| {f.Rank} | ({f.Category}) {Causes[f.Category].Name} | {f.Tier} | {f.Key} | {f.Documents} | {string.Join("; ", f.Evidence)} | {f.Action} |");
        }

        if (b.Fixes.Count == 0)
        {
            md.AppendLine("| — | no failures | | | | | |");
        }

        md.AppendLine().AppendLine("## Per document").AppendLine().AppendLine("| Document | Stratum | Cause | Why |").AppendLine("|---|---|---|---|");
        foreach (var f in b.Failures.OrderBy(f => f.Doc, StringComparer.Ordinal))
        {
            md.AppendLine($"| {f.Doc} | {f.Stratum} | ({f.Category}) | {f.Why} |");
        }

        return md.ToString();
    }

    public static string PullRequestBody(TuneBatch b, TuneFix fix)
    {
        var md = new StringBuilder();
        md.AppendLine($"## Tier 2 (design change): ({fix.Category}) {Causes[fix.Category].Name} — {fix.Key}").AppendLine();
        md.AppendLine("**Label: `tier-2`.** Keep `[tier-2]` in the merge commit message: the next batch counts it as a grammar change (tuning-loop.md §6).").AppendLine();
        md.AppendLine($"**Found by:** tuning batch {b.Batch} (`lossrun tune`, commit `{b.Sha[..Math.Min(12, b.Sha.Length)]}`), fix-list rank {fix.Rank}.").AppendLine();
        md.AppendLine($"**What it would clear:** {fix.Documents} tuning document(s): {string.Join(", ", fix.Docs.Take(10))}{(fix.Docs.Count > 10 ? ", …" : "")}.").AppendLine();
        md.AppendLine($"**Evidence (rule ids, scrubbed reasons):** {string.Join("; ", fix.Evidence)}.").AppendLine();
        md.AppendLine($"**Proposed change:** {fix.Action}").AppendLine();
        md.AppendLine("**Checklist (tuning-loop.md §3–4):**");
        md.AppendLine("- [ ] ADR `docs/decisions/NNNN-title.md` (context, decision, consequences)");
        md.AppendLine("- [ ] Deck table reproduces on corpus v5 (`eval generate --v5`, `eval run`, `eval score`); results folder attached");
        md.AppendLine("- [ ] `tools/generic-guard.sh` and the sample guard pass; .NET and web tests pass");
        md.AppendLine("- [ ] A learn-prompt change: `python tools/prompt-gate.py --write` and the corpus v5 run whose run.json records the artefact");
        md.AppendLine("- [ ] The batch report attached; spend reported from report.md");
        md.AppendLine().AppendLine("The assistant opened this pull request and stops here: a human reviews and merges design changes.");
        return md.ToString();
    }
}
