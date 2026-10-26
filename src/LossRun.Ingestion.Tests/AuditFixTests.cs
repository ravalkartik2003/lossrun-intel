using LossRun.Contracts;
using LossRun.Ingestion.Metering;

namespace LossRun.Ingestion.Tests;

/// <summary>Brief 30 item 22: a model call is priced by its model name, and one with no price is reported unpriced, never as $0.</summary>
public class AuditFixTests
{
    private static UsageEvent Tokens(string deployment, long n, string meter = "inputTokens", string? modelName = null) =>
        new("doc-1", "run", "learn", "AzureOpenAI", meter, n, "token", deployment, "DataZoneStandard", "eastus2", DateTimeOffset.UnixEpoch, "c-1", ModelName: modelName);

    [Fact]
    public void Item22_a_deployment_named_apart_from_its_model_is_priced_as_the_model()
    {
        var prices = PriceTable.Embedded.WithDeployments(new Dictionary<string, string> { ["chat-gpt-5.4-mini"] = "gpt-5.4-mini" });
        var cost = CostCalculator.Compute("doc-1", "run", "learn", [Tokens("chat-gpt-5.4-mini", 1_000_000)], prices);

        var line = Assert.Single(cost.Breakdown);
        Assert.True(line.CostUsd > 0); // before brief 30: "missing price", $0
        Assert.Equal(PriceTable.Embedded.Prices.Single(p => p.Model == "gpt-5.4-mini" && p.Meter == "inputTokens").UnitPrice * 1_000_000, line.CostUsd);
        Assert.Equal(0, cost.UnpricedLines);

        // The event may also carry the model name itself.
        Assert.Equal(line.CostUsd, CostCalculator.Compute("doc-1", "run", "learn", [Tokens("my-deployment", 1_000_000, modelName: "gpt-5.4-mini")], PriceTable.Embedded).CostUsd);
    }

    [Fact]
    public void Item22_a_model_with_no_price_is_unpriced_not_free()
    {
        var cost = CostCalculator.Compute("doc-1", "run", "learn", [Tokens("gpt-9-preview", 500)], PriceTable.Embedded);

        Assert.Equal(1, cost.UnpricedLines);
        Assert.Equal(["AzureOpenAI inputTokens (gpt-9-preview)"], cost.Unpriced);
        Assert.StartsWith("unpriced", CostCalculator.Summary(cost, "CU layout"), StringComparison.Ordinal);
    }

    [Fact]
    public void Item22_the_price_table_is_read_from_the_config_file_with_the_embedded_copy_as_fallback()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "azure-prices.json");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { priceTableVersion = "test-1", region = "eastus2", prices = Array.Empty<object>() }));
            Assert.Equal("test-1", PriceTable.FromFileOrEmbedded(path).Version);
            Assert.Equal(PriceTable.Embedded.Version, PriceTable.FromFileOrEmbedded(Path.Combine(dir.FullName, "missing.json")).Version);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    private sealed class StaticCredential : Azure.Core.TokenCredential
    {
        public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) => new("t", DateTimeOffset.MaxValue);

        public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class HtmlHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("<html><body>Bad gateway</body></html>") });
    }

    [Fact]
    public async Task Item39_an_inline_call_answered_with_an_html_page_falls_back_instead_of_throwing()
    {
        var endpoint = new Uri("https://cu.invalid/");
        var service = new LossRun.Ingestion.ContentUnderstanding.ContentUnderstandingService(
            new Azure.AI.ContentUnderstanding.ContentUnderstandingClient(endpoint, new StaticCredential()),
            new LossRun.Ingestion.ContentUnderstanding.InlineAnalyzeOptions(endpoint, new StaticCredential()), new HttpClient(new HtmlHandler()));

        Assert.Null(await service.TryInlineAsync("prebuilt-layout", BinaryData.FromBytes([1, 2, 3]), "image/png", CancellationToken.None)); // before: JsonException
    }

    [Fact]
    public void Brief31_the_mapper_keeps_a_missing_word_confidence_null()
    {
        var json = System.Text.Json.JsonDocument.Parse("""
            {"result":{"contents":[{"kind":"document","markdown":"Claim","startPageNumber":1,"endPageNumber":1,
              "pages":[{"pageNumber":1,"width":8.5,"height":11,"words":[{"content":"Claim","source":"D(1,0,0,1,0,1,1,0,1)","span":{"offset":0,"length":5}}]}]}]}}
            """).RootElement;
        var graph = LossRun.Ingestion.ContentUnderstanding.CuResultMapper.Map(json, "doc-x", new SourceFile { FileName = "x.pdf", Kind = FileKind.Pdf }, "prebuilt-layout",
            CanonicalData.Glossary, null, null);

        Assert.Null(Assert.Single(graph.Words).Confidence); // before brief 31: 0
    }
}
