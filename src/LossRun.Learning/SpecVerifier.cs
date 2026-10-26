using System.Globalization;
using System.Text;
using LossRun.Contracts;
using LossRun.Formats;
using LossRun.Formats.Replay;
using LossRun.Rules;

namespace LossRun.Learning;

/// <summary>Outcome of replaying a candidate spec on the document it was learned from (CACHE-ED2-style verify-before-store).</summary>
/// <param name="FileKind">pdf | spreadsheet (selects the gate).</param>
/// <param name="RequiredCoverage">Share of the learn gate's required fields the replay filled (0..1; docs/evaluation/learn-gate.json).</param>
/// <param name="CarrierFromContext">The submission names the carrier and it agrees with the replayed carrier.</param>
public sealed record Verification(
    ReplayResult Replay,
    IReadOnlyList<RuleOutcome> HardFailures,
    int SoftFailures,
    IReadOnlyList<string> Unmapped,
    IReadOnlyList<string> MissingRequired,
    IReadOnlyList<string> Issues,
    bool? TotalsTie,
    int TotalsChecks,
    string FileKind = "pdf",
    double RequiredCoverage = 1.0,
    bool CarrierFromContext = false,
    IReadOnlyList<string>? DocumentFacts = null)
{
    public const string PdfTotalsGate = "pdf-totals";
    public const string SpreadsheetTotalsGate = "spreadsheet-totals";
    public const string SpreadsheetRulesGate = "spreadsheet-rules";

    /// <summary>
    /// The gate this verification passes, or null — policy from docs/evaluation/learn-gate.json (ADR 0007, amended in
    /// the 06/06b reviews): always 0 hard failures, nothing unmapped, no issues (lint included) and the always-required
    /// fields on every claim; then <c>pdf-totals</c> / <c>spreadsheet-totals</c> (every required field present and a printed
    /// total reconciles) or, where the file kind allows it, <c>spreadsheet-rules</c> (no printed total contradicts the rows,
    /// coverage ≥ the kind's minimum, and the carrier comes from the submission).
    /// </summary>
    public string? Gate(double? minCoverage = null, LearnGate? gate = null)
    {
        gate ??= LearnGate.Default;
        var kind = gate.FileKinds[FileKind == "spreadsheet" ? "spreadsheet" : "pdf"];
        if (HardFailures.Count > 0 || Unmapped.Count > 0 || Issues.Count > 0 || MissingRequired.Any(gate.IsAlwaysRequired))
        {
            return null;
        }

        // Brief 17 Answers 2: a conditional component word read without its required column is published only when
        // printed totals confirm the reading.
        if (Replay.AmbiguousComposites.Count > 0 && TotalsTie != true)
        {
            return null;
        }

        if (MissingRequired.Count == 0 && TotalsTie == true)
        {
            return FileKind == "spreadsheet" ? SpreadsheetTotalsGate : PdfTotalsGate;
        }

        return !kind.RequirePrintedTotals && TotalsTie != false && RequiredCoverage >= (minCoverage ?? kind.MinCoverage) && (CarrierFromContext || !kind.RequireCarrierFromContext)
            ? SpreadsheetRulesGate
            : null;
    }

    public bool Clean(double? minCoverage = null) => Gate(minCoverage) is not null;

    /// <summary>Lower is better: ranks attempts so exhaustion hands Review the best one.</summary>
    public int Penalty => HardFailures.Count * 100 + MissingRequired.Count * 40 + Unmapped.Count * 20 + Issues.Count * 5 + (TotalsTie == true ? 0 : 50)
        + (Replay.Stats.ClaimRows == 0 && !LearnGate.IsSummaryOnly(Replay.Document) ? 500 : 0);

    /// <summary>One line for the spec's learning record and the CLI.</summary>
    public string Summary =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Replay.Stats.ClaimRows} claim rows; {HardFailures.Count} hard / {SoftFailures} soft failures; {Unmapped.Count} unmapped; {MissingRequired.Count} missing; {Issues.Count} issues; totals {(TotalsTie is null ? "n/a" : TotalsTie.Value ? "tie" : "do not tie")} ({TotalsChecks} checks); required coverage {RequiredCoverage:P0}; gate {Gate() ?? "none"}");

    /// <summary>Structured failure list sent back to the model.</summary>
    public string Feedback()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Verification failed — fix the spec and return the complete corrected spec");
        sb.AppendLine(Summary);
        sb.AppendLine(CultureInfo.InvariantCulture, $"Replay stats: tables used {Replay.Stats.TablesUsed}, claim rows {Replay.Stats.ClaimRows}, subtotal rows {Replay.Stats.SubtotalRows}, grand total rows {Replay.Stats.GrandTotalRows}, line-fallback rows {Replay.Stats.LineFallbackRows}, skipped rows {Replay.Stats.SkippedRows}");
        if (Replay.Stats.TablesUsed == 0 && Replay.Stats.LineFallbackRows == 0)
        {
            sb.AppendLine("- No table qualified: columnMap sources must be the printed header texts (headerMatchMin share must match).");
        }

        foreach (var h in Unmapped)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- unmapped header \"{h}\": map it or add it to ignoredHeaders");
        }

        foreach (var m in MissingRequired)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- missing: {m}");
        }

        foreach (var ambiguous in Replay.AmbiguousComposites)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {ambiguous}: map it to the total, or keep it only if printed totals reconcile");
        }

        foreach (var issue in Issues.Take(20))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- issue: {issue}");
        }

        foreach (var f in HardFailures.Take(20))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {f.RuleId} failed: {f.Message}{(f.Detail is null ? "" : $" [{f.Detail}]")}");
        }

        if (FileKind == "spreadsheet" && TotalsTie is null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"- No printed total was read. A spreadsheet without totals can still pass with ≥ 80% required-field coverage (now {RequiredCoverage:P0}) and the carrier from the submission ({(CarrierFromContext ? "present" : "absent")}).");
        }
        else if (TotalsTie != true)
        {
            sb.AppendLine(TotalsTie is null
                ? "- No printed subtotal or grand total was read: classify the total rows with tableLocator.rowKinds (regex over the row text shown in the document part)."
                : "- Printed totals do not reconcile to the claim rows (see R-10/R-12 above).");
        }

        var periods = Replay.Document.PolicyPeriods;
        sb.AppendLine(CultureInfo.InvariantCulture, $"Replay read {periods.Count} period(s): {string.Join("; ", periods.Select(p => $"{p.PolicyNumber ?? "?"} {p.EffectiveDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "?"}..{p.ExpirationDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "?"} ({p.Claims.Count} claims)"))}");
        foreach (var claim in periods.SelectMany(p => p.Claims).Take(4))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  sample claim: {System.Text.Json.JsonSerializer.Serialize(claim, CanonicalJson.Options)}");
        }

        return sb.ToString();
    }
}

public static class SpecVerifier
{
    /// <summary>
    /// Brief 22 Answers 6 (learn-gate <c>summaryOnly.everyAmountChecked</c>): the money amounts a summary-only replay read on its
    /// period rows or report totals that no passing printed-total check (R-26 for that component and measure) covers. A tie on
    /// incurred and the claim count let a spec publish two analysis columns read as recoveries; each such amount is an issue.
    /// </summary>
    internal static List<string> UncheckedAmounts(LossRunDocument document, IReadOnlyList<RuleOutcome> outcomes, IReadOnlyList<string> totalsRules)
    {
        var read = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var totals in document.PolicyPeriods.Select(p => p.Subtotals).Append(document.ReportTotals).OfType<Totals>())
        {
            var node = System.Text.Json.JsonSerializer.SerializeToNode(totals, CanonicalJson.Options)!.AsObject();
            foreach (var (name, value) in node)
            {
                if (value is System.Text.Json.Nodes.JsonObject measures)
                {
                    foreach (var (measure, amount) in measures)
                    {
                        if (amount is System.Text.Json.Nodes.JsonValue)
                        {
                            read.Add($"{name}.{measure}");
                        }
                    }
                }
                else if (value is System.Text.Json.Nodes.JsonValue && name.StartsWith("recover", StringComparison.Ordinal))
                {
                    read.Add(name);
                }
            }
        }

        var checkedAmounts = outcomes.Where(o => totalsRules.Contains(o.RuleId) && o.Status == RuleStatus.Pass && o.Bindings is { } b && b.ContainsKey("c") && b.ContainsKey("m"))
            .Select(o => $"{o.Bindings!["c"]}.{o.Bindings["m"]}").ToHashSet(StringComparer.Ordinal);
        return [.. read.Where(a => !checkedAmounts.Contains(a))
            .Select(a => $"gate: summary-only amount {a} is checked by no printed total ({string.Join("/", totalsRules)}); map an amount only when the run prints a grand total for it, else list its column in ignoredHeaders")];
    }

    public static Verification Verify(DocumentGraph graph, CarrierFormat spec, SubmissionContext? submission = null, Glossary? glossary = null)
    {
        var replay = ReplayEngine.Execute(graph, spec, glossary: glossary, submission: submission);
        var hard = replay.RuleOutcomes.Where(o => o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Hard).ToList();
        var soft = replay.RuleOutcomes.Count(o => o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Soft);

        var document = replay.Document;
        var gate = LearnGate.Default;
        var missing = gate.Missing(document).ToList();
        if (replay.Stats.ClaimRows == 0 && document.NoLossesStatement != true && !LearnGate.IsSummaryOnly(document))
        {
            missing.Insert(0, "claim rows (no claim row was read)");
        }

        var totalsRules = gate.TotalsRulesFor(document);
        var totals = replay.RuleOutcomes.Where(o => totalsRules.Contains(o.RuleId) && o.Status is RuleStatus.Pass or RuleStatus.Fail or RuleStatus.NotInformative).ToList();
        bool? tie = totals.Count == 0 ? null : totals.All(o => o.Status != RuleStatus.Fail);
        var carrierFromContext = submission?.CarrierHint is { Length: > 0 } hint
            && FormatFingerprinter.SameCarrier(hint, document.CarrierName ?? spec.CarrierName);
        List<string> uncheckedAmounts = gate.SummaryOnly.EveryAmountChecked && LearnGate.IsSummaryOnly(document) ? UncheckedAmounts(document, replay.RuleOutcomes, gate.SummaryOnly.TotalsRules) : [];
        return new Verification(replay, hard, soft, replay.UnmappedHeaders, missing, [.. replay.Issues, .. SpecLint.Check(spec, replay, graph, glossary), .. uncheckedAmounts], tie, totals.Count,
            spec.FileKind == "spreadsheet" ? "spreadsheet" : "pdf", gate.Coverage(document), carrierFromContext, DocumentFacts(graph, missing, glossary));
    }

    /// <summary>
    /// Brief 23: why a required document or period field is missing, when the reason is what the document prints rather than
    /// a gap in the spec: no valuation date anywhere in the header block; a losses window printed where a policy period would
    /// be. Review then states a document fact.
    /// </summary>
    internal static List<string> DocumentFacts(DocumentGraph graph, IReadOnlyList<string> missing, Glossary? glossary = null)
    {
        var facts = new List<string>();
        if (graph.Words.Count == 0)
        {
            return facts;
        }

        var header = graph.HeaderFields ?? HeaderFields.Detect(graph, glossary ?? CanonicalData.Glossary);
        if (missing.Contains("document.valuationDate") && !header.Any(h => h.Field == "document.valuationDate"))
        {
            facts.Add("document fact: the run prints no valuation (as-of) date");
        }

        if (missing.Any(m => m.EndsWith("effectiveDate", StringComparison.Ordinal) || m.EndsWith("expirationDate", StringComparison.Ordinal))
            && !header.Any(h => h.Field == "period.effectiveDate"))
        {
            facts.Add(header.FirstOrDefault(h => h.Field == HeaderFields.LossWindow) is { } window
                ? $"document fact: the run prints a losses window (\"{window.Label}\"), not a policy period"
                : "document fact: the run prints no policy period");
        }

        if (missing.Any(m => m.EndsWith("policyNumber", StringComparison.Ordinal)) && !header.Any(h => h.Field == "period.policyNumber"))
        {
            facts.Add("document fact: the run prints no policy number in its header");
        }

        return facts;
    }
}
