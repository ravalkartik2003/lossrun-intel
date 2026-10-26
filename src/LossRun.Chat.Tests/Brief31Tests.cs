using System.Text.Json.Nodes;

namespace LossRun.Chat.Tests;

/// <summary>Brief 31 item 5: the review-status filter is an app setting (Chat__ReviewStatusFilter, default on), so a deploy before the index update is a one-setting rollback.</summary>
public class Brief31Tests
{
    private sealed class Capture : HttpMessageHandler
    {
        public JsonNode? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"value":[]}""") };
        }
    }

    private sealed class Token : Azure.Core.TokenCredential
    {
        public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) => new("t", DateTimeOffset.MaxValue);

        public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Item5_the_index_side_review_status_filter_follows_the_setting(bool setting, bool filtered)
    {
        var capture = new Capture();
        var search = new AzureSearchChatSearch(new Uri("https://search.invalid/"), "lossrun-docs", new Token(), new HttpClient(capture), reviewStatusFilter: setting);

        await search.SearchAsync(ChatScope.ForQuote("Q-A"), "loss run", null, 4);

        var filter = capture.Body!["filter"]!.GetValue<string>();
        var select = capture.Body!["select"]!.GetValue<string>();
        Assert.Equal(filtered, filter.Contains("reviewStatus", StringComparison.Ordinal)); // before brief 31: always (no setting)
        Assert.Equal(filtered, select.Split(',').Contains("reviewStatus"));              // and the field was never selected back
        Assert.StartsWith("scope eq 'quote'", filter, StringComparison.Ordinal);
    }
}
