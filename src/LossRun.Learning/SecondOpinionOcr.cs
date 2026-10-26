using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using LossRun.Contracts;
using LossRun.Formats.Replay;

namespace LossRun.Learning;

/// <summary>
/// Scanned-table fallback, second opinion (brief 04 Answers 2, brief 06 §5): behind <see cref="Enabled"/> (default
/// false), when a scanned page has no Content Understanding table and the line fallback could not read claim rows, run
/// Document Intelligence <c>prebuilt-layout</c> on that page only and merge its tables into the graph. Metered as its own
/// line (stage <c>secondOpinionOcr</c>, service DocumentIntelligence, meter prebuiltPages).
/// </summary>
public sealed class SecondOpinionOcr(Uri endpoint, TokenCredential credential, ICostMeter meter, string region, bool enabled = false, HttpClient? http = null)
{
    public const string ApiVersion = "2024-11-30";
    public const string Stage = "secondOpinionOcr";
    private static readonly string[] Scopes = ["https://cognitiveservices.azure.com/.default"];
    private readonly HttpClient _http = http ?? new HttpClient();

    public bool Enabled { get; } = enabled;

    /// <summary>
    /// Pages with OCR words but no CU table, when replay (including the line fallback) read no claim rows — or, before
    /// any format is known, every such page. Pages whose rows the line fallback already reads are left alone.
    /// </summary>
    public static IReadOnlyList<int> PagesNeedingSecondOpinion(DocumentGraph graph, ReplayResult? replay)
    {
        if (replay is { Stats.ClaimRows: > 0 })
        {
            return [];
        }

        return graph.Pages.Where(p => graph.Words.Any(w => w.Page == p.Number) && !graph.Tables.Any(t => t.Page == p.Number))
            .Select(p => p.Number).ToList();
    }

    /// <summary>Analyzes one page with Document Intelligence layout and appends its tables (ids <c>di{page}-{n}</c>) to the graph.</summary>
    public async Task<(int TablesAdded, TimeSpan Latency, IReadOnlyList<UsageEvent> Usage)> MergePageAsync(DocumentGraph graph, byte[] pdf, int page, string runId,
        Glossary glossary, CancellationToken cancellationToken = default)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        using var post = new HttpRequestMessage(HttpMethod.Post,
            new Uri(endpoint, $"documentintelligence/documentModels/prebuilt-layout:analyze?api-version={ApiVersion}&pages={page}"))
        {
            Content = JsonContent.Create(new { base64Source = Convert.ToBase64String(pdf) }),
        };
        post.Headers.Authorization = new("Bearer", token.Token);
        using var accepted = await _http.SendAsync(post, cancellationToken).ConfigureAwait(false);
        if (!accepted.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Document Intelligence analyze failed ({(int)accepted.StatusCode}): {await accepted.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)}");
        }

        var operation = accepted.Headers.GetValues("Operation-Location").First();
        JsonDocument result;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            using var get = new HttpRequestMessage(HttpMethod.Get, operation);
            get.Headers.Authorization = new("Bearer", token.Token);
            using var response = await _http.SendAsync(get, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var json = JsonDocument.Parse(body);
            var status = json.RootElement.GetProperty("status").GetString();
            if (status == "succeeded")
            {
                result = json;
                break;
            }

            json.Dispose();
            if (status is "failed" or "canceled")
            {
                throw new InvalidOperationException($"Document Intelligence analyze {status}: {body[..Math.Min(body.Length, 500)]}");
            }
        }

        stopwatch.Stop();
        using (result)
        {
            var added = Merge(graph, result.RootElement.GetProperty("analyzeResult"), page, glossary);
            var usage = new UsageEvent(graph.DocumentId, runId, Stage, "DocumentIntelligence", "prebuiltPages", 1, "page", "prebuilt-layout", null, region,
                DateTimeOffset.UtcNow, operation.Split('/').Last().Split('?')[0]);
            await meter.RecordAsync(usage, cancellationToken).ConfigureAwait(false);
            return (added, stopwatch.Elapsed, [usage]);
        }
    }

    /// <summary>
    /// Maps DI layout tables (same cell model as CU: rowIndex, columnIndex, kind, content, boundingRegions) into graph
    /// tables. A DI table that overlaps a table CU already returned on the page is skipped, so no row is read twice
    /// (measured: merging both made the scan's last period read WC-24-0003 twice, brief 06 report).
    /// </summary>
    internal static int Merge(DocumentGraph graph, JsonElement analyzeResult, int page, Glossary glossary)
    {
        if (!analyzeResult.TryGetProperty("tables", out var tables))
        {
            return 0;
        }

        var existing = graph.Tables.Where(t => t.Page == page && t.Polygon is { Count: >= 8 }).Select(t => VerticalRange(t.Polygon!)).ToList();
        var n = 0;
        var skipped = 0;
        foreach (var table in tables.EnumerateArray())
        {
            if (Polygon(table) is { Count: >= 8 } box && existing.Any(e => Overlap(e, VerticalRange(box)) > 0.5))
            {
                skipped++;
                continue;
            }

            var id = $"di{page}-{n++}";
            var cells = table.GetProperty("cells").EnumerateArray().ToList();
            var columns = table.GetProperty("columnCount").GetInt32();
            var headerCells = cells.Where(c => c.TryGetProperty("kind", out var k) && k.GetString() == "columnHeader").ToList();
            if (headerCells.Count == 0)
            {
                headerCells = cells.Where(c => c.GetProperty("rowIndex").GetInt32() == 0).ToList();
            }

            static int Int(JsonElement e, string name, int fallback) => e.TryGetProperty(name, out var v) ? v.GetInt32() : fallback;
            var headerRows = headerCells.Select(c => c.GetProperty("rowIndex").GetInt32()).Distinct().Order().ToList();
            var headerTexts = Enumerable.Range(0, columns)
                .Select(col => string.Join(" ", headerCells
                    .Where(c => col >= c.GetProperty("columnIndex").GetInt32() && col < c.GetProperty("columnIndex").GetInt32() + Int(c, "columnSpan", 1))
                    .OrderBy(c => c.GetProperty("rowIndex").GetInt32())
                    .Select(c => c.GetProperty("content").GetString()!.Trim())
                    .Where(t => t.Length > 0)))
                .ToList();
            graph.Tables.Add(new GraphTable
            {
                Id = id,
                Page = page,
                RowCount = table.GetProperty("rowCount").GetInt32(),
                ColumnCount = columns,
                HeaderRows = headerRows,
                HeaderTexts = headerTexts,
                HeaderFields = [.. headerTexts.Select(t => glossary.ResolveHeader(t)?.Field)],
                Polygon = Polygon(table),
            });
            foreach (var cell in cells)
            {
                graph.Cells.Add(new GraphCell
                {
                    TableId = id,
                    Page = page,
                    Row = cell.GetProperty("rowIndex").GetInt32(),
                    Column = cell.GetProperty("columnIndex").GetInt32(),
                    RowSpan = Int(cell, "rowSpan", 1),
                    ColumnSpan = Int(cell, "columnSpan", 1),
                    Kind = cell.TryGetProperty("kind", out var kind) ? kind.GetString() : null,
                    RawValue = cell.GetProperty("content").GetString() ?? "",
                    Polygon = Polygon(cell),
                });
            }
        }

        graph.Warnings.Add($"page {page}: {n} table(s) merged from Document Intelligence prebuilt-layout (second-opinion OCR); {skipped} skipped as already read by CU");
        return n;
    }

    private static (double Top, double Bottom) VerticalRange(List<double> polygon)
    {
        var ys = polygon.Where((_, i) => i % 2 == 1).ToList();
        return (ys.Min(), ys.Max());
    }

    /// <summary>Overlap of two vertical ranges as a share of the smaller one.</summary>
    private static double Overlap((double Top, double Bottom) a, (double Top, double Bottom) b)
    {
        var overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        var smaller = Math.Min(a.Bottom - a.Top, b.Bottom - b.Top);
        return overlap <= 0 || smaller <= 0 ? 0 : overlap / smaller;
    }

    private static List<double>? Polygon(JsonElement element) =>
        element.TryGetProperty("boundingRegions", out var regions) && regions.GetArrayLength() > 0 && regions[0].TryGetProperty("polygon", out var polygon)
            ? polygon.EnumerateArray().Select(p => p.GetDouble()).ToList()
            : null;
}
