// Brief 30 offline replay re-score: every document a stored corpus run delivered is replayed again with the spec it used, on this
// source tree's code, and re-gated (router for replay hits, the learn gate's no-issues rule for learned/delta documents). No model,
// no CU, no embeddings: the run's stored graphs and published specs only. Writes a results folder for `lossrun eval score`.
using System.Reflection;
using System.Text.Json;
using LossRun.Contracts;
using LossRun.Evaluation;
using LossRun.Formats;
using LossRun.Formats.Replay;
using LossRun.Rules;

var (corpus, run, outDir, formats) = (args[0], args[1], args[2], args[3]);
var registry = new InMemoryFormatRegistry();
await FormatDirectory.LoadIntoAsync(formats, registry);
var learned = Directory.GetFiles(Path.Combine(run, "specs"), "*.published.json").Select(f => FormatJson.Parse(File.ReadAllText(f))).OrderBy(s => s.FormatId).ThenBy(s => s.Version);
foreach (var spec in learned)
{
    try { await registry.PublishAsync(spec); } catch (Exception e) { Console.Error.WriteLine($"publish {spec.Ref}: {e.GetType().Name}"); }
}

var carrierName = typeof(RunResult).Assembly.GetType("LossRun.Evaluation.EvalRunner")!.GetMethod("CarrierName", BindingFlags.NonPublic | BindingFlags.Static)!;
var carriers = JsonDocument.Parse(File.ReadAllText(Path.Combine(corpus, "manifest.json"))).RootElement.GetProperty("entries").EnumerateArray()
    .ToDictionary(e => e.GetProperty("id").GetString()!, e => e.GetProperty("carrier").GetString()!);
Directory.CreateDirectory(Path.Combine(outDir, "docs"));
foreach (var f in Directory.GetFiles(run, "*.json"))
{
    File.Copy(f, Path.Combine(outDir, Path.GetFileName(f)), true);
}

var router = new FormatRouter();
var (delivered, toReview, valuesChanged, missing, threw) = (0, 0, 0, 0, 0);
var lines = new List<string>();
foreach (var file in Directory.GetFiles(Path.Combine(run, "docs"), "*.json").Order(StringComparer.Ordinal))
{
    var r = JsonSerializer.Deserialize<RunResult>(File.ReadAllText(file), CanonicalJson.Options)!;
    foreach (var d in r.Documents.Where(d => d.FinalTier != "Review" && d.FormatRef is not null))
    {
        delivered++;
        var at = d.FormatRef!.LastIndexOf('@');
        var spec = await registry.GetAsync(d.FormatRef[..at], int.Parse(d.FormatRef[(at + 1)..]));
        var graphPath = Path.Combine(run, "graphs", d.GraphDocumentId + ".json");
        if (spec is null || !File.Exists(graphPath))
        {
            missing++;
            lines.Add($"{r.Id}\t{d.GraphDocumentId}\t{d.FinalTier}\tkept (no spec or graph)");
            continue;
        }

        var graph = JsonSerializer.Deserialize<DocumentGraph>(File.ReadAllText(graphPath), CanonicalJson.Options)!;
        var submission = new SubmissionContext((string?)carrierName.Invoke(null, [carriers[r.Id]]), $"EVAL-{r.Id}", "system");
        ReplayResult replay;
        try
        {
            replay = ReplayEngine.Execute(graph, spec, null, CanonicalData.Glossary, submission: submission);
        }
        catch (Exception e)
        {
            threw++;
            lines.Add($"{r.Id}\t{d.GraphDocumentId}\t{d.FinalTier}\tReview (replay threw {e.GetType().Name})");
            (d.FinalTier, d.PreFill, d.Document) = ("Review", d.Document, null);
            toReview++;
            continue;
        }

        string verdict;
        if (d.FinalTier == "ReplayAccepted")
        {
            var scanned = OcrHeaders.IsScanned(graph);
            var match = new FingerprintMatch(spec, d.FingerprintTier ?? "deterministic", 1.0, false, FingerprintText.Compute(graph), 0, new Dictionary<string, double>(), []);
            var decision = router.Decide(match, replay, FormatRouter.MeanOcrConfidence(graph), failures: OcrFailures.Classify(replay, scanned),
                narrative: graph.SourceFile.Kind == FileKind.Pdf && graph.Tables.Count == 0, scanDpi: scanned ? graph.SourceFile.ScanDpi : null);
            verdict = decision.Tier == RoutingTier.ReplayAccepted ? "accepted" : $"{decision.Tier}: {string.Join(" | ", decision.Reasons)}";
        }
        else
        {
            var hard = replay.RuleOutcomes.Count(o => o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Hard);
            verdict = replay.Issues.Count == 0 && hard == 0 ? "accepted" : $"Review: learn gate ({replay.Issues.Count} issue(s), {hard} hard): {replay.Issues.FirstOrDefault()}";
        }

        if (verdict != "accepted")
        {
            toReview++;
            lines.Add($"{r.Id}\t{d.GraphDocumentId}\t{d.FinalTier}\t{verdict}");
            (d.FinalTier, d.PreFill, d.Document) = ("Review", replay.Document, null);
            continue;
        }

        var before = JsonSerializer.Serialize(d.Document, CanonicalJson.Options);
        replay.Document.Lineage = d.Document?.Lineage is null ? null : replay.Document.Lineage;
        var after = JsonSerializer.Serialize(replay.Document, CanonicalJson.Options);
        if (before != after)
        {
            valuesChanged++;
        }

        d.Document = replay.Document;
    }

    File.WriteAllText(Path.Combine(outDir, "docs", Path.GetFileName(file)), JsonSerializer.Serialize(r, CanonicalJson.Options));
}

File.WriteAllLines(Path.Combine(outDir, "rescore.tsv"), lines);
Console.WriteLine($"delivered {delivered}: re-gated to Review {toReview} (replay threw {threw}); values changed on {valuesChanged} still delivered; kept as stored {missing}");
