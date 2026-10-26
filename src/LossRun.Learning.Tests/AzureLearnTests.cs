using System.Diagnostics;
using System.Text.Json;
using LossRun.Contracts;
using LossRun.Formats;
using LossRun.Ingestion;
using LossRun.Ingestion.Metering;
using LossRun.Learning;
using LossRun.Rules;
using Xunit.Abstractions;

namespace LossRun.Learning.Tests;

/// <summary>Runs only when LOSSRUN_AZURE_TESTS=1: learn/delta on the AI resource (keyless; LOSSRUN_AOAI_ENDPOINT) over recorded CU graphs.</summary>
public sealed class AzureFactAttribute : FactAttribute
{
    public AzureFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("LOSSRUN_AZURE_TESTS") != "1")
        {
            Skip = "Azure integration test: set LOSSRUN_AZURE_TESTS=1.";
        }
    }
}

/// <summary>
/// Brief-06 measurements. Graphs come from the recorded Content Understanding responses (the CU stage was measured in
/// brief 04), so wall time here is fingerprint + model + replay + verification. Every scenario appends one JSON line to
/// learn-measurements.jsonl (LOSSRUN_MEASUREMENTS_DIR) for the report.
/// </summary>
[Trait("Azure", "true")]
public class AzureLearnTests(ITestOutputHelper output)
{
    private static Uri Endpoint => Settings.RequiredUri("LOSSRUN_AOAI_ENDPOINT", "the Azure OpenAI endpoint: terraform output ai_endpoint");

    private sealed record Harness(InMemoryCostMeter Meter, IFormatRegistry Registry, FormatLearner Learner, LearningPipeline Pipeline, LearnOptions Options,
        AzureOpenAiLearnModel Model);

    private static async Task<Harness> BuildAsync(bool handSpecs = true, bool image = false)
    {
        var options = LearnOptions.FromConfiguration() with { IncludePageImage = image };
        var meter = new InMemoryCostMeter();
        var registry = handSpecs ? await Graphs.RegistryAsync() : new InMemoryFormatRegistry();
        var embeddings = new AzureOpenAiEmbeddingService(Endpoint, AzureCredentials.Default(), options.EmbeddingModel!, meter, options.Region, options.DeploymentType);
        var model = new AzureOpenAiLearnModel(Endpoint, AzureCredentials.Default(), meter, options.DeploymentType, options.Region);
        var learner = new FormatLearner(model, registry, options, embeddings);
        return new Harness(meter, registry, learner, new LearningPipeline(registry, embeddings, learner, new FormatRepairer(model, learner, options)), options, model);
    }

    private void Record(string scenario, LearningResult? result, LearnOutcome? outcome, TimeSpan wall, InMemoryCostMeter meter, string documentId, object? extra = null)
    {
        outcome ??= result?.Learn;
        var events = meter.Events.Where(e => e.DocumentId == documentId).ToList();
        var cost = CostCalculator.Compute(documentId, "azure", "learn", events, PriceTable.Embedded);
        var replay = outcome?.Verification?.Replay ?? result?.Replay;
        var measurement = new
        {
            scenario,
            routing = result?.Decision.Tier.ToString(),
            finalTier = result?.FinalTier ?? (outcome?.Published == true ? "Learned" : "Review"),
            published = outcome?.Published == true ? outcome.Spec!.Ref : null,
            createdBy = outcome?.Spec?.CreatedBy,
            iterations = outcome?.Attempts.Select(a => $"{a.Stage}#{a.Iteration}").ToList(),
            modelCalls = outcome?.ModelCalls ?? 0,
            inputTokens = outcome?.InputTokens ?? 0,
            cachedInputTokens = outcome?.CachedInputTokens ?? 0,
            outputTokens = outcome?.OutputTokens ?? 0,
            reasoningTokens = outcome?.ReasoningTokens ?? 0,
            perCall = outcome?.Attempts.Select(a => new
            {
                a.Stage, a.Iteration, a.Response.InputTokens, a.Response.CachedInputTokens, a.Response.OutputTokens, latencyMs = Math.Round(a.Response.Latency.TotalMilliseconds),
                verification = a.Verification?.Summary ?? a.Error,
            }).ToList(),
            embeddingCalls = cost.EmbeddingCalls,
            modelCostUsd = cost.CostUsd,
            wallMs = Math.Round(wall.TotalMilliseconds),
            rules = replay is null ? null : new
            {
                pass = replay.RuleOutcomes.Count(o => o.Status == RuleStatus.Pass),
                hardFail = replay.RuleOutcomes.Count(o => o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Hard),
                softFail = replay.RuleOutcomes.Count(o => o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Soft),
                flags = replay.RuleOutcomes.Count(o => o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Flag),
                notApplicable = replay.RuleOutcomes.Count(o => o.Status == RuleStatus.NotApplicable),
                notInformative = replay.RuleOutcomes.Count(o => o.Status == RuleStatus.NotInformative),
                claims = replay.Stats.ClaimRows,
            },
            extra,
        };
        var line = JsonSerializer.Serialize(measurement);
        output.WriteLine(line);
        File.AppendAllText(Path.Combine(Environment.GetEnvironmentVariable("LOSSRUN_MEASUREMENTS_DIR") ?? AppContext.BaseDirectory, "learn-measurements.jsonl"),
            line + Environment.NewLine);
    }

    [AzureFact]
    public async Task Second_carrier_is_learned_then_replayed_with_zero_model_calls()
    {
        var h = await BuildAsync();
        var graph = Graphs.Second();

        var stopwatch = Stopwatch.StartNew();
        var learned = await h.Pipeline.ProcessAsync(graph, "azure-learn", DateTimeOffset.UtcNow);
        stopwatch.Stop();
        Record("learn second carrier (native)", learned, null, stopwatch.Elapsed, h.Meter, graph.DocumentId,
            new { spec = learned.Learn?.Spec is { } s ? JsonSerializer.Deserialize<JsonElement>(FormatJson.Write(s)) : default });
        Assert.Equal(("Learned", RoutingTier.Learn), (learned.FinalTier, learned.Decision.Tier));

        var calls = h.Meter.Events.Count(e => e.Stage == "learn");
        stopwatch.Restart();
        var again = await h.Pipeline.ProcessAsync(Graphs.Second(), "azure-replay", DateTimeOffset.UtcNow);
        stopwatch.Stop();
        Record("replay second carrier (2nd run)", again, null, stopwatch.Elapsed, new InMemoryCostMeter(), graph.DocumentId,
            new { fingerprint = again.Match.Tier, badge = again.Decision.Badge, again.Decision.EmbeddingCalls });
        Assert.Equal(RoutingTier.ReplayAccepted, again.Decision.Tier);
        Assert.Equal(calls, h.Meter.Events.Count(e => e.Stage == "learn")); // zero LLM calls on the second document
        Assert.Equal(0, again.Replay!.ModelCalls);
    }

    [AzureFact]
    public async Task Scanned_rendering_is_learned_from_scratch()
    {
        var h = await BuildAsync();
        var graph = Graphs.SecondScanned();
        var stopwatch = Stopwatch.StartNew();
        var learned = await h.Pipeline.ProcessAsync(graph, "azure-learn-scan", DateTimeOffset.UtcNow);
        stopwatch.Stop();
        Record("learn second carrier (scanned)", learned, null, stopwatch.Elapsed, h.Meter, graph.DocumentId,
            new { meanOcrConfidence = FormatRouter.MeanOcrConfidence(graph), tables = graph.Tables.Count });
        Assert.Equal("Learned", learned.FinalTier);

        // The native rendering now matches the format learned from the scan (embedding tier), or misses; either way no wrong carrier.
        var native = await h.Pipeline.ProcessAsync(Graphs.Second(), "azure-cross", DateTimeOffset.UtcNow);
        Record("native after scanned-learn", native, null, TimeSpan.Zero, new InMemoryCostMeter(), "none",
            new { fingerprint = native.Match.Tier, native.Match.Score, native.Match.IdentityScores, formatRef = native.Decision.FormatRef });
    }

    [AzureFact]
    public async Task Page_image_with_the_learn_request()
    {
        var h = await BuildAsync(image: true);
        var graph = Graphs.Second();
        var stopwatch = Stopwatch.StartNew();
        var outcome = await h.Learner.LearnAsync(new LearnRequest(graph, "azure-image", SourcePdf: File.ReadAllBytes(Graphs.File("fixtures", "lossrun2-native.pdf"))));
        stopwatch.Stop();
        Record("learn second carrier (native) + page image", null, outcome, stopwatch.Elapsed, h.Meter, graph.DocumentId);
        Assert.True(outcome.Published, string.Join(" | ", outcome.Reasons));
    }

    [AzureFact]
    public async Task Repeated_learn_requests_hit_the_prompt_cache()
    {
        var h = await BuildAsync();
        var first = await h.Learner.LearnAsync(new LearnRequest(Graphs.Second(), "azure-cache-1"));
        var h2 = await BuildAsync();
        var stopwatch = Stopwatch.StartNew();
        var second = await h2.Learner.LearnAsync(new LearnRequest(Graphs.Second(), "azure-cache-2"));
        stopwatch.Stop();
        Record("prompt cache: same learn request repeated", null, second, stopwatch.Elapsed, h2.Meter, Graphs.Second().DocumentId,
            new { firstCallCached = first.Attempts[0].Response.CachedInputTokens, firstCallInput = first.Attempts[0].Response.InputTokens });
        Assert.True(second.Attempts[0].Response.CachedInputTokens > 0, "no cached tokens on a repeated request");
    }

    [AzureFact]
    public async Task Drifted_layout_is_delta_repaired()
    {
        var h = await BuildAsync();
        var graph = Graphs.Drifted();
        var stopwatch = Stopwatch.StartNew();
        var result = await h.Pipeline.ProcessAsync(graph, "azure-delta", DateTimeOffset.UtcNow);
        stopwatch.Stop();
        Record("delta repair drifted v2", result, null, stopwatch.Elapsed, h.Meter, graph.DocumentId,
            new { unmapped = result.Learn?.Attempts.FirstOrDefault()?.Spec?.ColumnMap.Where(c => c.Source is "Date Rptd" or "Lit").Select(c => $"{c.Source}→{c.Target}").ToList() });
        Assert.Equal(RoutingTier.ReplayWithDelta, result.Decision.Tier);
        Assert.True(result.Learn!.Published, string.Join(" | ", result.Learn.Reasons));
        Assert.Equal("synthetic-mutual-pdf@1", result.Learn.Spec!.Supersedes);

        var again = await h.Pipeline.ProcessAsync(Graphs.Drifted(), "azure-delta-2", DateTimeOffset.UtcNow);
        Assert.Equal(RoutingTier.ReplayAccepted, again.Decision.Tier);
    }

    [AzureFact]
    public async Task Identity_guard_native_vs_second_carrier()
    {
        // Real embeddings: the second carrier against the Synthetic Mutual centroid (checked in, text-embedding-3-large).
        var h = await BuildAsync();
        var fingerprinter = new FormatFingerprinter(h.Registry, new AzureOpenAiEmbeddingService(Endpoint, AzureCredentials.Default(), h.Options.EmbeddingModel!,
            h.Meter, h.Options.Region, h.Options.DeploymentType));
        var second = await fingerprinter.MatchAsync(Graphs.SecondScanned(), "azure-guard"); // scans miss the deterministic tier
        var scanned = await fingerprinter.MatchAsync(Graphs.Scanned(), "azure-guard");
        var mismatch = await fingerprinter.MatchAsync(Graphs.Scanned(), "azure-guard", new SubmissionContext("Fictional Casualty Group"));

        var measurement = new
        {
            secondCarrierVsSyntheticMutual = new { second.Tier, cosine = second.Similarities.GetValueOrDefault("synthetic-mutual-pdf"), identity = second.IdentityScores?.GetValueOrDefault("synthetic-mutual-pdf"), format = second.Format?.Ref },
            syntheticScanVsSyntheticMutual = new { scanned.Tier, cosine = scanned.Similarities.GetValueOrDefault("synthetic-mutual-pdf"), identity = scanned.IdentityScores?.GetValueOrDefault("synthetic-mutual-pdf"), format = scanned.Format?.Ref },
            hintMismatch = new { mismatch.CarrierMismatch, routing = new FormatRouter().Decide(mismatch, null, null).Tier.ToString() },
        };
        var line = JsonSerializer.Serialize(new { scenario = "identity guard", measurement });
        output.WriteLine(line);
        File.AppendAllText(Path.Combine(Environment.GetEnvironmentVariable("LOSSRUN_MEASUREMENTS_DIR") ?? AppContext.BaseDirectory, "learn-measurements.jsonl"), line + Environment.NewLine);

        Assert.NotEqual("synthetic-mutual-pdf", second.Format?.FormatId);
        Assert.Equal("synthetic-mutual-pdf", scanned.Format?.FormatId);
        Assert.True(mismatch.CarrierMismatch);
    }

    [AzureFact]
    public async Task Second_opinion_ocr_on_the_scan_without_cu_tables()
    {
        var graph = Graphs.Scanned();
        var spec = Graphs.Spec("synthetic-mutual-pdf");
        var before = Formats.Replay.ReplayEngine.Execute(graph, spec);
        var meter = new InMemoryCostMeter();
        var ocr = new SecondOpinionOcr(Endpoint, AzureCredentials.Default(), meter, "eastus2", enabled: true);
        var pages = SecondOpinionOcr.PagesNeedingSecondOpinion(graph, null);

        var (added, latency, usage) = await ocr.MergePageAsync(graph, File.ReadAllBytes(Graphs.File("fixtures", "lossrun-scanned.pdf")), 1, "azure-di", CanonicalData.Glossary);
        var after = Formats.Replay.ReplayEngine.Execute(graph, spec);
        var cost = CostCalculator.Compute(graph.DocumentId, "azure-di", "di", usage, PriceTable.Embedded);
        var line = JsonSerializer.Serialize(new
        {
            scenario = "second-opinion OCR (DI prebuilt-layout) on lossrun-scanned p1",
            pagesFlagged = pages,
            tablesAdded = added,
            headers = graph.Tables.Where(t => t.Id.StartsWith("di", StringComparison.Ordinal)).Select(t => t.HeaderTexts).ToList(),
            latencyMs = Math.Round(latency.TotalMilliseconds),
            costUsd = cost.CostUsd,
            before = new { before.Stats.ClaimRows, before.Stats.LineFallbackRows, hard = before.RuleOutcomes.Count(o => o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Hard) },
            after = new { after.Stats.ClaimRows, after.Stats.TablesUsed, after.Stats.LineFallbackRows, hard = after.RuleOutcomes.Count(o => o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Hard) },
            diagnostics = new
            {
                graph.Unit,
                cuTables = graph.Tables.Where(t => !t.Id.StartsWith("di", StringComparison.Ordinal)).Select(t => new { t.Id, t.Page, t.HeaderTexts, top = t.Polygon?.Where((_, i) => i % 2 == 1).Min() }).ToList(),
                diTables = graph.Tables.Where(t => t.Id.StartsWith("di", StringComparison.Ordinal)).Select(t => new { t.Id, t.RowCount, top = t.Polygon?.Where((_, i) => i % 2 == 1).Min() }).ToList(),
                anchors = graph.Paragraphs.Where(p => p.Text.StartsWith("Policy ", StringComparison.Ordinal)).Select(p => new { p.Text, top = p.Polygon?.Where((_, i) => i % 2 == 1).Min() }).ToList(),
                failures = after.RuleOutcomes.Where(o => o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Hard).Select(o => o.RuleId + ": " + o.Message).Distinct().Take(6).ToList(),
                periods = after.Document.PolicyPeriods.Select(p => $"{p.PolicyNumber}: {string.Join(",", p.Claims.Select(c => c.ClaimNumber))}").ToList(),
            },
        });
        output.WriteLine(line);
        File.AppendAllText(Path.Combine(Environment.GetEnvironmentVariable("LOSSRUN_MEASUREMENTS_DIR") ?? AppContext.BaseDirectory, "learn-measurements.jsonl"), line + Environment.NewLine);
        Assert.True(added > 0);
    }
}
