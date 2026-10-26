using System.Net;
using Azure.AI.ContentUnderstanding;
using Azure.Core;
using Azure.Core.Pipeline;
using LossRun.Contracts;
using LossRun.Ingestion.ContentUnderstanding;

namespace LossRun.Ingestion.Tests;

/// <summary>Brief 33: a parse that runs again never bills again, and a throttled analyze is retried where the Retry-After is.</summary>
public class Brief33Tests
{
    [Fact]
    public async Task A_parse_run_again_reuses_the_stored_results_and_bills_nothing()
    {
        var h = new Harness();
        var bytes = Fixture.Bytes("lossrun-native.pdf");
        await h.Pipeline().IngestAsync("lossrun-native.pdf", bytes);
        var calls = h.Cu.Calls.Count;
        var billed = h.Meter.Events.Count(e => e.Stage != "reused" && e.Quantity > 0);
        Assert.True(calls > 0);

        // A Durable re-delivery after a worker loss, or the activity's retry: the same document, the same bytes.
        await h.Pipeline().IngestAsync("lossrun-native.pdf", bytes);

        Assert.Equal(calls, h.Cu.Calls.Count); // before brief 33: every page analyzed (and billed) again
        Assert.Equal(billed, h.Meter.Events.Count(e => e.Stage != "reused" && e.Quantity > 0));
        var reused = h.Meter.Events.Where(e => e.Stage == "reused").ToList();
        Assert.NotEmpty(reused);
        Assert.All(reused, e => Assert.Equal(0, e.Quantity)); // the cost report shows the reuse

        // Different bytes under the same document id are analyzed again: the stored result is keyed on the content.
        var changed = (byte[])bytes.Clone();
        changed[^2] ^= 0x20;
        await h.Pipeline().IngestAsync("lossrun-native.pdf", changed);
        Assert.True(h.Cu.Calls.Count > calls);
    }

    [Fact]
    public async Task Brief34_the_reuse_key_includes_the_definition_and_the_defaults_and_an_eval_never_reuses()
    {
        var h = new Harness();
        var bytes = Fixture.Bytes("bundle.pdf");
        await h.Pipeline().IngestAsync("bundle.pdf", bytes);
        var calls = h.Cu.Calls.Count;

        // The analyzer definition was edited under the same id since the result was stored: its hash is in the stored meta, and it differs.
        foreach (var meta in h.Store.Items.Keys.Where(k => k.EndsWith(".meta.json", StringComparison.Ordinal)).ToList())
        {
            var stored = System.Text.Json.JsonSerializer.Deserialize<IngestionPipeline.RawResultMeta>(h.Store.Items[meta])!;
            h.Store.Items[meta] = System.Text.Json.JsonSerializer.Serialize(stored with { DefinitionSha256 = stored.DefinitionSha256 is null ? null : "an older definition" });
        }

        var missed = await h.Pipeline().IngestAsync("bundle.pdf", bytes);
        var afterDefinition = h.Cu.Calls.Count;
        // Answers 1: the miss says which hash moved.
        Assert.Contains(missed.CacheMisses, m => m.StartsWith("lossrun_classifier", StringComparison.Ordinal) && m.Contains("definition an older de", StringComparison.Ordinal));
        Assert.Contains(h.Cu.Calls, c => c.AnalyzerId.StartsWith("lossrun_classifier", StringComparison.Ordinal)); // the custom classifier, with a checked-in definition
        Assert.True(afterDefinition > calls); // before brief 34: reused (the key was the analyzer id and the bytes only)

        // Another model behind a prebuilt alias (the CU defaults hash Terraform sets) is another result.
        var moved = await h.Pipeline(new IngestionOptions { CuDefaultsHash = "defaults-v2" }).IngestAsync("bundle.pdf", bytes);
        Assert.True(h.Cu.Calls.Count > afterDefinition);
        Assert.Contains(moved.CacheMisses, m => m.EndsWith("defaults none → defaults-v2", StringComparison.Ordinal));
        var withDefaults = h.Cu.Calls.Count;
        await h.Pipeline(new IngestionOptions { CuDefaultsHash = "defaults-v2" }).IngestAsync("bundle.pdf", bytes);
        Assert.Equal(withDefaults, h.Cu.Calls.Count); // same defaults: reused

        await h.Pipeline(new IngestionOptions { CuDefaultsHash = "defaults-v2", ForceReparse = true }).IngestAsync("bundle.pdf", bytes);
        Assert.True(h.Cu.Calls.Count > withDefaults); // the eval runner's ForceReparse
    }

    [Fact]
    public async Task Brief34_a_call_whose_result_write_failed_is_metered_again_as_rebilled_and_priced()
    {
        var cu = new FakeContentUnderstanding();
        var meter = new LossRun.Ingestion.Metering.InMemoryCostMeter();
        var store = new FailFirstResultWrite();
        var pipeline = new IngestionPipeline(cu, meter, store, LossRun.Ingestion.Metering.PriceTable.Embedded, CanonicalData.Glossary, new IngestionOptions());
        var bytes = Fixture.Bytes("lossrun-native.pdf");

        await Assert.ThrowsAsync<IOException>(() => pipeline.IngestAsync("lossrun-native.pdf", bytes)); // billed, then the result write failed
        var billedOnce = meter.Events.Where(e => e.Quantity > 0).ToList();
        await pipeline.IngestAsync("lossrun-native.pdf", bytes); // the retry: nothing stored to reuse

        var rebilled = meter.Events.Where(e => e.Stage == "rebilled").ToList();
        Assert.NotEmpty(rebilled);
        Assert.Equal(billedOnce.Sum(e => e.Quantity), rebilled.Sum(e => e.Quantity)); // the same pages, billed again
        var cost = LossRun.Ingestion.Metering.CostCalculator.Compute("d", "r", "x", rebilled.Select(e => e with { DocumentId = "d" }), LossRun.Ingestion.Metering.PriceTable.Embedded);
        Assert.True(cost.CostUsd > 0); // the cost view and the reconciliation price it like any call
    }

    private sealed class FailFirstResultWrite : IRawResultStore
    {
        private readonly InMemoryRawResultStore _inner = new();
        private bool _failed;

        public Task<string> SaveAsync(string documentId, string name, string json, CancellationToken cancellationToken)
        {
            if (!_failed && !name.EndsWith(".pending.json", StringComparison.Ordinal) && !name.EndsWith(".meta.json", StringComparison.Ordinal))
            {
                _failed = true;
                throw new IOException("injected: the raw result write failed");
            }

            return _inner.SaveAsync(documentId, name, json, cancellationToken);
        }

        public Task<RawResult?> TryGetAsync(string documentId, string name, CancellationToken cancellationToken) => _inner.TryGetAsync(documentId, name, cancellationToken);
    }

    [Fact]
    public async Task A_throttled_analyze_is_retried_after_the_retry_after_and_a_failed_poll_never_resubmits()
    {
        var transport = new ScriptedCu();
        var client = new ContentUnderstandingClient(new Uri("https://cu.example.invalid/"), new StaticToken(),
            ContentUnderstandingService.ClientOptions(5, new HttpClientTransport(new HttpClient(transport))));
        var service = new ContentUnderstandingService(client);

        var result = await service.AnalyzeAsync("prebuilt-layout", BinaryData.FromBytes([1, 2, 3]), "application/pdf", CancellationToken.None);

        Assert.Equal("op-1", result.OperationId);
        Assert.Equal(2, transport.Submits); // the 429, then one billed analyze; before brief 33 a 429 after the pipeline's 3 retries failed the upload
        Assert.True(transport.Polls >= 2); // the 503 on a poll polled the same operation again
        Assert.True(transport.SecondSubmitAfter >= TimeSpan.FromSeconds(0.9), $"retried after {transport.SecondSubmitAfter}"); // Retry-After: 1 honoured
    }

    private sealed class StaticToken : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => new("t", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => new(GetToken(requestContext, cancellationToken));
    }

    /// <summary>Content Understanding as a script: 429 (Retry-After 1), then 202; the first poll 503, then Succeeded.</summary>
    private sealed class ScriptedCu : HttpMessageHandler
    {
        private DateTimeOffset _firstSubmit;

        public int Submits { get; private set; }
        public int Polls { get; private set; }
        public TimeSpan SecondSubmitAfter { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                Submits++;
                if (Submits == 1)
                {
                    _firstSubmit = DateTimeOffset.UtcNow;
                    var throttled = new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("{\"error\":{\"code\":\"TooManyRequests\",\"message\":\"Rate limit\"}}") };
                    throttled.Headers.Add("Retry-After", "1");
                    return Task.FromResult(throttled);
                }

                SecondSubmitAfter = DateTimeOffset.UtcNow - _firstSubmit;
                var accepted = new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("{\"id\":\"op-1\",\"status\":\"Running\"}") };
                accepted.Headers.Add("Operation-Location", "https://cu.example.invalid/contentunderstanding/analyzerResults/op-1?api-version=2025-11-01");
                accepted.Headers.Add("Retry-After", "0");
                return Task.FromResult(accepted);
            }

            Polls++;
            if (Polls == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"error\":{\"code\":\"ServiceUnavailable\",\"message\":\"busy\"}}") });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"op-1\",\"status\":\"Succeeded\",\"result\":{\"analyzerId\":\"prebuilt-layout\",\"contents\":[]}}"),
            });
        }
    }
}
