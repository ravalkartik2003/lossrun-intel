using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LossRun.Contracts;
using LossRun.Ingestion.ContentUnderstanding;
using LossRun.Ingestion.Metering;
using LossRun.Ingestion.Spreadsheets;

namespace LossRun.Ingestion;

/// <summary>A file that was not turned into a graph, and why (never dropped silently).</summary>
public sealed record UnsupportedFile(string FileName, FileKind Kind, string Reason);

/// <summary>Whether a multi-page PDF went to the classifier (skip rate is reported in brief 08).</summary>
public sealed record ClassifierDecision(string DocumentId, bool Skipped, double HeaderResolution, string Reason);

/// <summary>One Content Understanding call, for the latency/usage report.</summary>
public sealed record CallMeasurement(string DocumentId, string AnalyzerId, string Stage, string OperationId, TimeSpan Latency, IReadOnlyList<UsageEvent> Usage,
    string Mode = "async");

public sealed class IngestionResult
{
    public string RunId { get; init; } = "";
    public List<DocumentGraph> Documents { get; } = [];
    public List<UnsupportedFile> Unsupported { get; } = [];

    /// <summary>One cost per uploaded (top-level or archive-member) document; bundle segments are attributed to their parent.</summary>
    public List<DocumentCost> Costs { get; } = [];
    public List<CallMeasurement> Calls { get; } = [];
    /// <summary>Brief 34 Answers 1: a stored CU result for the same analyzer and bytes that was not reused, and which hash moved (logged by the activity).</summary>
    public List<string> CacheMisses { get; } = [];
    public List<ClassifierDecision> ClassifierDecisions { get; } = [];
}

/// <summary>
/// Brief 04 preprocessing: sniff → expand ZIPs → unlock PDFs → spreadsheets natively (no Azure call) or
/// PDFs/images through Content Understanding (layout, or the zero-shot classifier for multi-page bundles) → document
/// graph(s), raw results to Blob, one UsageEvent per CU meter.
/// </summary>
public sealed partial class IngestionPipeline(
    IContentUnderstandingService contentUnderstanding,
    ICostMeter costMeter,
    IRawResultStore rawResults,
    PriceTable prices,
    Glossary glossary,
    IngestionOptions options)
{
    public async Task<IngestionResult> IngestAsync(string fileName, byte[] bytes, CancellationToken cancellationToken = default)
    {
        var result = new IngestionResult { RunId = Guid.NewGuid().ToString("N") };
        var recorder = new InMemoryCostMeter();
        var meter = new CompositeCostMeter(recorder, costMeter);
        await IngestFileAsync(fileName, bytes, parentId: null, archivePath: null, result, meter, cancellationToken).ConfigureAwait(false);
        ApplyLineOfBusiness(result, glossary, options.LossRunCategory);
        ApplyReportKind(result, options.LossRunCategory);

        foreach (var root in result.Documents.Where(d => d.SourceFile.FirstPage is null).Select(d => d.DocumentId).Distinct())
        {
            var graph = result.Documents.First(d => d.DocumentId == root);
            result.Costs.Add(CostCalculator.Compute(root, result.RunId, PathOf(graph), recorder.Events, prices));
        }

        return result;
    }

    /// <summary>
    /// ADR 0020: a loss run of another line of business (property, auto, liability, …) is kept, not processed, before any learn:
    /// a bundle's segment loses its child graph and gets the reason; a whole document gets one kept segment with the reason.
    /// The manifest lists it, chat indexes it, and no model call is spent on it.
    /// </summary>
    public static void ApplyLineOfBusiness(IngestionResult result, Glossary glossary, string lossRunCategory)
    {
        foreach (var graph in result.Documents.Where(d => d.Segments.Count == 0 || d.SourceFile.FirstPage is not null).ToList())
        {
            var verdict = LineOfBusinessDetector.Detect(graph, glossary);
            graph.LineOfBusiness = verdict.Line;
            if (!verdict.OutOfScope)
            {
                continue;
            }

            var reason = $"line of business: {Words(verdict.Line!)} ({string.Join("; ", verdict.Evidence.Take(3))}); out of the workers' comp scope, kept, not processed";
            if (graph.SourceFile.FirstPage is not null && graph.Segments.Count == 1)
            {
                graph.Segments[0].ChildDocumentId = null; // the parent's segment object: it becomes a kept segment
                graph.Segments[0].Reason = reason;
                result.Documents.Remove(graph);
            }
            else
            {
                graph.Segments = [new GraphSegment { Category = lossRunCategory, StartPage = 1, EndPage = Math.Max(1, graph.Pages.Count), Reason = reason }];
            }
        }

        static string Words(string line) => Regex.Replace(line, "(?<=[a-z])([A-Z])", " $1").ToLowerInvariant();
    }

    /// <summary>A title or page header naming an insurer (brief 23 Answers 3: the carrier a segment prints).</summary>
    [GeneratedRegex(@"(?i)\b(?:insurance|mutual|casualty|assurance|indemnity|underwriters|company|co\.|group|exchange)\b")]
    private static partial Regex InsurerName();

    /// <summary>
    /// Brief 23 §3: the classifier split one six-page run into three loss-run segments where a page opened with a period
    /// heading and the repeated table header. A loss-run segment that starts on the page after another, prints the same table
    /// header signature, and prints no document header block of its own on its first page (no valuation date, insured or
    /// carrier label: <see cref="HeaderFields"/>) is that run's continuation, and the two are merged before routing. A second
    /// run with its own header block stays a segment of its own.
    /// </summary>
    public static void MergeContinuations(DocumentGraph parent, string lossRunCategory, Glossary glossary)
    {
        if (parent.Segments.Count < 2)
        {
            return;
        }

        string? Signature(int page, bool first)
        {
            var tables = parent.Tables.Where(t => t.Page == page && t.HeaderTexts.Any(h => h.Trim().Length > 0)).ToList();
            var table = first ? tables.FirstOrDefault() : tables.LastOrDefault();
            return table is null ? null : string.Join("|", table.HeaderTexts.Select(CanonicalComparer.NormalizeText));
        }

        var header = HeaderFields.HeaderPages(parent, glossary);
        // Brief 23 Answers 3: the carrier a segment prints, labelled ("Carrier: …") or as a title naming an insurer; null when none.
        string? Carrier(GraphSegment s)
        {
            var labelled = glossary.Fields.TryGetValue("document.carrierName", out var entry)
                ? entry.Aliases.Select(a => HeaderFields.Read(new DocumentGraph { Words = [.. parent.Words.Where(w => w.Page >= s.StartPage && w.Page <= s.EndPage)] }, a.Header, "document.carrierName"))
                    .FirstOrDefault(h => h is not null)
                : null;
            var printed = labelled?.Value ?? parent.Paragraphs.FirstOrDefault(p => p.Page >= s.StartPage && p.Page <= s.EndPage && p.Role is "title" or "pageHeader" && InsurerName().IsMatch(p.Text))?.Text;
            return printed is null ? null : CanonicalComparer.NormalizeText(printed);
        }

        var merged = new List<GraphSegment>();
        foreach (var segment in parent.Segments.OrderBy(s => s.StartPage))
        {
            if (merged.Count > 0 && merged[^1] is { } previous && previous.Category == lossRunCategory && segment.Category == lossRunCategory
                && segment.StartPage == previous.EndPage + 1 && !header.Contains(segment.StartPage)
                && Signature(previous.EndPage, first: false) is { } before && before == Signature(segment.StartPage, first: true)
                && !(Carrier(previous) is { } a && Carrier(segment) is { } b && a != b))
            {
                parent.Warnings.Add($"segments merged: p{segment.StartPage}-{segment.EndPage} continues p{previous.StartPage}-{previous.EndPage} (same table header, no header block)");
                previous.EndPage = segment.EndPage;
                continue;
            }

            merged.Add(segment);
        }

        parent.Segments = merged;
    }

    /// <summary>
    /// ADR 0021: a loss-analysis page (classification <c>LossAnalysis</c>) or a changes-only run is kept, not processed, before
    /// any learn, the same way as another line of business (<see cref="ApplyLineOfBusiness"/>): the manifest lists it with the
    /// reason, chat indexes it, and its rows never enter the numbers.
    /// </summary>
    public static void ApplyReportKind(IngestionResult result, string lossRunCategory)
    {
        foreach (var graph in result.Documents.Where(d => d.Segments.Count == 0 || d.SourceFile.FirstPage is not null).ToList())
        {
            var verdict = ReportKindDetector.Detect(graph, lossRunCategory);
            graph.ReportKind = verdict.Kind;
            if (verdict.Kind is null)
            {
                continue;
            }

            var reason = $"{verdict.Reason} ({verdict.Evidence})";
            if (graph.SourceFile.FirstPage is not null && graph.Segments.Count == 1)
            {
                graph.Segments[0].ChildDocumentId = null;
                graph.Segments[0].Category = verdict.Category;
                graph.Segments[0].Reason = reason;
                result.Documents.Remove(graph);
            }
            else
            {
                graph.Segments = [new GraphSegment { Category = verdict.Category, StartPage = 1, EndPage = Math.Max(1, graph.Pages.Count), Reason = reason }];
            }
        }
    }

    private async Task IngestFileAsync(string fileName, byte[] bytes, string? parentId, string? archivePath, IngestionResult result, ICostMeter meter,
        CancellationToken cancellationToken)
    {
        var kind = FileSniffer.Sniff(bytes, fileName);
        var source = new SourceFile
        {
            FileName = fileName,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            SizeBytes = bytes.Length,
            Kind = kind,
            ParentDocumentId = parentId,
            ArchivePath = archivePath,
        };
        // Content-addressed by default; scoped (e.g. per upload, brief 07) so the same file in two uploads gets two documents.
        var documentId = options.DocumentIdScope is { Length: > 0 } scope
            ? "doc-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{scope}:{source.Sha256}")))[..16]
            : "doc-" + source.Sha256[..16];

        switch (kind)
        {
            case FileKind.Zip:
                IReadOnlyList<ArchiveEntry> entries;
                try
                {
                    entries = ArchiveExpander.Expand(bytes, options.MaxArchiveDepth, options.MaxArchiveBytes);
                }
                catch (InvalidDataException e)
                {
                    result.Unsupported.Add(new UnsupportedFile(fileName, kind, e.Message));
                    return;
                }

                foreach (var entry in entries)
                {
                    await IngestFileAsync(Path.GetFileName(entry.Path), entry.Bytes, documentId, entry.Path, result, meter, cancellationToken).ConfigureAwait(false);
                }

                return;

            case var _ when FileSniffer.IsSpreadsheet(kind):
                try
                {
                    result.Documents.Add(SpreadsheetReader.Read(bytes, source, documentId, glossary, options.Passwords));
                }
                catch (ExcelDataReader.Exceptions.ExcelReaderException e)
                {
                    result.Unsupported.Add(new UnsupportedFile(fileName, kind, e.Message));
                }

                return;

            case FileKind.Pdf:
                var unlocked = PdfUnlocker.Unlock(bytes, options.Passwords);
                source.WasEncrypted = unlocked.WasEncrypted;
                if (!unlocked.Ok)
                {
                    result.Unsupported.Add(new UnsupportedFile(fileName, kind, unlocked.Error!));
                    return;
                }

                // Born-digital vs scanned decides OCR-aware routing (08b): CU OCRs every page, so word confidence cannot.
                source.TextLayer = FirstPageCheck.HasTextLayer(unlocked.Bytes!);
                source.ScanDpi = source.TextLayer == true ? null : FirstPageCheck.ScanDpi(unlocked.Bytes!);

                if (options.ClassifyBundles && unlocked.PageCount >= options.ClassifyWhenPagesAtLeast)
                {
                    var check = options.FirstPageCheck
                        ? FirstPageCheck.Evaluate(unlocked.Bytes!, glossary, options.AcceptThreshold)
                        : new FirstPageCheckResult(false, 0, "first-page check disabled");
                    result.ClassifierDecisions.Add(new ClassifierDecision(documentId, check.IsSingleLossRun, check.HeaderResolution, check.Reason));
                    if (check.IsSingleLossRun)
                    {
                        await LayoutAsync(unlocked.Bytes!, "application/pdf", source, documentId, result, meter, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await ClassifyAsync(unlocked.Bytes!, source, documentId, result, meter, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    await LayoutAsync(unlocked.Bytes!, "application/pdf", source, documentId, result, meter, cancellationToken).ConfigureAwait(false);
                }

                return;

            case var _ when FileSniffer.IsImage(kind):
                await LayoutAsync(bytes, FileSniffer.ContentType(kind), source, documentId, result, meter, cancellationToken).ConfigureAwait(false);
                return;

            case FileKind.Docx:
                result.Unsupported.Add(new UnsupportedFile(fileName, kind, "Word documents are not in the intake scope (CU can read them; enable when needed)."));
                return;

            default:
                result.Unsupported.Add(new UnsupportedFile(fileName, kind, "Unrecognized file type (magic bytes and extension)."));
                return;
        }
    }

    private async Task LayoutAsync(byte[] bytes, string contentType, SourceFile source, string documentId, IngestionResult result, ICostMeter meter,
        CancellationToken cancellationToken)
    {
        var (call, path) = await CallAsync(options.LayoutAnalyzerId, "layout", "layout.json", bytes, contentType, documentId, result, meter, cancellationToken).ConfigureAwait(false);
        source.RawResultBlobPath = path;
        result.Documents.Add(CuResultMapper.Map(call.Root, documentId, source, options.LayoutAnalyzerId, glossary));
    }

    /// <summary>
    /// accept (≥ AcceptThreshold) | review | notLossRun (&lt; ReviewThreshold) for a classified loss-run segment (brief 04
    /// Answers 1). No table to resolve is no evidence against the classifier: narrative statements and scans CU read no
    /// table on are extracted (line fallback) and reviewed, not dropped (ADR 0009, brief 08 eval run).
    /// </summary>
    public static string SegmentDecision(bool hasTable, double derivedConfidence, IngestionOptions options) =>
        !hasTable ? "review"
        : derivedConfidence >= options.AcceptThreshold ? "accept"
        : derivedConfidence >= options.ReviewThreshold ? "review"
        : "notLossRun";

    /// <summary>
    /// Classifies and splits a bundle in one call. The classifier is based on prebuilt-document, so its result already
    /// carries the layout (pages, paragraphs, tables, words): loss-run segments become child graphs from the same result,
    /// with no second (billed) layout call.
    /// </summary>
    private async Task ClassifyAsync(byte[] bytes, SourceFile source, string documentId, IngestionResult result, ICostMeter meter,
        CancellationToken cancellationToken)
    {
        await contentUnderstanding.EnsureAnalyzerAsync(options.ClassifierAnalyzerId, cancellationToken).ConfigureAwait(false);
        var (call, path) = await CallAsync(options.ClassifierAnalyzerId, "classify", "classifier.json", bytes, "application/pdf", documentId, result, meter, cancellationToken)
            .ConfigureAwait(false);
        source.RawResultBlobPath = path;

        var parent = CuResultMapper.Map(call.Root, documentId, source, options.ClassifierAnalyzerId, glossary);
        MergeContinuations(parent, options.LossRunCategory, glossary);
        result.Documents.Add(parent);
        // OCR misreads headers on scans ("Accldent Dt"): there the resolution tolerates near-misses (brief 08d: a fax CU
        // classified as a loss run scored 0.40 on exact matching and was dropped).
        var scanned = source.TextLayer == false;
        var single = parent.Segments.Count == 1 && parent.Segments[0].StartPage == 1 && parent.Segments[0].EndPage >= parent.Pages.Count;
        if (single && parent.Segments[0].Category == options.OtherCategory)
        {
            parent.Segments = FallbackSegments(parent, parent.Segments[0], scanned, options, glossary);
        }

        foreach (var segment in parent.Segments.Where(s => s.DerivedDecision is null && s.Reason is null))
        {
            // CU GA returns no segment confidence: derive one from the segment's best table (brief 04 Answers 1).
            var tables = parent.Tables.Where(t => t.Page >= segment.StartPage && t.Page <= segment.EndPage).ToList();
            segment.DerivedConfidence = tables.Select(t => scanned ? TolerantHeaderResolution(t, glossary) : HeaderResolution(t)).DefaultIfEmpty(0).Max();
            var hasTable = tables.Count > 0;
            segment.DerivedConfidenceMethod = (hasTable ? "header-resolution" : "header-resolution (no table)") + (scanned ? ", OCR-tolerant" : "");
            if (segment.Category == options.LossRunCategory)
            {
                segment.DerivedDecision = SegmentDecision(hasTable, segment.DerivedConfidence.Value, options);
                if (single && segment.DerivedDecision == "notLossRun")
                {
                    // The upload is this one document and CU calls it a loss run: review it rather than drop it.
                    segment.DerivedDecision = "review";
                    segment.DerivedConfidenceMethod += "; single-document upload kept for review";
                }
            }

            if (segment.Category == options.LossRunCategory && hasTable)
            {
                MarkSummaryOnly(segment, tables);
            }
        }

        foreach (var segment in parent.Segments.Where(s => (s.Category == options.LossRunCategory || s.DerivedDecision == "review") && s.DerivedDecision != "notLossRun"))
        {
            var childSource = new SourceFile
            {
                FileName = source.FileName,
                Sha256 = source.Sha256,
                SizeBytes = source.SizeBytes,
                Kind = source.Kind,
                WasEncrypted = source.WasEncrypted,
                ParentDocumentId = documentId,
                ArchivePath = source.ArchivePath,
                FirstPage = segment.StartPage,
                LastPage = segment.EndPage,
                RawResultBlobPath = source.RawResultBlobPath,
                TextLayer = source.TextLayer,
                ScanDpi = source.ScanDpi,
            };
            var childId = $"{documentId}-p{segment.StartPage}-{segment.EndPage}";
            var child = CuResultMapper.Map(call.Root, childId, childSource, options.ClassifierAnalyzerId, glossary, segment.StartPage, segment.EndPage);
            segment.ChildDocumentId = childId;
            child.Segments = [segment]; // same object as the parent's, so the derived confidence/decision travel with it
            result.Documents.Add(child);
        }
    }

    /// <summary>
    /// What a stored raw result was produced from; written after the result, so its presence means the result is complete. Brief 34 A1.2: the key is
    /// (analyzer id, the analyzer definition's hash, the CU defaults hash, the input's hash): a definition edited under the same id, or another model
    /// behind a prebuilt alias, is another result.
    /// </summary>
    public sealed record RawResultMeta(string AnalyzerId, string ContentSha256, string OperationId, string? DefinitionSha256 = null, string? DefaultsHash = null);

    /// <summary>Brief 34 A1.3: written before a billed call; still there without a result on the next attempt, it means that call was billed and lost.</summary>
    public sealed record RawResultPending(string AnalyzerId, string ContentSha256);

    /// <summary>The SHA-256 of the analyzer's checked-in definition (what <c>EnsureAnalyzerAsync</c> creates it from); prebuilt analyzers have none.</summary>
    public static string? DefinitionHash(string analyzerId)
    {
        try
        {
            return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(AnalyzerDefinitions.Get(analyzerId).ReplaceLineEndings("\n"))));
        }
        catch (InvalidOperationException)
        {
            return null; // prebuilt-layout, prebuilt-read: Microsoft's, versioned by the API
        }
    }

    /// <summary>
    /// One Content Understanding call, stored as <paramref name="rawName"/>. Brief 33: when the same analyzer already analyzed the same bytes for this
    /// document (a Durable re-delivery after a worker loss, or the activity's retry), the stored result is reused and nothing is billed: the usage rows
    /// are recorded with quantity 0 and stage <c>reused</c>, so the cost report shows the reuse.
    /// </summary>
    private static string Short(string? hash) => hash is null ? "none" : hash[..Math.Min(12, hash.Length)];

    private async Task<(CuCallResult Call, string Path)> CallAsync(string analyzerId, string stage, string rawName, byte[] bytes, string contentType, string documentId,
        IngestionResult result, ICostMeter meter, CancellationToken cancellationToken)
    {
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var definition = DefinitionHash(analyzerId);
        var metaName = Path.ChangeExtension(rawName, ".meta.json");
        var pendingName = Path.ChangeExtension(rawName, ".pending.json");
        var priorMeta = options.ForceReparse ? null : await rawResults.TryGetAsync(documentId, metaName, cancellationToken).ConfigureAwait(false) is { } metaJson
            ? System.Text.Json.JsonSerializer.Deserialize<RawResultMeta>(metaJson.Json) : null;
        if (priorMeta is { } moved && moved.AnalyzerId == analyzerId && moved.ContentSha256 == sha
            && (moved.DefinitionSha256 != definition || moved.DefaultsHash != options.CuDefaultsHash))
        {
            // Brief 34 Answers 1: both hashes are in the key; the log says which one moved.
            result.CacheMisses.Add($"{analyzerId} {rawName}: definition {Short(moved.DefinitionSha256)} → {Short(definition)}, defaults {Short(moved.DefaultsHash)} → {Short(options.CuDefaultsHash)}");
        }
        else if (priorMeta is { } meta && meta.AnalyzerId == analyzerId && meta.ContentSha256 == sha
            && await rawResults.TryGetAsync(documentId, rawName, cancellationToken).ConfigureAwait(false) is { } stored)
        {
            var reused = new CuCallResult(analyzerId, meta.OperationId, stored.Json, TimeSpan.Zero, "reused");
            var free = CuUsage.ToEvents(reused.Root, documentId, result.RunId, "reused", analyzerId, options.Region, options.ModelDeploymentType, meta.OperationId,
                DateTimeOffset.UtcNow).Select(e => e with { Quantity = 0 }).ToList();
            foreach (var usage in free)
            {
                await meter.RecordAsync(usage, cancellationToken).ConfigureAwait(false);
            }

            result.Calls.Add(new CallMeasurement(documentId, analyzerId, "reused", meta.OperationId, TimeSpan.Zero, free, reused.Mode));
            return (reused, stored.Path);
        }

        // Brief 34 A1.3: a pending marker for the same analyzer and bytes, with no result, means an earlier attempt was billed and its result lost
        // (the write after the call failed). This call is billed again: metered as usual, under stage "rebilled", so the cost view and the
        // reconciliation show what the lost write cost.
        var rebilled = !options.ForceReparse && await rawResults.TryGetAsync(documentId, pendingName, cancellationToken).ConfigureAwait(false) is { } pendingJson
            && System.Text.Json.JsonSerializer.Deserialize<RawResultPending>(pendingJson.Json) is { } pending && pending.AnalyzerId == analyzerId && pending.ContentSha256 == sha;
        await rawResults.SaveAsync(documentId, pendingName, System.Text.Json.JsonSerializer.Serialize(new RawResultPending(analyzerId, sha)), cancellationToken).ConfigureAwait(false);
        var call = await contentUnderstanding.AnalyzeAsync(analyzerId, BinaryData.FromBytes(bytes), contentType, cancellationToken).ConfigureAwait(false);
        var meteredStage = rebilled ? "rebilled" : stage;
        var events = CuUsage.ToEvents(call.Root, documentId, result.RunId, meteredStage, analyzerId, options.Region, options.ModelDeploymentType, call.OperationId,
            DateTimeOffset.UtcNow);
        foreach (var usage in events)
        {
            await meter.RecordAsync(usage, cancellationToken).ConfigureAwait(false);
        }

        result.Calls.Add(new CallMeasurement(documentId, analyzerId, meteredStage, call.OperationId, call.Latency, events, call.Mode));
        var path = await rawResults.SaveAsync(documentId, rawName, call.RawJson, cancellationToken).ConfigureAwait(false);
        await rawResults.SaveAsync(documentId, metaName, System.Text.Json.JsonSerializer.Serialize(new RawResultMeta(analyzerId, sha, call.OperationId, definition, options.CuDefaultsHash)),
            cancellationToken).ConfigureAwait(false);
        return (call, path);
    }

    /// <summary>
    /// <see cref="HeaderResolution"/> for OCR text: a header that does not resolve exactly counts when it is a near-miss
    /// (normalized similarity ≥ 0.8) of a glossary alias. Used only to decide whether a scanned segment is a loss run;
    /// field mapping stays exact (replay resolves OCR headers against the spec, not the glossary).
    /// </summary>
    public static double TolerantHeaderResolution(GraphTable table, Glossary glossary)
    {
        var headers = table.HeaderTexts.Select((text, i) => (text, field: i < table.HeaderFields.Count ? table.HeaderFields[i] : null))
            .Where(h => h.text.Trim().Length > 0)
            .ToList();
        if (headers.Count == 0)
        {
            return 0;
        }

        var aliases = glossary.Fields.Values.SelectMany(f => f.Aliases).Select(a => CanonicalComparer.NormalizeText(a.Header)).Where(a => a.Length >= 3).Distinct().ToList();
        var resolved = headers.Count(h => h.field is not null || glossary.ResolveHeader(h.text) is not null
            || CanonicalComparer.NormalizeText(h.text) is { Length: >= 3 } key && aliases.Any(a => CanonicalComparer.TextSimilarity(key, a) >= 0.8));
        return Math.Round((double)resolved / headers.Count, 4);
    }

    /// <summary>
    /// The single-document fallback (brief 08d, narrowed in brief 18 P6): CU called the only document "Other", but it may be
    /// a loss run CU did not recognise (a fax). On a document dense with text, only pages carrying a qualifying table —
    /// header resolution at or above the review threshold, with at least <see cref="IngestionOptions.FallbackMinResolvedHeaders"/>
    /// resolved headers — go on as review segments, and only when such pages are at least
    /// <see cref="IngestionOptions.FallbackMinTablePageShare"/> of the document. Every other page stays kept-not-processed
    /// with reason "no loss table found" (brief 17: an RFP and a carrier manual were learned as loss runs, whole).
    /// </summary>
    public static List<GraphSegment> FallbackSegments(DocumentGraph parent, GraphSegment other, bool scanned, IngestionOptions options, Glossary glossary)
    {
        double Score(GraphTable t) => scanned ? TolerantHeaderResolution(t, glossary) : HeaderResolution(t);
        var qualifying = parent.Tables
            .Where(t => t.Page >= other.StartPage && t.Page <= other.EndPage && Score(t) >= options.ReviewThreshold
                && Math.Round(Score(t) * NonEmptyHeaders(t)) >= options.FallbackMinResolvedHeaders)
            .ToList();
        var pages = qualifying.Select(t => t.Page).Distinct().Order().ToList();
        // A loss table continues across pages (fax c2-013: page 2 repeats page 1's 13-column claim table under OCR-garbled
        // headers): a table as wide as one on the previous qualifying page continues it.
        for (var page = other.StartPage + 1; page <= other.EndPage; page++)
        {
            var widths = parent.Tables.Where(t => t.Page == page - 1).Select(t => t.ColumnCount).ToHashSet();
            if (pages.Contains(page - 1) && !pages.Contains(page) && parent.Tables.Any(t => t.Page == page && widths.Contains(t.ColumnCount)))
            {
                pages.Add(page);
                pages.Sort();
            }
        }

        var total = other.EndPage - other.StartPage + 1;
        var dense = WordsPerPage(parent, other) >= options.FallbackMinWordsPerPage;
        if (pages.Count == 0 || !dense || pages.Count < options.FallbackMinTablePageShare * total)
        {
            other.Reason = pages.Count == 0 || !dense
                ? "no loss table found"
                : string.Create(CultureInfo.InvariantCulture,
                    $"no loss table found: loss-run-like tables on {pages.Count} of {total} pages only, below the {options.FallbackMinTablePageShare:P0} the fallback requires (a manual or exhibit, not a loss run)");
            other.DerivedConfidence = qualifying.Select(Score).DefaultIfEmpty(0).Max();
            other.DerivedConfidenceMethod = $"header-resolution{(scanned ? ", OCR-tolerant" : "")}; fallback not applied: {other.Reason}";
            return [other];
        }

        // Contiguous runs of qualifying pages become review segments; the gaps stay "Other", kept with the reason.
        var segments = new List<GraphSegment>();
        void Kept(int from, int to) => segments.Add(new GraphSegment
        {
            Category = other.Category, StartPage = from, EndPage = to, Confidence = other.Confidence, Reason = "no loss table found",
            DerivedConfidenceMethod = "fallback: no qualifying table on these pages",
        });
        var next = other.StartPage;
        foreach (var run in Runs(pages))
        {
            if (run.First > next)
            {
                Kept(next, run.First - 1);
            }

            var tables = qualifying.Where(t => t.Page >= run.First && t.Page <= run.Last).ToList();
            var segment = new GraphSegment
            {
                Category = other.Category, StartPage = run.First, EndPage = run.Last, Confidence = other.Confidence,
                DerivedConfidence = tables.Select(Score).Max(), DerivedDecision = "review",
                DerivedConfidenceMethod = $"header-resolution{(scanned ? ", OCR-tolerant" : "")}; fallback: CU category {other.Category}, loss-run headers and text density on pages {run.First}-{run.Last}",
            };
            MarkSummaryOnly(segment, tables);
            segments.Add(segment);
            next = run.Last + 1;
        }

        if (next <= other.EndPage)
        {
            Kept(next, other.EndPage);
        }

        return segments;
    }

    private static IEnumerable<(int First, int Last)> Runs(List<int> pages)
    {
        for (var i = 0; i < pages.Count;)
        {
            var j = i;
            while (j + 1 < pages.Count && pages[j + 1] == pages[j] + 1)
            {
                j++;
            }

            yield return (pages[i], pages[j]);
            i = j + 1;
        }
    }

    private static int NonEmptyHeaders(GraphTable table) => table.HeaderTexts.Count(text => text.Trim().Length > 0);

    private static readonly string[] ClaimIdentity = ["claim.claimNumber", "claim.claimantName", "claim.dateOfInjury"];

    /// <summary>
    /// ADR 0017: a segment whose loss tables print period rows (a fiscal/report/accident year column or counts by status)
    /// and no claim identity column (claim number, claimant, date of injury) is marked summary-only for the learn path.
    /// </summary>
    public static void MarkSummaryOnly(GraphSegment segment, IReadOnlyList<GraphTable> tables)
    {
        var fields = tables.SelectMany(t => t.HeaderFields).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var periodRows = fields.Contains("period.periodLabel") || fields.Any(f => f.EndsWith("Count", StringComparison.Ordinal));
        var money = fields.Any(f => f.StartsWith("claim.", StringComparison.Ordinal) && (f.EndsWith(".paid", StringComparison.Ordinal) || f.EndsWith(".incurred", StringComparison.Ordinal)
            || f.EndsWith(".reserve", StringComparison.Ordinal)));
        if (periodRows && money && !fields.Overlaps(ClaimIdentity))
        {
            segment.SummaryOnly = true;
            segment.DerivedConfidenceMethod += "; summary-only (period rows, no claim identity column)";
        }
    }

    private static double WordsPerPage(DocumentGraph graph, GraphSegment segment)
    {
        var pages = Math.Max(1, segment.EndPage - segment.StartPage + 1);
        return (double)graph.Words.Count(w => w.Page >= segment.StartPage && w.Page <= segment.EndPage) / pages;
    }

    /// <summary>Share of a table's non-empty header texts that resolve against the glossary.</summary>
    public static double HeaderResolution(GraphTable table)
    {
        var headers = table.HeaderTexts.Select((text, i) => (text, field: i < table.HeaderFields.Count ? table.HeaderFields[i] : null))
            .Where(h => h.text.Trim().Length > 0)
            .ToList();
        return headers.Count == 0 ? 0 : Math.Round((double)headers.Count(h => h.field is not null) / headers.Count, 4);
    }

    private static string PathOf(DocumentGraph graph) => graph.Engine == "spreadsheet-native" ? "spreadsheet-native" : $"cu:{graph.Engine}";
}
