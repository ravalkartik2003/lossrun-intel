using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LossRun.Api.Contracts;
using LossRun.Contracts;
using LossRun.Formats;
using LossRun.Formats.Replay;
using LossRun.Ingestion;
using LossRun.Ingestion.ContentUnderstanding;
using LossRun.Ingestion.Metering;
using LossRun.Learning;
using LossRun.Rules;

namespace LossRun.Evaluation;

/// <summary>One loss-run document the pipeline produced from a corpus file (a single document, or a bundle's loss-run segment).</summary>
public sealed class ExtractedDocument
{
    public string GraphDocumentId { get; set; } = "";
    public int? FirstPage { get; set; }
    public int? LastPage { get; set; }
    public string FinalTier { get; set; } = "";
    public string? RoutingTier { get; set; }
    public string? FingerprintTier { get; set; }
    public string? FormatRef { get; set; }
    public string? FormatStatus { get; set; }
    public bool ConfirmNewFormat { get; set; }
    public bool LearnPublished { get; set; }
    public int ModelCalls { get; set; }
    public List<string> Reasons { get; set; } = [];
    public ReplayStats? ReplayStats { get; set; }
    /// <summary>Delivered document (no human): null when the document went to Review.</summary>
    public LossRunDocument? Document { get; set; }

    /// <summary>What a reviewer starts from on a Review document: the replay of the matched/recovered format or the learn's best attempt (08 Answers 1).</summary>
    public LossRunDocument? PreFill { get; set; }
    public int HardFailures { get; set; }
    public int SoftFailures { get; set; }
    public List<string> HardFailureRules { get; set; } = [];
    public long ProcessMs { get; set; }

    /// <summary>Per model call: stage#iteration, tokens, verification summary or error (learn/delta only).</summary>
    public List<string> Attempts { get; set; } = [];
}

/// <summary>Everything the scorer and the studies need about one corpus file.</summary>
public sealed class RunResult
{
    public string Id { get; set; } = "";
    public string Mode { get; set; } = "local";
    public string? Error { get; set; }
    public long IngestMs { get; set; }
    public long TotalMs { get; set; }

    /// <summary>Time spent waiting for the run's serialized learn gate — a harness artifact, left out of latency (<see cref="LatencyMs"/>).</summary>
    public long QueueMs { get; set; }

    public long LatencyMs => TotalMs - QueueMs;
    public List<string> CuModes { get; set; } = [];
    public List<GraphSegment> Segments { get; set; } = [];
    public List<ClassifierDecision> ClassifierDecisions { get; set; } = [];
    public List<string> Unsupported { get; set; } = [];
    public List<ExtractedDocument> Documents { get; set; } = [];
    public List<UsageEvent> Usage { get; set; } = [];
    public decimal CostUsd { get; set; }
    public int LlmCalls { get; set; }
    public int EmbeddingCalls { get; set; }
}

public sealed record RunMetadata(string GitSha, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, string Mode, bool CuInline, string LearnModel, string? LearnModelVersion,
    string DeltaModel, string? DeltaModelVersion, string? EmbeddingModel, string PromptVersion, string LearnGateVersion, string PriceTableVersion, string CatalogVersion,
    string CorpusGeneratorVersion, int? CorpusSeed, int Documents, IReadOnlyList<FormatSummary> RegistryAtEnd, LearnedGlossary? LearnedAliasesAtEnd = null,
    IReadOnlyList<string>? GlossaryAblation = null, string? LearnPromptArtefact = null, IReadOnlyList<string>? SupersedesArtefacts = null);

/// <summary><c>lossrun eval run</c> (brief 08 §3).</summary>
public static class EvalRunner
{
    public static JsonSerializerOptions Json { get; } = new(CanonicalJson.Options) { WriteIndented = false };

    /// <param name="blind">Only these entry ids: the sealed blind set (<c>--only-blind seal-manifest.json</c>) or the tuning set (<c>--only-tuning</c>).</param>
    public static async Task<int> RunAsync(string corpusDir, string outDir, string mode, int concurrency, bool inline, int? limit, string[]? only, bool secondOpinion,
        IReadOnlyCollection<string>? blind = null, string? cuCache = null)
    {
        var manifest = CorpusManifest.Load(corpusDir);
        var blindSet = blind?.ToHashSet(StringComparer.Ordinal);
        var entries = manifest.Entries.Where(e => (only is null || only.Contains(e.Carrier)) && (blindSet is null || blindSet.Contains(e.Id))).Take(limit ?? int.MaxValue).ToList();
        Directory.CreateDirectory(Path.Combine(outDir, "docs"));
        Directory.CreateDirectory(Path.Combine(outDir, "graphs"));
        var started = DateTimeOffset.UtcNow;
        var done = entries.Where(e => File.Exists(Path.Combine(outDir, "docs", e.Id + ".json"))).Select(e => e.Id).ToHashSet();
        var pending = entries.Where(e => !done.Contains(e.Id)).ToList();
        Console.WriteLine($"eval run ({mode}): {pending.Count} to process, {done.Count} already done → {outDir}");

        IDocumentRunner runner = mode == "api" ? new ApiRunner() : new LocalRunner(inline, secondOpinion, cuCache, stub: mode == "stub");

        // Seeds first and one at a time: the first document of each carrier × input type, so a new format is learned
        // once (as an archive would present it) before its repeats replay concurrently.
        var seeds = pending.GroupBy(e => (e.Carrier, e.InputType)).Select(g => g.First()).ToList();
        var rest = pending.Except(seeds).ToList();
        var count = 0;
        async Task One(ManifestEntry entry)
        {
            var truth = CorpusManifest.TryTruth(corpusDir, entry.Id); // none for an imported archive: the runner does not need it
            RunResult result;
            try
            {
                result = await runner.RunAsync(corpusDir, entry, truth, outDir).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                result = new RunResult { Id = entry.Id, Mode = mode, Error = $"{e.GetType().Name}: {e.Message}" };
            }

            await File.WriteAllTextAsync(Path.Combine(outDir, "docs", entry.Id + ".json"), JsonSerializer.Serialize(result, Json)).ConfigureAwait(false);
            var n = Interlocked.Increment(ref count);
            var tiers = string.Join(",", result.Documents.Select(d => d.FinalTier));
            Console.WriteLine($"[{n}/{pending.Count}] {entry.Id}: {(result.Error ?? tiers)} · {result.LatencyMs / 1000.0:0.0}s · ${result.CostUsd:0.0000} · LLM {result.LlmCalls}");
        }

        foreach (var seed in seeds)
        {
            await One(seed).ConfigureAwait(false);
        }

        await Parallel.ForEachAsync(rest, new ParallelOptions { MaxDegreeOfParallelism = concurrency }, async (entry, _) => await One(entry).ConfigureAwait(false))
            .ConfigureAwait(false);

        var learn = runner.Options;
        var metadata = new RunMetadata(GitSha(), started, DateTimeOffset.UtcNow, mode, inline, learn.LearnDeployment, learn.LearnModelVersion, learn.DeltaDeployment,
            learn.DeltaModelVersion, learn.EmbeddingModel, learn.PromptVersion, LearnGate.Default.Version, PriceTable.Embedded.Version, RuleCatalog.Embedded.CatalogVersion,
            manifest.GeneratorVersion, manifest.Seed, entries.Count, await runner.RegistrySnapshotAsync().ConfigureAwait(false),
            await runner.LearnedAliasesAsync().ConfigureAwait(false), CanonicalData.GlossaryAblation is { Count: > 0 } ablated ? ablated : null,
            PromptArtefact.Compute(), // brief 20 Answers 3: the CI gate matches it against docs/evaluation/learn-prompt-artefact.json
            PromptArtefact.Supersedes()); // brief 31 Answers 3: the never-run artefacts this run also covers
        await File.WriteAllTextAsync(Path.Combine(outDir, "run.json"), JsonSerializer.Serialize(metadata, new JsonSerializerOptions(CanonicalJson.Options) { WriteIndented = true }))
            .ConfigureAwait(false);
        if (runner is LocalRunner { CuCache: { } cache })
        {
            Console.WriteLine(mode == "stub"
                ? $"CU cache: {cache.Hits} hit(s) (their pages meter at list price, so the report shows what the run would cost); {cache.Misses} miss(es): not run (stub)"
                : $"CU cache: {cache.Hits} hit(s), not paid for again (their pages still meter, so the report shows the run's full cost); {cache.Misses} miss(es), paid and stored");
        }

        return 0;
    }

    private static string GitSha()
    {
        try
        {
            static string Git(string arguments)
            {
                using var git = Process.Start(new ProcessStartInfo("git", arguments) { RedirectStandardOutput = true, UseShellExecute = false })!;
                var output = git.StandardOutput.ReadToEnd().Trim();
                git.WaitForExit();
                return output;
            }

            // A run from uncommitted code says so: the SHA alone would claim code that is not what ran.
            return Git("rev-parse HEAD") + (Git("status --porcelain --untracked-files=no -- src") is { Length: > 0 } ? "+dirty" : "");
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    internal static string? FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, relative)))
            {
                return Path.Combine(dir.FullName, relative);
            }
        }

        return null;
    }

    /// <summary>A synthetic carrier key (c1…c5) → its name; an imported archive's carrier is already a name (null when unknown).</summary>
    // The construct carriers too (brief 19: their key "c6" reached the router as the carrier name and every replay was a
    // carrier-mismatch review).
    internal static string? CarrierName(string key) => Carriers.All.Concat(Carriers.Constructs).FirstOrDefault(c => c.Key == key)?.Name ?? (key == "unknown" ? null : key);
}

internal interface IDocumentRunner
{
    LearnOptions Options { get; }

    Task<RunResult> RunAsync(string corpusDir, ManifestEntry entry, TruthRecord? truth, string outDir);

    Task<IReadOnlyList<FormatSummary>> RegistrySnapshotAsync();

    /// <summary>The learned glossary aliases at the end of the run (ADR 0019), with status and evidence.</summary>
    Task<LearnedGlossary?> LearnedAliasesAsync();
}

/// <summary>
/// In-process pipeline: real Content Understanding, embeddings and learn model (keyless), in-memory registry seeded with the hand specs.
/// Brief 27, <c>--mode stub</c>: no Azure at all — Content Understanding only from <c>--cu-cache</c> (a miss fails that document),
/// no embeddings (semantic tier off), and a learn model whose answers never verify (every learn ends in review). The playbook's
/// offline proof runs this way; spreadsheets and replays of the hand specs behave as in a real run.
/// </summary>
internal sealed class LocalRunner : IDocumentRunner
{
    private readonly IContentUnderstandingService _cu;
    private readonly CachingContentUnderstanding? _cache;
    private readonly IngestionOptions _ingestion;
    private readonly InMemoryFormatRegistry _registry = new();
    private FileExemplarStore? _exemplars;
    private readonly Uri _aiEndpoint;
    private readonly Azure.Core.TokenCredential _credential = AzureCredentials.Default();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly bool _secondOpinion;
    private readonly SemaphoreSlim _learnGate = new(1, 1);
    private readonly bool _stub;

    public LearnOptions Options { get; } = LearnOptions.FromConfiguration();

    public LocalRunner(bool inline, bool secondOpinion, string? cuCache = null, bool stub = false)
    {
        _stub = stub;
        var cuEndpoint = stub ? new Uri("https://stub.invalid/") : Settings.RequiredUri("LOSSRUN_CU_ENDPOINT", "the Content Understanding endpoint: terraform output content_understanding_endpoint");
        _aiEndpoint = stub ? new Uri("https://stub.invalid/") : Settings.RequiredUri("LOSSRUN_AOAI_ENDPOINT", "the Azure OpenAI endpoint: terraform output ai_endpoint");
        IContentUnderstandingService cu = stub ? new StubContentUnderstanding()
            : new ContentUnderstandingService(cuEndpoint, _credential, inline ? new InlineAnalyzeOptions(cuEndpoint, _credential) : null);
        _cache = cuCache is null ? null : new CachingContentUnderstanding(cu, cuCache);
        _cu = (IContentUnderstandingService?)_cache ?? cu;
        // Brief 34 A1.2: an evaluation measures real calls (cost, latency); a stored result is never reused here (the CU cache, --cu-cache, is its own opt-in).
        _ingestion = new IngestionOptions { ContentUnderstandingEndpoint = cuEndpoint, Passwords = [CorpusGenerator.Password, .. Passwords()], ForceReparse = true };
        _secondOpinion = secondOpinion;
        var formats = EvalRunner.FindUp("src/TestShared/formats") ?? throw new DirectoryNotFoundException("src/TestShared/formats");
        FormatDirectory.LoadIntoAsync(formats, _registry).GetAwaiter().GetResult();
    }

    /// <summary>PDF/workbook passwords for an imported archive (LOSSRUN_PASSWORDS, ';'-separated; populate from Key Vault, never commit).</summary>
    private static string[] Passwords() =>
        Environment.GetEnvironmentVariable("LOSSRUN_PASSWORDS") is { Length: > 0 } p ? p.Split(';', StringSplitOptions.RemoveEmptyEntries) : [];

    public async Task<RunResult> RunAsync(string corpusDir, ManifestEntry entry, TruthRecord? truth, string outDir)
    {
        var bytes = await File.ReadAllBytesAsync(CorpusManifest.DocumentPath(corpusDir, entry.File)).ConfigureAwait(false);
        var meter = new InMemoryCostMeter();
        var total = Stopwatch.StartNew();
        // ADR 0019: headers resolve with the active learned aliases as the run has them now (they grow as the run verifies them).
        var glossary = await _registry.GlossaryAsync().ConfigureAwait(false);
        var pipeline = new IngestionPipeline(_cu, meter, new InMemoryRawResultStore(), PriceTable.Embedded, glossary, _ingestion with { DocumentIdScope = entry.Id });
        var ingest = await pipeline.IngestAsync(Path.GetFileName(entry.File), bytes).ConfigureAwait(false);
        var ingestMs = total.ElapsedMilliseconds;

        IEmbeddingService? embeddings = _stub ? null
            : new AzureOpenAiEmbeddingService(_aiEndpoint, _credential, Options.EmbeddingModel!, meter, _ingestion.Region, _ingestion.ModelDeploymentType, _http);
        ILearnModel model = _stub ? new StubLearnModel() : new AzureOpenAiLearnModel(_aiEndpoint, _credential, meter, Options.DeploymentType, Options.Region, _http);
        var learner = new FormatLearner(model, _registry, Options, embeddings);
        var secondOpinion = _stub ? null : new SecondOpinionOcr(_aiEndpoint, _credential, meter, _ingestion.Region, _secondOpinion, _http);
        var learning = new LearningPipeline(_registry, embeddings, learner, new FormatRepairer(model, learner, Options), secondOpinion, aliasPolicy: Options.AliasPolicy,
            exemplars: _exemplars ??= new FileExemplarStore(Path.Combine(outDir, "registry", "exemplars"))); // brief 34 A2: the production repair gate
        var submission = new SubmissionContext(EvalRunner.CarrierName(entry.Carrier), $"EVAL-{entry.Id}", "system");

        var result = new RunResult
        {
            Id = entry.Id, Mode = _stub ? "stub" : "local", IngestMs = ingestMs, CuModes = [.. ingest.Calls.Select(c => $"{c.AnalyzerId}:{c.Mode}:{c.Latency.TotalMilliseconds:0}ms")],
            Segments = [.. ingest.Documents.SelectMany(d => d.Segments).DistinctBy(s => (s.StartPage, s.EndPage, s.Category))],
            ClassifierDecisions = [.. ingest.ClassifierDecisions], Unsupported = [.. ingest.Unsupported.Select(u => $"{u.FileName}: {u.Reason}")],
        };

        foreach (var graph in ingest.Documents.Where(d => d.Segments.Count == 0 || d.SourceFile.FirstPage is not null))
        {
            await File.WriteAllTextAsync(Path.Combine(outDir, "graphs", graph.DocumentId + ".json"), JsonSerializer.Serialize(graph, CanonicalJson.Options)).ConfigureAwait(false);
            var step = Stopwatch.StartNew();

            // Learning is serialized so two concurrent documents of a new format cannot both learn it (the orchestration has the
            // same property per format through its registry publish-once rule; here it keeps the cost numbers honest).
            var queued = System.Diagnostics.Stopwatch.StartNew();
            await _learnGate.WaitAsync().ConfigureAwait(false);
            result.QueueMs += queued.ElapsedMilliseconds;
            step.Restart();
            LearningResult learned;
            try
            {
                learned = await learning.ProcessAsync(graph, entry.Id, DateTimeOffset.UtcNow, submission, sourcePdf: graph.SourceFile.Kind == FileKind.Pdf ? bytes : null)
                    .ConfigureAwait(false);
            }
            finally
            {
                _learnGate.Release();
            }

            result.Documents.Add(Extracted(graph, learned, step.ElapsedMilliseconds));
            if ((learned.Learn?.Spec ?? learned.Learn?.Attempts.LastOrDefault(a => a.Spec is not null)?.Spec) is { } spec)
            {
                Directory.CreateDirectory(Path.Combine(outDir, "specs"));
                await File.WriteAllTextAsync(Path.Combine(outDir, "specs", $"{graph.DocumentId}.{(learned.Learn!.Published ? "published" : "best-attempt")}.json"),
                    FormatJson.Write(spec)).ConfigureAwait(false);
            }
        }

        total.Stop();
        result.TotalMs = total.ElapsedMilliseconds;
        result.Usage = [.. meter.Events];
        Cost(result);
        return result;
    }

    internal static ExtractedDocument Extracted(DocumentGraph graph, LearningResult learned, long ms)
    {
        var replay = learned.Replay;
        var outcomes = replay is null ? [] : RuleEvaluator.Default.Evaluate(replay.Document).Outcomes.ToList();
        var hard = outcomes.Where(o => o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Hard).ToList();
        return new ExtractedDocument
        {
            GraphDocumentId = graph.DocumentId, FirstPage = graph.SourceFile.FirstPage, LastPage = graph.SourceFile.LastPage, FinalTier = learned.FinalTier,
            RoutingTier = learned.Decision.Tier.ToString(), FingerprintTier = learned.Match.Tier, FormatRef = learned.Format?.Ref,
            FormatStatus = learned.Decision.FormatStatus, ConfirmNewFormat = learned.ConfirmNewFormat, LearnPublished = learned.Learn?.Published == true,
            ModelCalls = learned.Learn?.ModelCalls ?? 0, Reasons = [.. learned.Learn?.Reasons ?? learned.Decision.Reasons], ReplayStats = replay?.Stats,
            Document = learned.FinalTier == "Review" ? null : replay?.Document,
            PreFill = learned.FinalTier == "Review" ? replay?.Document : null,
            HardFailures = hard.Count, SoftFailures = outcomes.Count(o => o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Soft),
            HardFailureRules = [.. hard.Select(h => h.RuleId).Distinct()], ProcessMs = ms,
            Attempts = [.. learned.Learn?.Attempts.Select(a =>
                $"{a.Stage}#{a.Iteration} in {a.Response.InputTokens}/{a.Response.CachedInputTokens}c out {a.Response.OutputTokens}: {a.Verification?.Summary ?? a.Error}") ?? []],
        };
    }

    internal static void Cost(RunResult result)
    {
        var events = result.Usage.Select(e => e with { DocumentId = result.Id }).ToList();
        var cost = CostCalculator.Compute(result.Id, "eval", "eval", events, PriceTable.Embedded);
        (result.CostUsd, result.LlmCalls, result.EmbeddingCalls) = (cost.CostUsd, cost.LlmCalls, cost.EmbeddingCalls);
    }

    public async Task<LearnedGlossary?> LearnedAliasesAsync() => await _registry.GetLearnedGlossaryAsync().ConfigureAwait(false);

    public async Task<IReadOnlyList<FormatSummary>> RegistrySnapshotAsync()
    {
        var list = new List<FormatSummary>();
        foreach (var e in await _registry.GetIndexAsync().ConfigureAwait(false))
        {
            var spec = (await _registry.GetAsync(e.FormatId, e.Version).ConfigureAwait(false))!;
            list.Add(new FormatSummary(spec.FormatId, spec.Version, spec.CarrierName, spec.FileKind, spec.CreatedBy, spec.CreatedAt,
                await FormatLifecycle.StatusAsync(_registry, spec).ConfigureAwait(false), spec.Supersedes, spec.Learning?.VerificationGate));
        }

        return list;
    }

    /// <summary>The run's registry (learned specs included) — the multi-exemplar study replays them.</summary>
    internal IFormatRegistry Registry => _registry;

    internal CachingContentUnderstanding? CuCache => _cache;
}

/// <summary>The deployed API (brief 07) — LOSSRUN_API_BASE, LOSSRUN_API_USER / LOSSRUN_API_PASSWORD; uses the live format registry.</summary>
internal sealed class ApiRunner : IDocumentRunner
{
    // LOSSRUN_API_TOKEN (Entra bearer, the organisation's environment) or LOSSRUN_API_USER / LOSSRUN_API_PASSWORD (demo host).
    private readonly HttpClient _http = Api.Client();
    private readonly string _base = Api.Base(null);
    private readonly string _runKey = Guid.NewGuid().ToString("N")[..8];

    public LearnOptions Options { get; } = LearnOptions.FromConfiguration();

    public ApiRunner() => _http.Timeout = TimeSpan.FromMinutes(10);

    public async Task<RunResult> RunAsync(string corpusDir, ManifestEntry entry, TruthRecord? truth, string outDir)
    {
        var total = Stopwatch.StartNew();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(CorpusManifest.DocumentPath(corpusDir, entry.File)).ConfigureAwait(false)), "file", Path.GetFileName(entry.File));
        if (EvalRunner.CarrierName(entry.Carrier) is { } carrierHint)
        {
            form.Add(new StringContent(carrierHint), "carrierHint");
        }
        using var post = new HttpRequestMessage(HttpMethod.Post, $"{_base}/uploads") { Content = form };
        post.Headers.Add("Idempotency-Key", $"eval-{_runKey}-{entry.Id}");
        using var response = await _http.SendAsync(post).ConfigureAwait(false);
        var accepted = JsonSerializer.Deserialize<UploadAccepted>(await response.Content.ReadAsStringAsync().ConfigureAwait(false), CanonicalJson.Options)!;
        UploadManifest manifest;
        while (true)
        {
            await Task.Delay(500).ConfigureAwait(false);
            manifest = JsonSerializer.Deserialize<UploadManifest>(await _http.GetStringAsync($"{_base}/uploads/{accepted.UploadId}").ConfigureAwait(false), CanonicalJson.Options)!;
            if (manifest.Status is UploadStatuses.Completed or UploadStatuses.AwaitingReview or UploadStatuses.Failed || total.Elapsed > TimeSpan.FromMinutes(8))
            {
                break;
            }
        }

        var result = new RunResult { Id = entry.Id, Mode = "api", TotalMs = total.ElapsedMilliseconds, IngestMs = manifest.FirstClaimMs ?? 0 };
        result.Segments = [.. manifest.Items.Where(i => i.PageRange is not null).Select(i => new GraphSegment
        {
            Category = i.Classification, StartPage = int.Parse(i.PageRange!.Split('-')[0]), EndPage = int.Parse(i.PageRange.Split('-')[1]), ChildDocumentId = i.Classification == "LossRun" ? i.DocumentId : null,
        })];
        result.Unsupported = [.. manifest.Items.Where(i => i.Status == ItemStatuses.Unsupported).Select(i => $"{i.FileName}: {i.Reason}")];
        foreach (var item in manifest.Items.Where(i => i.Classification == "LossRun"))
        {
            var doc = JsonSerializer.Deserialize<DocumentResult>(await _http.GetStringAsync($"{_base}/documents/{item.DocumentId}").ConfigureAwait(false), CanonicalJson.Options)!;
            var hard = doc.RuleOutcomes.Where(o => o.Status == RuleStatus.Fail && o.Severity == RuleSeverity.Hard).ToList();
            result.Documents.Add(new ExtractedDocument
            {
                GraphDocumentId = item.DocumentId, FinalTier = item.RoutingTier ?? "", RoutingTier = doc.Routing?.Tier.ToString(), FingerprintTier = doc.Routing?.FingerprintTier,
                FormatRef = doc.FormatRef, FormatStatus = item.FormatStatus, ConfirmNewFormat = item.ReviewKind == ReviewKinds.ConfirmFormat,
                LearnPublished = item.RoutingTier is "Learned" or "Delta", Reasons = [.. doc.Routing?.Reasons ?? []], Document = item.RoutingTier == "Review" ? null : doc.Document,
                PreFill = item.RoutingTier == "Review" ? doc.Document : null,
                HardFailures = hard.Count, HardFailureRules = [.. hard.Select(h => h.RuleId).Distinct()], ModelCalls = doc.Cost?.LlmCalls ?? 0,
            });
        }

        result.CostUsd = manifest.Cost?.TotalUsd ?? 0;
        result.LlmCalls = manifest.Cost?.LlmCalls ?? 0;
        result.EmbeddingCalls = manifest.Cost?.EmbeddingCalls ?? 0;
        return result;
    }

    public async Task<IReadOnlyList<FormatSummary>> RegistrySnapshotAsync() =>
        JsonSerializer.Deserialize<List<FormatSummary>>(await _http.GetStringAsync($"{_base}/formats").ConfigureAwait(false), CanonicalJson.Options)!;

    public async Task<LearnedGlossary?> LearnedAliasesAsync() =>
        new() { Mappings = JsonSerializer.Deserialize<List<LearnedMapping>>(await _http.GetStringAsync($"{_base}/glossary/aliases").ConfigureAwait(false), CanonicalJson.Options)! };
}

/// <summary>Brief 27 stub: no Content Understanding service; with <c>--cu-cache</c> in front, cached documents still run.</summary>
internal sealed class StubContentUnderstanding : IContentUnderstandingService
{
    public Task EnsureAnalyzerAsync(string analyzerId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<CuCallResult> AnalyzeAsync(string analyzerId, BinaryData content, string contentType, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("stub run: Content Understanding is not called (no --cu-cache entry for this file)");
}

/// <summary>Brief 27 stub: a learn model whose answer never verifies — every learn ends in review, at no cost.</summary>
internal sealed class StubLearnModel : ILearnModel
{
    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ModelResponse("{}", 0, 0, 0, 0, TimeSpan.Zero, "stub", []));
}
