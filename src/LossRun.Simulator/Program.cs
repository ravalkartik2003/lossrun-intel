using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LossRun.Api.Contracts;
using LossRun.Contracts;

// Synthetic submission-engine simulator (brief 07 §3): posts N uploads concurrently to the API, follows each to
// completion and prints time-to-first-claim, time-to-complete and metered cost (p50/p95). Input to brief 11's load test.
//
// lossrun-sim [--base https://<app>.azurewebsites.net/api/v1] [--concurrency 5] [--fixtures dir] [--files a.pdf,b.csv,...]
//             [--carrier "<hint>"] [--quote Q-1] [--repeat-key] [--sse] [--steps] [--out results.json]
// --steps: follows every upload's SSE stream and reports per document the server-side time spent waiting for a learn slot
// (learn-queued → learn) and in the learn itself (brief 08d burst test of the LearnGate cap).
// Credentials (Basic on the demo host): LOSSRUN_API_USER / LOSSRUN_API_PASSWORD; or LOSSRUN_API_TOKEN (Entra bearer).
var baseUrl = (Option("--base") ?? Environment.GetEnvironmentVariable("LOSSRUN_API_BASE")
    ?? throw new InvalidOperationException("Set --base or LOSSRUN_API_BASE (https://<function app host>/api/v1: terraform output function_app_hostname).")).TrimEnd('/');
var concurrency = int.Parse(Option("--concurrency") ?? "5", CultureInfo.InvariantCulture);
var fixturesDir = Option("--fixtures") ?? FindUp("src/TestShared/fixtures") ?? ".";
string[] mix = (Option("--files") ?? "lossrun-native.pdf,lossrun-scanned.pdf,lossrun-stacked.xlsx,lossrun-export.csv,bundle.pdf").Split(',');
var runKey = args.Contains("--repeat-key") ? "fixed" : Guid.NewGuid().ToString("N")[..8];

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
if (Environment.GetEnvironmentVariable("LOSSRUN_API_TOKEN") is { Length: > 0 } token)
{
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
}
else
{
    var user = Environment.GetEnvironmentVariable("LOSSRUN_API_USER") ?? throw new InvalidOperationException("Set LOSSRUN_API_USER/LOSSRUN_API_PASSWORD or LOSSRUN_API_TOKEN.");
    var password = Environment.GetEnvironmentVariable("LOSSRUN_API_PASSWORD") ?? "";
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
}

Console.WriteLine($"simulator: {concurrency} concurrent upload(s) → {baseUrl} (run {runKey})");
var jobs = Enumerable.Range(0, concurrency).Select(i => mix[i % mix.Length]).ToList();
var results = await Task.WhenAll(jobs.Select((file, i) => RunAsync(file, i)));

Console.WriteLine();
Console.WriteLine($"{"file",-24} {"upload",-24} {"status",-16} {"1st claim s",11} {"complete s",10} {"server 1st",10} {"server done",11} {"cost $",9} {"LLM",4}  tiers");
foreach (var r in results)
{
    Console.WriteLine($"{r.File,-24} {r.UploadId,-24} {r.Status,-16} {Sec(r.FirstClaim),11} {Sec(r.Complete),10} {Ms(r.ServerFirstClaimMs),10} {Ms(r.ServerCompletedMs),11} {r.CostUsd,9:0.0000} {r.LlmCalls,4}  {r.Tiers}");
}

Console.WriteLine();
Report("time-to-first-claim (client)", results.Where(r => r.FirstClaim is not null).Select(r => r.FirstClaim!.Value.TotalSeconds));
Report("time-to-complete (client)", results.Where(r => r.Complete is not null).Select(r => r.Complete!.Value.TotalSeconds));
if (args.Contains("--steps"))
{
    var steps = results.SelectMany(r => r.Steps ?? []).ToList();
    Console.WriteLine();
    Console.WriteLine($"{"document",-34} {"queued s",9} {"learn s",8} {"to done s",9} {"done − queue s",14}  steps");
    foreach (var d in steps)
    {
        Console.WriteLine($"{d.DocumentId,-34} {d.QueuedMs / 1000.0,9:0.0} {d.LearnMs / 1000.0,8:0.0} {d.DoneMs / 1000.0,9:0.0} {(d.DoneMs - d.QueuedMs) / 1000.0,14:0.0}  {string.Join(" → ", d.Steps)}");
    }

    Report("queue wait (server)", steps.Select(d => d.QueuedMs / 1000.0));
    Report("document latency excluding the queue (server)", steps.Select(d => (d.DoneMs - d.QueuedMs) / 1000.0));
    var peak = 0;
    foreach (var t in steps.SelectMany(d => new[] { (At: d.LearnStartUtc, Delta: 1), (At: d.LearnEndUtc, Delta: -1) }).Where(e => e.At is not null).OrderBy(e => e.At).ThenBy(e => e.Delta))
    {
        peak = Math.Max(peak, steps.Count(d => d.LearnStartUtc <= t.At && (d.LearnEndUtc ?? DateTimeOffset.MaxValue) > t.At));
    }

    Console.WriteLine($"documents queued for a learn slot: {steps.Count(d => d.QueuedMs > 0)} of {steps.Count}; peak concurrent learns observed (client clock): {peak}");
}
Console.WriteLine($"total metered cost: ${results.Sum(r => r.CostUsd):0.0000} over {results.Length} upload(s); LLM calls {results.Sum(r => r.LlmCalls)}");
if (Option("--out") is { } outPath)
{
    await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(results, new JsonSerializerOptions(CanonicalJson.Options) { WriteIndented = true }));
}

return results.All(r => r.Status is UploadStatuses.Completed or UploadStatuses.AwaitingReview) ? 0 : 1;

async Task<SimResult> RunAsync(string file, int index)
{
    var bytes = await File.ReadAllBytesAsync(Path.Combine(fixturesDir, file));
    using var form = new MultipartFormDataContent();
    var content = new ByteArrayContent(bytes);
    content.Headers.ContentType = new MediaTypeHeaderValue(file.EndsWith(".pdf", StringComparison.Ordinal) ? "application/pdf"
        : file.EndsWith(".csv", StringComparison.Ordinal) ? "text/csv" : "application/octet-stream");
    form.Add(content, "file", file);
    if (Option("--carrier") is { } carrier)
    {
        form.Add(new StringContent(carrier), "carrierHint");
    }

    form.Add(new StringContent(Option("--quote") ?? $"SIM-{runKey}-{index}"), "quoteId");
    using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/uploads") { Content = form };
    request.Headers.Add("Idempotency-Key", $"sim-{runKey}-{index}");

    var stopwatch = Stopwatch.StartNew();
    using var response = await http.SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();
    if ((int)response.StatusCode != 202)
    {
        return new SimResult(file, "-", $"HTTP {(int)response.StatusCode}", null, null, null, null, 0, 0, body[..Math.Min(body.Length, 120)], 0);
    }

    var accepted = JsonSerializer.Deserialize<UploadAccepted>(body, CanonicalJson.Options)!;
    var sseEvents = args.Contains("--sse") && index == 0 ? CountSseAsync(accepted.UploadId) : Task.FromResult(0);
    var traced = args.Contains("--steps") ? TraceStepsAsync(accepted.UploadId) : Task.FromResult<List<DocSteps>>([]);
    TimeSpan? firstClaim = null;
    UploadManifest? manifest = null;
    while (stopwatch.Elapsed < TimeSpan.FromMinutes(8))
    {
        await Task.Delay(250);
        using var poll = await http.GetAsync($"{baseUrl}/uploads/{accepted.UploadId}");
        if (!poll.IsSuccessStatusCode)
        {
            continue;
        }

        manifest = JsonSerializer.Deserialize<UploadManifest>(await poll.Content.ReadAsStringAsync(), CanonicalJson.Options)!;
        if (firstClaim is null && manifest.Items.Any(i => i.ClaimCount > 0 && i.Status == ItemStatuses.Processed))
        {
            firstClaim = stopwatch.Elapsed;
        }

        if (manifest.Status is UploadStatuses.Completed or UploadStatuses.AwaitingReview or UploadStatuses.Failed)
        {
            break;
        }
    }

    var complete = manifest?.Status is UploadStatuses.Completed or UploadStatuses.AwaitingReview ? stopwatch.Elapsed : (TimeSpan?)null;
    var tiers = string.Join(",", manifest?.Items.Where(i => i.RoutingTier is not null).Select(i => i.RoutingTier) ?? []);
    var items = string.Join(",", manifest?.Items.GroupBy(i => i.Status).Select(g => $"{g.Count()} {g.Key}") ?? []);
    return new SimResult(file, accepted.UploadId, manifest?.Status ?? "timeout", firstClaim, complete, manifest?.FirstClaimMs, manifest?.CompletedMs,
        manifest?.Cost?.TotalUsd ?? 0, manifest?.Cost?.LlmCalls ?? 0, $"{tiers} [{items}]", await sseEvents, await traced);
}

// Per document: the first time each progress step was seen (server elapsed ms since the document's orchestration started).
async Task<List<DocSteps>> TraceStepsAsync(string uploadId)
{
    var seen = new Dictionary<string, Dictionary<string, (long Ms, DateTimeOffset At)>>(StringComparer.Ordinal);
    using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/uploads/{uploadId}/events");
    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
    while (await reader.ReadLineAsync() is { } line)
    {
        if (line.StartsWith("event: end", StringComparison.Ordinal))
        {
            break;
        }

        if (!line.StartsWith("data: ", StringComparison.Ordinal) || JsonSerializer.Deserialize<UploadProgress>(line[6..], CanonicalJson.Options) is not { DocumentId: { } doc } p)
        {
            continue;
        }

        if (!seen.TryGetValue(doc, out var steps))
        {
            seen[doc] = steps = new(StringComparer.Ordinal);
        }

        steps.TryAdd(p.Step, (p.ElapsedMs, DateTimeOffset.UtcNow));
    }

    return [.. seen.Select(kv =>
    {
        var s = kv.Value;
        long Ms(string step) => s.TryGetValue(step, out var v) ? v.Ms : 0;
        var learnStep = s.ContainsKey("learn") ? "learn" : "delta";
        var queued = s.ContainsKey($"{learnStep}-queued") ? Ms(learnStep) - Ms($"{learnStep}-queued") : 0;
        return new DocSteps(kv.Key, [.. s.OrderBy(x => x.Value.Ms).Select(x => x.Key)], Math.Max(0, queued), Ms("validate") - Ms(learnStep), Ms("done"),
            s.TryGetValue(learnStep, out var start) ? start.At : null, s.TryGetValue("validate", out var end) ? end.At : null);
    })];
}

async Task<int> CountSseAsync(string uploadId)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/uploads/{uploadId}/events");
    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
    var count = 0;
    while (await reader.ReadLineAsync() is { } line)
    {
        if (line.StartsWith("event: progress", StringComparison.Ordinal))
        {
            count++;
        }
        else if (line.StartsWith("event: end", StringComparison.Ordinal))
        {
            break;
        }
    }

    Console.WriteLine($"SSE {uploadId}: {count} progress event(s) ({response.Content.Headers.ContentType})");
    return count;
}

static void Report(string label, IEnumerable<double> seconds)
{
    var sorted = seconds.Order().ToList();
    if (sorted.Count == 0)
    {
        Console.WriteLine($"{label}: no samples");
        return;
    }

    double P(double q) => sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Count) - 1, 0, sorted.Count - 1)];
    Console.WriteLine($"{label}: p50 {P(0.50):0.0} s · p95 {P(0.95):0.0} s · min {sorted[0]:0.0} s · max {sorted[^1]:0.0} s (n={sorted.Count})");
}

static string Sec(TimeSpan? t) => t is { } v ? v.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) : "-";

static string Ms(long? ms) => ms is { } v ? (v / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) : "-";

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string? FindUp(string relative)
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

internal sealed record SimResult(string File, string UploadId, string Status, TimeSpan? FirstClaim, TimeSpan? Complete, long? ServerFirstClaimMs, long? ServerCompletedMs,
    decimal CostUsd, int LlmCalls, string Tiers, int SseEvents, List<DocSteps>? Steps = null);

/// <param name="QueuedMs">Server time between learn-queued and learn (0 when a slot was free).</param>
internal sealed record DocSteps(string DocumentId, List<string> Steps, long QueuedMs, long LearnMs, long DoneMs, DateTimeOffset? LearnStartUtc, DateTimeOffset? LearnEndUtc);
