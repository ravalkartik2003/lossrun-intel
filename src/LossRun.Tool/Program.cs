using System.Text.Json;
using Azure.Storage.Blobs;
using LossRun.Contracts;
using LossRun.Formats;
using LossRun.Formats.Replay;
using LossRun.Ingestion;
using LossRun.Ingestion.ContentUnderstanding;
using LossRun.Ingestion.Metering;
using LossRun.Ingestion.Spreadsheets;
using LossRun.Learning;
using LossRun.Rules;

// lossrun ingest <path> [--no-classify] [--out <dir>] [--replay] [--formats <dir>]
//                [--learn [--registry <dir> | --registry-container <name>] [--carrier <name>] [--image] [--second-opinion]]
// lossrun demo pack --out <dir> [--skip-build] [--label "…"]   offline demo pack (brief 13 addendum)
// lossrun formats keys <file.xlsx|file.csv|graph.json|cu-result.json>
// lossrun formats centroid <formatId> <out-dir> <graph-or-cu.json>...
// Configuration (environment; no secrets in args):
//   LOSSRUN_CU_ENDPOINT            Content Understanding endpoint (default: the POC Foundry resource)
//   LOSSRUN_AOAI_ENDPOINT          Azure OpenAI endpoint for embeddings (default: the POC Foundry resource)
//   LOSSRUN_EMBEDDING_MODEL        embedding deployment (Terraform output embedding_model; default from config/models.json)
//   LOSSRUN_LEARN_MODEL[_VERSION], LOSSRUN_DELTA_MODEL[_VERSION]  learn/delta deployments (default from config/models.json)
//   LOSSRUN_STORAGE_BLOB_ENDPOINT  when set, raw results and usage.jsonl go to <endpoint>/extractions (keyless)
//   LOSSRUN_PASSWORDS              ';'-separated PDF/workbook passwords (populate from Key Vault, never commit)
var credential = AzureCredentials.Default();
// Read only by the commands that call Azure OpenAI (a missing setting must not break the others).
var aoai = new Lazy<Uri>(() => Settings.RequiredUri("LOSSRUN_AOAI_ENDPOINT", "the Azure OpenAI endpoint: terraform output ai_endpoint"));
var learnOptions = LearnOptions.FromConfiguration() with { IncludePageImage = args.Contains("--image") };
var embeddingModel = learnOptions.EmbeddingModel!;

return args switch
{
    ["ingest", var path, ..] => await IngestAsync(path),
    ["eval", .. var rest] => await LossRun.Evaluation.EvalCli.RunAsync(rest),
    ["tune", ..] when Option("--corpus") is { } tuneCorpus && Option("--truth") is { } tuneTruth && Option("--out") is { } tuneOut =>
        await LossRun.Evaluation.Tune.RunAsync(tuneCorpus, tuneTruth, tuneOut, Option("--mode") ?? "local",
            Option("--budget") is { } b ? decimal.Parse(b, System.Globalization.CultureInfo.InvariantCulture) : null, Option("--seal"), Option("--tuning-items"), Option("--cu-cache"),
            Option("--config")),
    ["chat", .. var rest] => await LossRun.Evaluation.ChatCli.RunAsync(rest),
    ["admin", .. var rest] => await LossRun.Tool.Admin.RunAsync(rest),
    ["demo", "pack", .. var rest] => await LossRun.Tool.DemoPack.RunAsync(rest),
    ["formats", "keys", var path] => Keys(path),
    ["formats", "centroid", var formatId, var outDir, .. var graphs] when graphs.Length > 0 => await CentroidAsync(formatId, outDir, graphs),
    ["formats", "reset", ..] when args.Contains("--learned") && Option("--reason") is { Length: > 0 } reason => await ResetAsync(reason, args.Contains("--dry-run")),
    ["formats", "unretire", var formatId, var version, ..] when int.TryParse(version, out var v) && Option("--reason") is { Length: > 0 } why =>
        await PostFormatAsync($"formats/{Uri.EscapeDataString(formatId)}/versions/{v}/unretire", new { reason = why }),
    ["glossary", "export", ..] when args.Contains("--promote") => await ExportAliasesAsync(Option("--out") ?? "glossary.promote.json"),
    ["glossary", "export", ..] when Option("--out") is { } exportPath => await ExportAliasesAsync(exportPath),
    ["report", ..] when (Option("--quote") is not null || Option("--payload") is not null) && Option("--out") is { } memoPath => await ReportAsync(memoPath),
    _ => Usage(),
};

int Usage()
{
    Console.Error.WriteLine("usage: lossrun ingest <path> [--no-classify] [--out <dir>] [--replay] [--formats <dir>]");
    Console.Error.WriteLine("                      [--learn [--registry <dir> | --registry-container <name>] [--carrier <name>] [--image] [--second-opinion]]");
    Console.Error.WriteLine("       lossrun formats keys <file.xlsx|file.csv|graph.json|cu-result.json>");
    Console.Error.WriteLine("       lossrun formats centroid <formatId> <out-dir> <graph-or-cu.json>...");
    Console.Error.WriteLine("       lossrun formats reset --learned --reason <text> [--dry-run] [--base <api>]   (Admin; retires every learned version; audited)");
    Console.Error.WriteLine("       lossrun formats unretire <formatId> <version> --reason <text> [--base <api>]   (Engineer, Admin; puts a retired version back in matching; audited)");
    Console.Error.WriteLine("       lossrun report (--quote <id> [--base <api>] | --payload <quote-payload.json>) --out memo.docx [--pdf]   (pricing memo + memo.json; --pdf via LibreOffice headless)");
    Console.Error.WriteLine("       lossrun glossary export (--registry <dir> | --run <eval-run dir> | --base <api>) --out glossary.learned.json   (ADR 0019 learned aliases, for review)");
    Console.Error.WriteLine("       lossrun glossary export (--registry | --run | --base) --promote [--min-weeks 4] [--min-documents 10] [--out glossary.promote.json]   (proposal + PR text; never edits glossary.json)");
    Console.Error.WriteLine("       lossrun chat init-index | reindex (--uploads <ids> | --from-api) | ask --scope … --question …");
    Console.Error.WriteLine("       lossrun tune --corpus <tuning set> --truth <truth.csv> --out <tune/batch-NN> [--mode local|api|stub] [--budget usd] [--seal seal-manifest.json] [--tuning-items <dir>] [--cu-cache <dir>]   (brief 27: one tuning batch → batch-report.md)");
    return 2;
}

async Task<int> IngestAsync(string path)
{
    var outDir = Option("--out");
    var options = new IngestionOptions
    {
        ContentUnderstandingEndpoint = Settings.RequiredUri("LOSSRUN_CU_ENDPOINT", "the Content Understanding endpoint: terraform output content_understanding_endpoint"),
        ClassifyBundles = !args.Contains("--no-classify"),
        Passwords = (Environment.GetEnvironmentVariable("LOSSRUN_PASSWORDS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries),
    };

    var usage = new InMemoryCostMeter();
    ICostMeter meter = usage;
    IRawResultStore store = new InMemoryRawResultStore(outDir is null ? null : Path.Combine(outDir, "raw"));
    if (Environment.GetEnvironmentVariable("LOSSRUN_STORAGE_BLOB_ENDPOINT") is { Length: > 0 } blobEndpoint)
    {
        var container = new BlobServiceClient(new Uri(blobEndpoint), credential).GetBlobContainerClient("extractions");
        store = new BlobRawResultStore(container);
        meter = new CompositeCostMeter(usage, new BlobJsonlCostMeter(container));
    }

    var pipeline = new IngestionPipeline(new ContentUnderstandingService(options.ContentUnderstandingEndpoint!, credential), meter, store,
        PriceTable.Embedded, CanonicalData.Glossary, options);
    var sourceBytes = await File.ReadAllBytesAsync(path);
    var result = await pipeline.IngestAsync(Path.GetFileName(path), sourceBytes);

    foreach (var graph in result.Documents)
    {
        var range = graph.SourceFile.FirstPage is { } first ? $" pages {first}-{graph.SourceFile.LastPage} of parent {graph.SourceFile.ParentDocumentId}" : "";
        Console.WriteLine($"{graph.DocumentId}  {graph.SourceFile.FileName} [{graph.SourceFile.Kind}{(graph.SourceFile.WasEncrypted ? ", was encrypted" : "")}]{range}");
        Console.WriteLine($"  engine {graph.Engine} · {graph.Pages.Count} page(s) · {graph.Tables.Count} table(s) · {graph.Cells.Count} cells · {graph.Words.Count} words · {graph.Paragraphs.Count} paragraphs");
        foreach (var table in graph.Tables)
        {
            Console.WriteLine($"  table {table.Id} p{table.Page}{(table.SheetName is null ? "" : $" '{table.SheetName}' {table.Range}")}: {table.RowCount}x{table.ColumnCount}, header rows [{string.Join(",", table.HeaderRows)}], {table.HeaderFields.Count(f => f is not null)}/{table.HeaderTexts.Count} headers resolved");
        }

        foreach (var segment in graph.Segments)
        {
            Console.WriteLine($"  segment {segment.Category} pages {segment.StartPage}-{segment.EndPage} confidence {segment.Confidence?.ToString("0.00") ?? "n/a"} derived {segment.DerivedConfidence?.ToString("0.00") ?? "n/a"} ({segment.DerivedConfidenceMethod}){(segment.DerivedDecision is null ? "" : $" {segment.DerivedDecision}")}{(segment.ChildDocumentId is null ? "" : $" → {segment.ChildDocumentId}")}");
        }

        if (outDir is not null)
        {
            Directory.CreateDirectory(outDir);
            await File.WriteAllTextAsync(Path.Combine(outDir, $"{graph.DocumentId}.graph.json"), JsonSerializer.Serialize(graph, CanonicalJson.Options));
        }
    }

    foreach (var decision in result.ClassifierDecisions)
    {
        Console.WriteLine($"classifier {(decision.Skipped ? "skipped" : "used")} for {decision.DocumentId}: {decision.Reason}");
    }

    foreach (var unsupported in result.Unsupported)
    {
        Console.WriteLine($"UNSUPPORTED {unsupported.FileName} [{unsupported.Kind}]: {unsupported.Reason}");
    }

    foreach (var call in result.Calls)
    {
        Console.WriteLine($"call {call.AnalyzerId} ({call.Stage}) {call.Latency.TotalSeconds:0.0}s · {string.Join(", ", call.Usage.Select(u => $"{u.Meter}{(u.Service == "AzureOpenAI" ? $"[{u.ModelOrAnalyzer}]" : "")}={u.Quantity}"))}");
    }

    if (args.Contains("--replay") || args.Contains("--learn"))
    {
        var handSpecs = new InMemoryFormatRegistry();
        var formatsDir = Option("--formats") ?? FindUp("src/TestShared/formats");
        var loaded = formatsDir is null ? 0 : await FormatDirectory.LoadIntoAsync(formatsDir, handSpecs);
        IFormatRegistry registry = handSpecs;
        var submission = Option("--carrier") is { } carrier ? new SubmissionContext(carrier, null, "underwriter") : null;
        var embeddings = new AzureOpenAiEmbeddingService(aoai.Value, credential, embeddingModel, usage, options.Region, options.ModelDeploymentType);
        LearningPipeline? learning = null;
        if (args.Contains("--learn"))
        {
            // Learned specs persist in a writable layer over the checked-in hand specs, so a second run replays them.
            IFormatRegistry top = Environment.GetEnvironmentVariable("LOSSRUN_STORAGE_BLOB_ENDPOINT") is { Length: > 0 } formatsBlob && Option("--registry") is null
                ? new BlobFormatRegistry(new BlobServiceClient(new Uri(formatsBlob), credential).GetBlobContainerClient(Option("--registry-container") ?? "formats"))
                : new DirectoryFormatRegistry(Option("--registry") ?? Path.Combine(".lossrun", "formats"));
            registry = new LayeredFormatRegistry(handSpecs, top);
            var model = new AzureOpenAiLearnModel(aoai.Value, credential, meter, options.ModelDeploymentType, options.Region);
            var learner = new FormatLearner(model, registry, learnOptions, embeddings);
            var secondOpinion = new SecondOpinionOcr(aoai.Value, credential, meter, options.Region, enabled: args.Contains("--second-opinion"));
            learning = new LearningPipeline(registry, embeddings, learner, new FormatRepairer(model, learner, learnOptions), secondOpinion);
            Console.WriteLine($"learn model {learnOptions.LearnDeployment}@{learnOptions.LearnModelVersion} · delta model {learnOptions.DeltaDeployment}@{learnOptions.DeltaModelVersion} · prompt {learnOptions.PromptVersion} · page image {(learnOptions.IncludePageImage ? "on" : "off")}");
        }

        Console.WriteLine($"format registry: {loaded} hand spec(s) from {formatsDir ?? "(none)"}; {(await registry.GetIndexAsync()).Count} version(s) visible");
        var memory = new FormatMemory(registry, embeddings);
        foreach (var graph in result.Documents.Where(IsLossRun))
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            FingerprintMatch match;
            ReplayResult? replay;
            RoutingDecision decision;
            if (learning is not null)
            {
                var learned = await learning.ProcessAsync(graph, result.RunId, DateTimeOffset.UtcNow, submission, sourcePdf: graph.SourceFile.Kind == FileKind.Pdf ? sourceBytes : null);
                (match, replay, decision) = (learned.Match, learned.Replay, learned.Decision);
                if (learned.Learn is { } outcome)
                {
                    foreach (var attempt in outcome.Attempts)
                    {
                        Console.WriteLine($"[{graph.DocumentId}] {attempt.Stage} #{attempt.Iteration}: {attempt.Response.Latency.TotalSeconds:0.0}s · input {attempt.Response.InputTokens} (cached {attempt.Response.CachedInputTokens}) · output {attempt.Response.OutputTokens} (reasoning {attempt.Response.ReasoningTokens}) · {attempt.Verification?.Summary ?? attempt.Error}");
                    }

                    Console.WriteLine($"[{graph.DocumentId}] {learned.FinalTier}: {string.Join("; ", outcome.Reasons)}");
                    Console.WriteLine(outcome.Published
                        ? $"[{graph.DocumentId}] published {outcome.Spec!.Ref} ({outcome.Spec.CreatedBy}{(outcome.Spec.Supersedes is null ? "" : $", supersedes {outcome.Spec.Supersedes}")}) after {outcome.ModelCalls} model call(s), {outcome.Elapsed.TotalSeconds:0.0}s"
                        : $"[{graph.DocumentId}] NOT published — routed to review with the best attempt");
                    if (outDir is not null && outcome.Spec is not null)
                    {
                        Directory.CreateDirectory(outDir);
                        await File.WriteAllTextAsync(Path.Combine(outDir, $"{graph.DocumentId}.{(outcome.Published ? "published" : "best-attempt")}.spec.json"), FormatJson.Write(outcome.Spec));
                    }
                }
            }
            else
            {
                (match, replay, decision) = await memory.ProcessAsync(graph, result.RunId, DateTimeOffset.UtcNow, submission: submission);
            }

            stopwatch.Stop();
            Console.WriteLine($"[{graph.DocumentId}] fingerprint {match.Tier} score {match.Score:0.0000}{(match.Drift ? " DRIFT" : "")} → {match.Format?.Ref ?? "none"}{(match.Similarities.Count > 0 ? $" (cosines: {string.Join(", ", match.Similarities.Select(s => $"{s.Key} {s.Value:0.0000}"))})" : "")}");
            Console.WriteLine($"[{graph.DocumentId}] routing {decision.Tier}: {string.Join("; ", decision.Reasons)}");
            Console.WriteLine($"[{graph.DocumentId}] badge: {decision.Badge}");
            if (replay is not null)
            {
                var failures = replay.RuleOutcomes.Where(o => o.Status == RuleStatus.Fail).ToList();
                Console.WriteLine($"[{graph.DocumentId}] replay {replay.FormatRef}: {replay.Stats.ClaimRows} claims, {replay.Stats.SubtotalRows} subtotal rows, {replay.Stats.GrandTotalRows} grand-total rows, {replay.Stats.LineFallbackBlocks} line-fallback block(s); field confidence min {replay.MinFieldConfidence:0.000} mean {replay.MeanFieldConfidence:0.000}; {stopwatch.Elapsed.TotalMilliseconds:0} ms incl. fingerprint");
                Console.WriteLine($"[{graph.DocumentId}] unmapped headers: {(replay.UnmappedHeaders.Count == 0 ? "none" : string.Join(", ", replay.UnmappedHeaders))}");
                Console.WriteLine($"[{graph.DocumentId}] rules: {replay.RuleOutcomes.Count(o => o.Status == RuleStatus.Pass)} pass, {failures.Count(f => f.Severity == RuleSeverity.Hard)} hard fail, {failures.Count(f => f.Severity == RuleSeverity.Soft)} soft fail, {failures.Count(f => f.Severity == RuleSeverity.Flag)} flag, {replay.RuleOutcomes.Count(o => o.Status == RuleStatus.NotApplicable)} n/a");
                foreach (var failure in failures)
                {
                    Console.WriteLine($"    {failure.RuleId} {failure.Severity}: {failure.Message}");
                }

                foreach (var issue in replay.Issues)
                {
                    Console.WriteLine($"    issue: {issue}");
                }

                if (outDir is not null)
                {
                    await File.WriteAllTextAsync(Path.Combine(outDir, $"{graph.DocumentId}.replay.json"), replay.ToJson());
                }
            }
        }
    }

    var costs = result.Documents.Where(d => d.SourceFile.FirstPage is null)
        .Select(d => CostCalculator.Compute(d.DocumentId, result.RunId, d.Engine == "spreadsheet-native" ? "spreadsheet-native" : $"cu:{d.Engine}", usage.Events, PriceTable.Embedded));
    foreach (var cost in costs)
    {
        var label = cost.Path switch
        {
            "spreadsheet-native" => "spreadsheet (no Azure call)",
            "cu:prebuilt-layout" => "CU layout",
            _ => $"CU {cost.Path[3..]}",
        };
        var embeddingNote = cost.EmbeddingCalls > 0 ? $" · {cost.EmbeddingCalls} embedding call{(cost.EmbeddingCalls == 1 ? "" : "s")}" : "";
        Console.WriteLine($"cost {cost.DocumentId}: {CostCalculator.Summary(cost, label)}{embeddingNote}");
    }

    return 0;
}

// Sample UW report package (brief 10 §6): quote payload v2 from the API (or a saved file) → memo.docx + memo.json.
async Task<int> ReportAsync(string memoPath)
{
    string json;
    if (Option("--payload") is { } file)
    {
        json = await File.ReadAllTextAsync(file);
    }
    else
    {
        using var http = ApiClient();
        var response = await http.GetAsync($"{ApiBase()}/quotes/{Uri.EscapeDataString(Option("--quote")!)}/quote-payload");
        json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"{(int)response.StatusCode} {json}");
            return 1;
        }

        if (Option("--save-payload") is { } save)
        {
            await File.WriteAllTextAsync(save, json);
        }
    }

    var payload = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(memoPath))!);
    var memo = LossRun.Analytics.Memo.Write(payload, memoPath);
    Console.WriteLine($"memo: {memo["title"]} → {memoPath} (+ {Path.ChangeExtension(memoPath, ".json")})");
    if (args.Contains("--pdf"))
    {
        return MemoPdf(memoPath);
    }

    return 0;
}

// Report 10 answer 3 / docs/analytics/memo-pdf.md: Word stays the working copy; the final package adds a PDF rendered by
// LibreOffice headless (MPL-2.0, run as a separate process, nothing linked). LOSSRUN_SOFFICE overrides the binary.
int MemoPdf(string docxPath)
{
    var soffice = Environment.GetEnvironmentVariable("LOSSRUN_SOFFICE") is { Length: > 0 } configured ? configured
        : OperatingSystem.IsWindows() && File.Exists(@"C:\Program Files\LibreOffice\program\soffice.exe") ? @"C:\Program Files\LibreOffice\program\soffice.exe"
        : "soffice";
    var outDir = Path.GetDirectoryName(Path.GetFullPath(docxPath))!;
    var start = new System.Diagnostics.ProcessStartInfo(soffice, ["--headless", "--norestore", "--convert-to", "pdf", "--outdir", outDir, Path.GetFullPath(docxPath)])
    {
        RedirectStandardOutput = true, RedirectStandardError = true,
    };
    try
    {
        using var process = System.Diagnostics.Process.Start(start)!;
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)) || process.ExitCode != 0)
        {
            Console.Error.WriteLine($"pdf: soffice failed ({process.StandardError.ReadToEnd().Trim()})");
            return 3;
        }
    }
    catch (System.ComponentModel.Win32Exception)
    {
        Console.Error.WriteLine("pdf: LibreOffice (soffice) not found — install it or set LOSSRUN_SOFFICE; or convert at the client (docs/analytics/memo-pdf.md).");
        return 3;
    }

    var pdf = Path.ChangeExtension(Path.GetFullPath(docxPath), ".pdf");
    Console.WriteLine($"pdf: {pdf}");
    return File.Exists(pdf) ? 0 : 3;
}

string ApiBase() => (Option("--base") ?? Settings.Required("LOSSRUN_API_BASE", "the API base, https://<function app host>/api/v1: terraform output function_app_hostname")).TrimEnd('/');

HttpClient ApiClient()
{
    var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    if (Environment.GetEnvironmentVariable("LOSSRUN_API_TOKEN") is { Length: > 0 } token)
    {
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }
    else
    {
        var user = Environment.GetEnvironmentVariable("LOSSRUN_API_USER") ?? throw new InvalidOperationException("Set LOSSRUN_API_USER/LOSSRUN_API_PASSWORD or LOSSRUN_API_TOKEN.");
        var basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{Environment.GetEnvironmentVariable("LOSSRUN_API_PASSWORD")}"));
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basic);
    }

    return http;
}

// Pre-bake-off reset (08c Answers 1): POST /formats/reset on the deployed API as an Admin (LOSSRUN_API_USER/PASSWORD, Basic on
// the demo host, or LOSSRUN_API_TOKEN). Run it the day before the blind-set run, not before; --dry-run lists what it would retire.
async Task<int> ResetAsync(string reason, bool dryRun)
{
    var baseUrl = (Option("--base") ?? Settings.Required("LOSSRUN_API_BASE", "the API base, https://<function app host>/api/v1: terraform output function_app_hostname")).TrimEnd('/');
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    if (Environment.GetEnvironmentVariable("LOSSRUN_API_TOKEN") is { Length: > 0 } token)
    {
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }
    else
    {
        var user = Environment.GetEnvironmentVariable("LOSSRUN_API_USER") ?? throw new InvalidOperationException("Set LOSSRUN_API_USER/LOSSRUN_API_PASSWORD (Admin) or LOSSRUN_API_TOKEN.");
        var basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{Environment.GetEnvironmentVariable("LOSSRUN_API_PASSWORD")}"));
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basic);
    }

    var response = await http.PostAsync($"{baseUrl}/formats/reset",
        new StringContent(System.Text.Json.JsonSerializer.Serialize(new { reason, dryRun }), System.Text.Encoding.UTF8, "application/json"));
    Console.WriteLine($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    return response.IsSuccessStatusCode ? 0 : 1;
}

// Brief 30 item 17: an engineer's explicit, audited un-retire (POST /formats/{id}/versions/{v}/unretire), same credentials as reset.
async Task<int> PostFormatAsync(string path, object body)
{
    var baseUrl = (Option("--base") ?? Settings.Required("LOSSRUN_API_BASE", "the API base, https://<function app host>/api/v1: terraform output function_app_hostname")).TrimEnd('/');
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(1) };
    if (Environment.GetEnvironmentVariable("LOSSRUN_API_TOKEN") is { Length: > 0 } token)
    {
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }
    else
    {
        var user = Environment.GetEnvironmentVariable("LOSSRUN_API_USER") ?? throw new InvalidOperationException("Set LOSSRUN_API_USER/LOSSRUN_API_PASSWORD or LOSSRUN_API_TOKEN.");
        var basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{Environment.GetEnvironmentVariable("LOSSRUN_API_PASSWORD")}"));
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basic);
    }

    var response = await http.PostAsync($"{baseUrl}/{path}",
        new StringContent(System.Text.Json.JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"));
    Console.WriteLine($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    return response.IsSuccessStatusCode ? 0 : 1;
}

async Task<int> ExportAliasesAsync(string outPath)
{
    LearnedGlossary glossary;
    string source;
    var policy = AliasPolicy.Default; // brief 29 Answers 4: hand formats never count, read from the same source as the aliases
    if (Option("--registry") is { } dir)
    {
        var registry = new DirectoryFormatRegistry(dir);
        (glossary, source) = (await registry.GetLearnedGlossaryAsync(), $"registry {Path.GetFileName(Path.GetFullPath(dir))}");
        policy = await policy.WithRegistryAsync(registry);
    }
    else if (Option("--run") is { } run)
    {
        var runJson = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run, "run.json"))).RootElement;
        var aliases = runJson.TryGetProperty("learnedAliasesAtEnd", out var at) && at.ValueKind == JsonValueKind.Object
            ? at.Deserialize<LearnedGlossary>(CanonicalJson.Options)! : new LearnedGlossary();
        if (runJson.TryGetProperty("registryAtEnd", out var reg) && reg.ValueKind == JsonValueKind.Array)
        {
            policy = policy.WithHandFormats(reg.EnumerateArray().Where(f => f.GetProperty("createdBy").GetString() == "hand").Select(f => f.GetProperty("formatId").GetString()!));
        }
        (glossary, source) = (aliases.Upgrade(), $"eval run {Path.GetFileName(Path.GetFullPath(run))}");
    }
    else
    {
        var baseUrl = (Option("--base") ?? Settings.Required("LOSSRUN_API_BASE", "the API base, https://<function app host>/api/v1: terraform output function_app_hostname")).TrimEnd('/');
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        if (Environment.GetEnvironmentVariable("LOSSRUN_API_TOKEN") is { Length: > 0 } token)
        {
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        var mappings = JsonSerializer.Deserialize<List<LearnedMapping>>(await http.GetStringAsync($"{baseUrl}/glossary/aliases"), CanonicalJson.Options)!;
        var formats = JsonDocument.Parse(await http.GetStringAsync($"{baseUrl}/formats")).RootElement;
        policy = policy.WithHandFormats(formats.EnumerateArray().Where(f => f.GetProperty("createdBy").GetString() == "hand").Select(f => f.GetProperty("formatId").GetString()!));
        (glossary, source) = (new LearnedGlossary { Mappings = mappings }, "api");
    }

    if (args.Contains("--promote"))
    {
        // Brief 29 §2: a proposal and a PR description; glossary.json itself is never written.
        var seedPath = FindUpFile("docs/evaluation/glossary.json") ?? throw new FileNotFoundException("docs/evaluation/glossary.json (run inside the repository)");
        var (weeks, minDocs) = (Option("--min-weeks") is { } w ? int.Parse(w, System.Globalization.CultureInfo.InvariantCulture) : 4,
            Option("--min-documents") is { } d ? int.Parse(d, System.Globalization.CultureInfo.InvariantCulture) : 10);
        var (promote, skipped) = GlossaryPromotion.Select(glossary, await File.ReadAllTextAsync(seedPath), DateTimeOffset.UtcNow, weeks, minDocs, policy);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        await File.WriteAllTextAsync(outPath, GlossaryPromotion.Json(promote, skipped, source, DateTimeOffset.UtcNow, weeks, minDocs));
        var prPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!, Path.GetFileNameWithoutExtension(outPath) + ".pr.md");
        await File.WriteAllTextAsync(prPath, GlossaryPromotion.PullRequest(promote, source, weeks, minDocs));
        Console.WriteLine($"{promote.Count} alias(es) proposed for glossary.json, {skipped.Count} active one(s) skipped → {outPath}, {prPath} (glossary.json not changed)");
        return 0;
    }

    await File.WriteAllTextAsync(outPath, LearnedGlossaryExport.Json(glossary, source, DateTimeOffset.UtcNow));
    Console.WriteLine($"{glossary.Mappings.Count} learned alias(es) → {outPath} ({string.Join(", ", glossary.Mappings.GroupBy(m => m.Status).Select(g => $"{g.Count()} {g.Key}"))})");
    return 0;
}

int Keys(string path)
{
    var graph = LoadGraph(path);
    var fp = FingerprintText.Compute(graph);
    Console.WriteLine(JsonSerializer.Serialize(new { fp.FileKind, fp.IdentityKey, fp.HeaderKey, fp.IdentityText, fp.HeaderText, embeddingText = FingerprintText.ForEmbedding(graph) },
        new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

async Task<int> CentroidAsync(string formatId, string outDir, string[] graphs)
{
    var meter = new InMemoryCostMeter();
    var service = new AzureOpenAiEmbeddingService(aoai.Value, credential, embeddingModel, meter, "eastus2", "DataZoneStandard");
    var vectors = new List<IReadOnlyList<float>>();
    var ids = new List<string>();
    foreach (var path in graphs)
    {
        var graph = LoadGraph(path);
        var embedding = await service.EmbedAsync(FingerprintText.ForEmbedding(graph), graph.DocumentId, "centroid", CancellationToken.None);
        vectors.Add(embedding.Vector);
        ids.Add(graph.DocumentId);
        Console.WriteLine($"embedded {path}: {embedding.Vector.Length} dims, {embedding.Usage[0].Quantity} tokens, {embedding.Latency.TotalMilliseconds:0} ms");
    }

    var centroid = new FormatCentroid { FormatId = formatId, Model = embeddingModel, ExemplarCount = vectors.Count, ExemplarDocumentIds = ids, Vector = Vectors.Centroid(vectors) };
    Directory.CreateDirectory(outDir);
    await File.WriteAllTextAsync(Path.Combine(outDir, "centroid.json"), JsonSerializer.Serialize(centroid, CanonicalJson.Options));
    Console.WriteLine($"wrote {Path.Combine(outDir, "centroid.json")}");
    return 0;
}

DocumentGraph LoadGraph(string path)
{
    var bytes = File.ReadAllBytes(path);
    var kind = FileSniffer.Sniff(bytes, path);
    if (FileSniffer.IsSpreadsheet(kind))
    {
        return SpreadsheetReader.Read(bytes, new SourceFile { FileName = Path.GetFileName(path), Kind = kind }, "doc-" + Path.GetFileNameWithoutExtension(path),
            CanonicalData.Glossary, []);
    }

    var json = JsonDocument.Parse(bytes).RootElement;
    return json.TryGetProperty("result", out _)
        ? CuResultMapper.Map(json, "doc-" + Path.GetFileNameWithoutExtension(path), new SourceFile { FileName = Path.GetFileName(path), Kind = FileKind.Pdf }, "prebuilt-layout", CanonicalData.Glossary)
        : JsonSerializer.Deserialize<DocumentGraph>(bytes, CanonicalJson.Options)!;
}

static bool IsLossRun(DocumentGraph graph) =>
    graph.Segments.Count == 0 || graph.SourceFile.FirstPage is not null; // single documents, and loss-run segments of bundles

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string? FindUp(string relative)
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, relative);
        if (Directory.Exists(candidate))
        {
            return candidate;
        }
    }

    return null;
}

static string? FindUpFile(string relative)
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
