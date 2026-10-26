using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Azure.Storage.Queues;
using LossRun.Api.Contracts;
using LossRun.Chat;
using LossRun.Contracts;
using LossRun.Ingestion;

namespace LossRun.Evaluation;

/// <summary>
/// <c>lossrun chat …</c> (brief 12): create the index, backfill it through the chat-index queue, and ask a question
/// against the API (streamed, with timings). Keyless: Search and the queue use the signed-in identity's RBAC roles; the
/// API uses LOSSRUN_API_USER / LOSSRUN_API_PASSWORD (or LOSSRUN_API_TOKEN), like the simulator.
/// </summary>
public static class ChatCli
{
    public static string SearchEndpoint => Settings.Required("LOSSRUN_SEARCH_ENDPOINT", "the AI Search endpoint: terraform output search_endpoint");
    public static string SearchIndex => Environment.GetEnvironmentVariable("LOSSRUN_SEARCH_INDEX") ?? "lossrun-docs";
    public static string QueueUri => Settings.Required("LOSSRUN_CHAT_QUEUE", "the chat-index queue: https://<storage account>.queue.core.windows.net/chat-index");

    public static async Task<int> RunAsync(string[] args)
    {
        string? Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        switch (args)
        {
            case ["init-index", ..]:
                await new AzureSearchChatSearch(new Uri(SearchEndpoint), SearchIndex, AzureCredentials.Default()).EnsureIndexAsync();
                Console.WriteLine($"index {SearchIndex} created or updated on {SearchEndpoint}");
                return 0;

            case ["reindex", ..]:
                var uploads = Option("--uploads")?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList() ?? [];
                if (args.Contains("--from-api"))
                {
                    uploads.AddRange(await UploadsFromApiAsync(Api.Base(Option("--base"))));
                }

                var queue = new QueueClient(new Uri(QueueUri), AzureCredentials.Default(), new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
                foreach (var u in uploads.Distinct(StringComparer.Ordinal))
                {
                    await queue.SendMessageAsync(u);
                }

                Console.WriteLine($"queued {uploads.Distinct(StringComparer.Ordinal).Count()} upload(s) for chat indexing");
                return 0;

            case ["ask", ..] when Option("--question") is { } question:
                var turn = await Api.AskAsync(Api.Base(Option("--base")), new ChatTurnRequest(Option("--scope") ?? ChatScopes.Quote, question, Option("--quote"),
                    Option("--workspace"), Option("--conversation")), delta => Console.Write(delta));
                Console.WriteLine();
                if (turn.Result is { } r)
                {
                    Console.WriteLine($"-- citations: {string.Join("; ", r.Citations.Select(c => $"{c.Ref} {c.Label}"))}");
                    Console.WriteLine($"-- tools: {string.Join("; ", r.ToolTrace.Select(t => $"{t.Tool}({t.Results}, {t.Ms} ms)"))}");
                    Console.WriteLine($"-- first token {turn.FirstDeltaMs} ms · total {turn.TotalMs} ms · ${r.Usage.CostUsd:0.000000} · {r.Usage.ModelCalls} model calls · {r.Usage.InputTokens} in ({r.Usage.CachedInputTokens} cached) / {r.Usage.OutputTokens} out");
                    return 0;
                }

                Console.Error.WriteLine(turn.Error);
                return 1;

            default:
                Console.Error.WriteLine("usage: lossrun chat init-index");
                Console.Error.WriteLine("       lossrun chat reindex (--uploads <id,id> | --from-api) [--base <api>]");
                Console.Error.WriteLine("       lossrun chat ask --scope org|quote|workspace [--quote <id>] [--workspace <id>] [--conversation <id>] --question <text> [--base <api>]");
                return 2;
        }
    }

    private static async Task<IReadOnlyList<string>> UploadsFromApiAsync(string baseUrl)
    {
        using var http = Api.Client();
        var quotes = await http.GetFromJsonAsync<List<QuoteSummary>>($"{baseUrl}/quotes", CanonicalJson.Options) ?? [];
        var uploads = new List<string>();
        foreach (var q in quotes)
        {
            using var payload = JsonDocument.Parse(await http.GetStringAsync($"{baseUrl}/quotes/{Uri.EscapeDataString(q.QuoteId)}/quote-payload"));
            uploads.AddRange(payload.RootElement.GetProperty("documents").EnumerateArray().Select(d => d.GetProperty("uploadId").GetString()!));
        }

        return uploads;
    }
}

/// <summary>The API from the CLI and the chat eval.</summary>
public static class Api
{
    public sealed record Turn(ChatTurnResult? Result, string? Error, long FirstDeltaMs, long TotalMs, IReadOnlyList<ChatToolCall> ToolsStreamed);

    public static string Base(string? option) =>
        (option ?? Settings.Required("LOSSRUN_API_BASE", "the API base, https://<function app host>/api/v1: terraform output function_app_hostname")).TrimEnd('/');

    public static HttpClient Client()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        if (Environment.GetEnvironmentVariable("LOSSRUN_API_TOKEN") is { Length: > 0 } token)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else
        {
            var user = Environment.GetEnvironmentVariable("LOSSRUN_API_USER") ?? throw new InvalidOperationException("Set LOSSRUN_API_USER/LOSSRUN_API_PASSWORD or LOSSRUN_API_TOKEN.");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{Environment.GetEnvironmentVariable("LOSSRUN_API_PASSWORD")}")));
        }

        return http;
    }

    /// <summary>POST /chat/turns as Server-Sent Events: time to the first answer delta, the tool events, the final turn.</summary>
    public static async Task<Turn> AskAsync(string baseUrl, ChatTurnRequest request, Action<string>? onDelta = null, CancellationToken cancellationToken = default)
    {
        using var http = Client();
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/turns") { Content = JsonContent.Create(request, options: CanonicalJson.Options) };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var stopwatch = Stopwatch.StartNew();
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new Turn(null, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync(cancellationToken)}", -1, stopwatch.ElapsedMilliseconds, []);
        }

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
        long first = -1;
        var tools = new List<ChatToolCall>();
        string? evt = null;
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                evt = line[7..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                data.Append(line[6..]);
            }
            else if (line.Length == 0 && evt is not null)
            {
                var json = data.ToString();
                switch (evt)
                {
                    case "delta":
                        first = first < 0 ? stopwatch.ElapsedMilliseconds : first;
                        onDelta?.Invoke(JsonDocument.Parse(json).RootElement.GetProperty("text").GetString() ?? "");
                        break;
                    case "tool":
                        tools.Add(JsonSerializer.Deserialize<ChatToolCall>(json, CanonicalJson.Options)!);
                        break;
                    case "final":
                        return new Turn(JsonSerializer.Deserialize<ChatTurnResult>(json, CanonicalJson.Options), null, first, stopwatch.ElapsedMilliseconds, tools);
                    case "error":
                        return new Turn(null, json, first, stopwatch.ElapsedMilliseconds, tools);
                }

                (evt, data) = (null, new StringBuilder());
            }
        }

        return new Turn(null, "stream ended without a final event", first, stopwatch.ElapsedMilliseconds, tools);
    }
}
