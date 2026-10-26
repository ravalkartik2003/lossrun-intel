using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LossRun.Contracts;
using LossRun.Formats;

namespace LossRun.Learning;

/// <param name="Graph">The document to learn from (the same graph the spec is verified on).</param>
/// <param name="Prior">Drift or escalation: the carrier's last known spec, supplied as a prior; the result is its next version.</param>
/// <param name="Reason">Why learning runs (routing reason), quoted to the model when there is a prior.</param>
/// <param name="SourcePdf">Original PDF bytes, only needed for the optional page image.</param>
/// <param name="Exemplars">Other documents of the same format (06 Answers 5): a candidate that verifies on <paramref name="Graph"/> must also
/// verify on every exemplar (replay only, no model call) before it is published; failures are fed back to the model.</param>
public sealed record LearnRequest(DocumentGraph Graph, string RunId, SubmissionContext? Submission = null, CarrierFormat? Prior = null, string Reason = "fingerprint miss",
    byte[]? SourcePdf = null, IReadOnlyList<DocumentGraph>? Exemplars = null);

/// <summary>One propose → replay → verify round.</summary>
public sealed record LearnAttempt(string Stage, int Iteration, CarrierFormat? Spec, Verification? Verification, string? Error, ModelResponse Response);

/// <summary>Result of the learn loop or a delta repair: a published, verified spec, or the best attempt for Review.</summary>
public sealed record LearnOutcome(
    bool Published,
    CarrierFormat? Spec,
    Verification? Verification,
    IReadOnlyList<LearnAttempt> Attempts,
    IReadOnlyList<UsageEvent> Usage,
    IReadOnlyList<string> Reasons,
    TimeSpan Elapsed)
{
    public int ModelCalls => Attempts.Count;
    public long InputTokens => Attempts.Sum(a => a.Response.InputTokens);
    public long CachedInputTokens => Attempts.Sum(a => a.Response.CachedInputTokens);
    public long OutputTokens => Attempts.Sum(a => a.Response.OutputTokens);
    public long ReasoningTokens => Attempts.Sum(a => a.Response.ReasoningTokens);
}

/// <summary>
/// The learn loop (brief 06; carrier-format-memory.md "LLM-synthesized extractors", verify-before-store): the model
/// proposes a spec, the replay engine executes it on the same graph, the verifier checks rules, unmapped headers,
/// required fields and printed totals, and failures go back to the model for up to <see cref="LearnOptions.MaxIterations"/>
/// rounds. Only a clean spec is published; on exhaustion the best attempt is returned unpublished for Review.
/// </summary>
public sealed class FormatLearner(ILearnModel model, IFormatRegistry registry, LearnOptions options, IEmbeddingService? embeddings = null, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Prompt-cache routing key: every learn and delta call shares the static prefix.</summary>
    public static string CacheKey => $"lossrun-{PromptBuilder.Version}";

    public async Task<LearnOutcome> LearnAsync(LearnRequest request, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var graph = request.Graph;
        var glossary = await LearnedGlossaryAsync(cancellationToken).ConfigureAwait(false);
        var image = options.IncludePageImage && request.SourcePdf is { } pdf ? PageImage.RenderFirstPage(pdf) : null;
        var turns = new List<ModelTurn> { new("user", PromptBuilder.LearnDocument(graph, request.Submission, request.Prior, request.Reason)) };
        var attempts = new List<LearnAttempt>();

        var maxIterations = options.IterationsFor(request.Graph);
        var autofixes = new List<string>();
        for (var iteration = 1; iteration <= maxIterations; iteration++)
        {
            var response = await model.CompleteAsync(new ModelRequest("learn", options.LearnDeployment, options.LearnModelVersion, PromptBuilder.StaticPrefix, [.. turns],
                "carrier_format", ModelSchema.Proposal, image, CacheKey, graph.DocumentId, request.RunId), cancellationToken).ConfigureAwait(false);

            CarrierFormat spec;
            try
            {
                spec = ModelSchema.ToSpec(response.Json, s => Candidate(s, graph, request.Prior));
            }
            catch (JsonException e)
            {
                attempts.Add(new LearnAttempt("learn", iteration, null, null, e.Message, response));
                turns.Add(new("assistant", response.Json));
                turns.Add(new("user", $"# Your answer is not a valid spec\n{e.Message}\nReturn the complete spec."));
                continue;
            }

            // Mechanical lint fixes need no model call (brief 08b): printed constants become locators.
            autofixes.AddRange(SpecLint.LocatePrintedConstants(spec, graph, glossary).Concat(SpecLint.RetargetRowLevelColumns(spec)).Concat(SpecLint.RetargetByValueShape(spec, graph, glossary)).Concat(SpecLint.DeclareFillDown(spec, graph)).Concat(SpecLint.RetargetCodedColumns(spec)).Concat(SpecLint.FixInvertedLookupSubtotals(spec)).Concat(SpecLint.MapLookupSubtotals(spec, graph, glossary)).Concat(SpecLint.RelaxRowKindPunctuation(spec)).Concat(SpecLint.UnanchorTotalRowKinds(spec, graph)).Concat(SpecLint.DropInertParts(spec))
                .Concat(SpecLint.LocateLabelNextDates(spec, graph, glossary)).Concat(SpecLint.MapPeriodDateColumns(spec, graph, glossary))
                .Concat(SpecLint.FitNormalizers(spec, glossary)).Concat(SpecLint.KeepPrintedZeros(spec)).Concat(SpecLint.DropUnnamedComponents(spec, glossary)).Concat(SpecLint.FixPunctuationBoundaries(spec)).Concat(SpecLint.RepairStackedParts(spec)).Concat(SpecLint.RepairRecordUnder(spec, graph))
                .Concat(SpecLint.RepairMeasureColumns(spec))
                .Concat(SpecLint.DeclareAbsentComponents(spec, graph, glossary)).Concat(SpecLint.DeclareStatusFrom(spec, graph, glossary))
                .Concat(SpecLint.LocateLabelRightValues(spec, graph, glossary)).Concat(SpecLint.RepairDocumentFields(spec, graph, glossary))
                .Concat(SpecLint.LocateHeaderFields(spec, graph, glossary))
                .Select(n => $"learn#{iteration} autofix: {n}"));

            Verification verification;
            try
            {
                verification = SpecVerifier.Verify(graph, spec, request.Submission, glossary);
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or ArgumentException or System.Text.RegularExpressions.RegexParseException)
            {
                // A spec the replay cannot execute (e.g. an invalid regex, a value of the wrong type) is a failed iteration, not a crash.
                attempts.Add(new LearnAttempt("learn", iteration, spec, null, $"replay failed: {e.Message}", response));
                turns.Add(new("assistant", response.Json));
                turns.Add(new("user", $"# The spec could not be replayed\n{e.Message}\nFix it and return the complete spec."));
                continue;
            }

            attempts.Add(new LearnAttempt("learn", iteration, spec, verification, null, response));
            if (verification.Clean(options.SpreadsheetMinRequiredCoverage) && request.Exemplars is { Count: > 0 } exemplars)
            {
                var failing = new List<(DocumentGraph Graph, string Summary, string Feedback)>();
                foreach (var exemplar in exemplars)
                {
                    try
                    {
                        var check = SpecVerifier.Verify(exemplar, spec, request.Submission, glossary);
                        if (!check.Clean(options.SpreadsheetMinRequiredCoverage))
                        {
                            failing.Add((exemplar, check.Summary, check.Feedback()));
                        }
                    }
                    catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or ArgumentException)
                    {
                        failing.Add((exemplar, $"replay failed: {e.Message}", e.Message));
                    }
                }

                if (failing.Count > 0)
                {
                    attempts[^1] = attempts[^1] with { Error = $"verified on the learning document; fails {failing.Count}/{exemplars.Count} exemplar(s)" };
                    turns.Add(new("assistant", response.Json));
                    turns.Add(new("user", $"# The spec verifies on this document but fails on {failing.Count} of {exemplars.Count} other documents of the same format\n" +
                        string.Join("\n", failing.Take(2).Select(f => $"## Exemplar {f.Graph.DocumentId}\n{f.Feedback}")) +
                        "\nGeneralize the spec (regexes and row kinds must fit every document of this layout) and return the complete spec."));
                    continue;
                }
            }

            if (verification.Clean(options.SpreadsheetMinRequiredCoverage))
            {
                var published = await PublishAsync(spec, graph, "learn", request.Prior, attempts, image is not null, glossary, cancellationToken).ConfigureAwait(false);
                verification = SpecVerifier.Verify(graph, published, request.Submission, glossary); // lineage now cites the published formatId@version
                return Outcome(true, published, verification, attempts, [$"learned {published.Ref} in {iteration} iteration(s): {verification.Summary}", .. autofixes], stopwatch.Elapsed);
            }

            turns.Add(new("assistant", response.Json));
            turns.Add(new("user", verification.Feedback()));
        }

        var best = attempts.Where(a => a.Verification is not null).MinBy(a => (a.Error is null ? 0 : 1, a.Verification!.Penalty));
        return Outcome(false, best?.Spec, best?.Verification, attempts,
            [$"learn exhausted {maxIterations} iteration(s) without a verified spec → review", best?.Verification?.Summary ?? "no attempt produced a valid spec",
                .. best?.Verification?.DocumentFacts ?? []],
            stopwatch.Elapsed);
    }

    internal static LearnOutcome Outcome(bool published, CarrierFormat? spec, Verification? verification, List<LearnAttempt> attempts, List<string> reasons, TimeSpan elapsed) =>
        new(published, spec, verification, attempts, attempts.SelectMany(a => a.Response.Usage).ToList(), reasons, elapsed);

    /// <summary>The seed glossary with the active learned aliases (ADR 0019: candidates and retired aliases are never used).</summary>
    internal Task<Glossary> LearnedGlossaryAsync(CancellationToken cancellationToken) => registry.GlossaryAsync(cancellationToken: cancellationToken);

    /// <summary>Pipeline-owned fields of a candidate under verification (final ids and versions are assigned at publish).</summary>
    private static void Candidate(CarrierFormat spec, DocumentGraph graph, CarrierFormat? prior)
    {
        spec.SpecSchema = CarrierFormat.SpecVersion;
        spec.FormatId = prior?.FormatId ?? "candidate";
        spec.Version = prior is null ? 1 : prior.Version + 1;
        spec.FileKind = FileKindOf(graph);
        spec.CreatedBy = "learn:candidate";
    }

    internal static string FileKindOf(DocumentGraph graph) => graph.SourceFile.Kind is FileKind.Xlsx or FileKind.Xls or FileKind.Xlsb or FileKind.Csv ? "spreadsheet" : "pdf";

    /// <summary>
    /// Publishes a verified spec: final formatId/version, fingerprint keys from the graph, provenance, centroid (PDFs),
    /// and learned glossary mappings. Never called with an unverified spec.
    /// </summary>
    internal async Task<CarrierFormat> PublishAsync(CarrierFormat spec, DocumentGraph graph, string stage, CarrierFormat? prior, IReadOnlyList<LearnAttempt> attempts,
        bool pageImage, Glossary glossary, CancellationToken cancellationToken)
    {
        var fp = FingerprintText.Compute(graph);
        var index = await registry.GetIndexAsync(cancellationToken).ConfigureAwait(false);
        var deployment = stage == "delta" ? options.DeltaDeployment : options.LearnDeployment;
        var modelVersion = stage == "delta" ? options.DeltaModelVersion : options.LearnModelVersion;
        var verification = attempts.Last(a => a.Verification is not null).Verification!;

        spec.SpecSchema = CarrierFormat.SpecVersion;
        spec.FormatId = prior?.FormatId ?? NewFormatId(spec.CarrierName, graph, index);
        spec.FileKind = FileKindOf(graph);
        spec.CreatedBy = $"{stage}:{deployment}@{modelVersion ?? "unversioned"}";
        spec.CreatedAt = _clock.GetUtcNow();
        spec.ReviewedBy = null;
        spec.Supersedes = prior?.Ref;
        spec.Fingerprint = new FormatFingerprintSpec
        {
            IdentityKey = fp.IdentityKey,
            HeaderKey = fp.HeaderKey,
            IdentityText = fp.IdentityText,
            HeaderText = fp.HeaderText,
            Embedding = spec.FileKind == "pdf" && embeddings is not null && options.EmbeddingModel is { } m
                ? prior?.Fingerprint.Embedding ?? new EmbeddingTier { Model = m }
                : prior?.Fingerprint.Embedding,
        };
        spec.Learning = new LearningRecord
        {
            Stage = stage,
            Model = deployment,
            ModelVersion = modelVersion,
            PromptVersion = options.PromptVersion,
            Iterations = attempts.Count(a => a.Stage == stage),
            InputTokens = attempts.Sum(a => a.Response.InputTokens),
            CachedInputTokens = attempts.Sum(a => a.Response.CachedInputTokens),
            OutputTokens = attempts.Sum(a => a.Response.OutputTokens),
            PageImage = pageImage,
            SourceDocumentId = graph.DocumentId,
            Verification = verification.Summary,
            VerificationGate = verification.Gate(options.SpreadsheetMinRequiredCoverage),
            LearnedFrom = LearningRecord.Rendering(graph.SourceFile),
        };

        // A new carrier's first learned version starts in shadow; drift/delta versions inherit the prior's live status.
        spec.Status = prior is null ? FormatLifecycle.Shadow : await FormatLifecycle.StatusAsync(registry, prior, cancellationToken).ConfigureAwait(false);

        for (var attempt = 0; ; attempt++)
        {
            spec.Version = Math.Max(prior?.Version ?? 0, index.Where(e => e.FormatId == spec.FormatId).Select(e => e.Version).DefaultIfEmpty(0).Max()) + 1;
            try
            {
                await registry.PublishAsync(spec, cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (FormatAlreadyPublishedException) when (attempt < 3)
            {
                index = await registry.GetIndexAsync(cancellationToken).ConfigureAwait(false); // a concurrent learner published first
            }
        }

        await FormatLifecycle.InitializeAsync(registry, spec, prior is null ? null : $"inherited:{prior.Ref}", spec.CreatedAt, cancellationToken).ConfigureAwait(false);

        if (spec.Fingerprint.Embedding is { } tier && embeddings is not null)
        {
            var vector = (await embeddings.EmbedAsync(FingerprintText.ForEmbedding(graph), graph.DocumentId, attempts[0].Response.Usage.FirstOrDefault()?.RunId ?? "learn",
                cancellationToken).ConfigureAwait(false)).Vector;
            var existing = await registry.GetCentroidAsync(spec.FormatId, cancellationToken).ConfigureAwait(false);
            await registry.SaveCentroidAsync(existing is null || existing.Model != tier.Model
                ? new FormatCentroid { FormatId = spec.FormatId, Model = tier.Model, ExemplarCount = 1, ExemplarDocumentIds = [graph.DocumentId], Vector = vector }
                : new FormatCentroid
                {
                    FormatId = spec.FormatId, Model = tier.Model, ExemplarCount = existing.ExemplarCount + 1,
                    ExemplarDocumentIds = [.. existing.ExemplarDocumentIds, graph.DocumentId],
                    Vector = existing.Vector.Select((v, i) => (v * existing.ExemplarCount + vector[i]) / (existing.ExemplarCount + 1)).ToArray(),
                }, cancellationToken).ConfigureAwait(false);
        }

        await registry.AppendLearnedMappingsAsync(LearnedMappings(spec, graph, deployment, modelVersion, verification), options.AliasPolicy, cancellationToken).ConfigureAwait(false);
        return spec;
    }

    /// <summary>
    /// Mappings the seed glossary does not already resolve to the same target (glossary growth, brief 06 §6): candidate
    /// aliases (ADR 0019) with this learn's provenance and verification evidence.
    /// </summary>
    internal IReadOnlyList<LearnedMapping> LearnedMappings(CarrierFormat spec, DocumentGraph graph, string deployment, string? modelVersion,
        Verification? verification = null) =>
        spec.ColumnMap
            .Where(c => c.Target != "claim.rawStatus" && CanonicalData.Glossary.ResolveHeader(c.Source)?.Field != c.Target)
            .GroupBy(c => (c.Source, c.Target)).Select(g => g.First())
            .Select(c => new LearnedMapping
            {
                Header = c.Source,
                CanonicalField = c.Target,
                CarrierName = spec.CarrierName,
                FormatId = spec.FormatId,
                FormatVersion = spec.Version.ToString(CultureInfo.InvariantCulture),
                LearnedBy = new LearnedBy { Model = deployment, ModelVersion = modelVersion, PromptVersion = options.PromptVersion },
                Confidence = 0.9,
                FirstSeenDocumentId = graph.DocumentId,
                FirstSeenUtc = _clock.GetUtcNow(),
                Status = AliasStatus.Candidate,
                TotalsTie = verification?.TotalsTie,
                VerificationGate = verification?.Gate(options.SpreadsheetMinRequiredCoverage),
                Evidence = [new AliasEvidence { DocumentId = graph.DocumentId, FormatRef = spec.Ref, Route = "learn", TotalsTie = verification?.TotalsTie, AtUtc = _clock.GetUtcNow() }],
            })
            .ToList();

    /// <summary>"Fictional Casualty Group" + pdf → "fictional-casualty-group-pdf" (suffixed -2, -3 … when taken).</summary>
    internal static string NewFormatId(string carrierName, DocumentGraph graph, IReadOnlyList<FormatIndexEntry> index)
    {
        var slug = Regex.Replace(CanonicalComparer.NormalizeText(carrierName), "[^a-z0-9]+", "-").Trim('-');
        var kind = graph.SourceFile.Kind switch { FileKind.Csv => "csv", FileKind.Xlsx or FileKind.Xls => "xlsx", _ => "pdf" };
        var id = $"{(slug.Length == 0 ? "carrier" : slug)}-{kind}";
        var candidate = id;
        for (var n = 2; index.Any(e => e.FormatId == candidate); n++)
        {
            candidate = $"{id}-{n}";
        }

        return candidate;
    }
}

/// <summary>Renders page 1 of a PDF for the optional image input (brief 06: measure whether it helps).</summary>
public static class PageImage
{
    public static byte[] RenderFirstPage(byte[] pdf, int dpi = 110)
    {
        using var bitmap = PDFtoImage.Conversion.ToImage(pdf, page: 0, options: new PDFtoImage.RenderOptions(Dpi: dpi));
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}
