using Azure.Messaging.EventGrid;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using LossRun.Integration;
using LossRun.Chat;
using LossRun.Functions;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;
using LossRun.Contracts;
using LossRun.Data;
using LossRun.Formats;
using LossRun.Ingestion;
using LossRun.Ingestion.ContentUnderstanding;
using LossRun.Learning;
using LossRun.Orchestration;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// LossRun Functions host (brief 07): Durable Functions on Durable Task Scheduler + HTTP API v1. Identity-only: every
// Azure call uses the app's managed identity (DefaultAzureCredential); configuration comes from the Terraform app settings.
var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();
builder.UseMiddleware<LossRun.Functions.ErrorMiddleware>(); // brief 30 items 27–28
builder.Services.AddApplicationInsightsTelemetryWorkerService().ConfigureFunctionsApplicationInsights();

builder.Services.AddSingleton(sp => Runtime.Build(sp.GetRequiredService<IConfiguration>()) with
{
    EscalationTelemetry = new LossRun.Functions.AppInsightsEscalationTelemetry(sp.GetRequiredService<Microsoft.ApplicationInsights.TelemetryClient>()),
});
builder.Services.AddSingleton(sp => Runtime.Integration(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<LossRunRuntime>()));
builder.Services.AddSingleton(sp => new UploadActivities(sp.GetRequiredService<LossRunRuntime>()));
builder.Services.AddSingleton(sp => Runtime.Authenticator(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(sp => new ApiOptions(ReviewTimeout: TimeSpan.FromDays(sp.GetRequiredService<IConfiguration>().GetValue("Review:TimeoutDays", 7.0)),
    ApplicantMayCreateQuotes: sp.GetRequiredService<IConfiguration>().GetValue("Quotes:ApplicantMayCreate", true), // brief 32 Answers 4; Terraform sets it by environment
    Retry: RetryConfig.Embedded)); // brief 33: handed to every upload orchestration
// Brief 33: the Durable worker adds these properties to an activity's failure details (a SqlException's number is not in its message).
builder.Services.AddSingleton<Microsoft.DurableTask.Worker.IExceptionPropertiesProvider, FailureProperties>();
builder.Services.AddSingleton(sp => Runtime.Chat(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<LossRunRuntime>()));

builder.Build().Run();

internal static class Runtime
{
    public static LossRunRuntime Build(IConfiguration config)
    {
        var credential = AzureCredentials.Default();
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var blobs = new BlobServiceClient(new Uri(config["Storage:BlobEndpoint"]!), credential);
        var store = new BlobObjectStore(blobs);

        // Registry: checked-in hand specs (read-only) under the learned formats — Azure SQL as the source with Blob as cache
        // (centroids, glossary sidecar) when Records:Store = sql (brief 09), else the Blob formats container alone.
        var handSpecs = new InMemoryFormatRegistry();
        FormatDirectory.LoadIntoAsync(Path.Combine(AppContext.BaseDirectory, "formats"), handSpecs).GetAwaiter().GetResult();
        IFormatRegistry learned = new BlobFormatRegistry(blobs.GetBlobContainerClient(config["Formats:Container"] ?? "formats"));
        IRecordStore? records = null;
        if (config["Records:Store"] == "sql" && config["Sql:ConnectionString"] is { Length: > 0 } sql)
        {
            var options = LossRunDbContext.Options(sql);
            LossRunDbContext Contexts() => new(options);
            records = new SqlRecordStore(Contexts, new ClaimCommandService(Contexts));
            learned = new SqlFormatRegistry(Contexts, learned, handSpecs);
        }

        var registry = new LayeredFormatRegistry(handSpecs, learned);

        var aiEndpoint = new Uri(config["AzureAI:Endpoint"]!);
        var cuEndpoint = new Uri(config["ContentUnderstanding:Endpoint"]!);
        var inlinePages = config.GetValue("ContentUnderstanding:InlineMaxPages", 0);
        var cu = new ContentUnderstandingService(cuEndpoint, credential, inlinePages > 0 ? new InlineAnalyzeOptions(cuEndpoint, credential, inlinePages) : null,
            config.GetValue("ContentUnderstanding:MaxAttempts", IngestionOptions.DefaultCuMaxAttempts)); // brief 33
        var ingestion = new IngestionOptions
        {
            ContentUnderstandingEndpoint = cuEndpoint,
            Passwords = (config["Ingestion:Passwords"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries),
            CuDefaultsHash = config["Cu:DefaultsHash"], // brief 34 A1.2: part of the raw-result reuse key; Terraform sets it from the CU defaults it sends
        };

        var embedded = LearnOptions.FromConfiguration();
        var learn = embedded with
        {
            LearnDeployment = config["Models:Learn"] ?? embedded.LearnDeployment,
            LearnModelVersion = config["Models:LearnVersion"] ?? embedded.LearnModelVersion,
            DeltaDeployment = config["Models:Delta"] ?? embedded.DeltaDeployment,
            DeltaModelVersion = config["Models:DeltaVersion"] ?? embedded.DeltaModelVersion,
            EmbeddingModel = config["Models:Embedding"] ?? embedded.EmbeddingModel,
            MaxIterations = config.GetValue("Learn:MaxIterations", embedded.MaxIterations),
            IncludePageImage = config.GetValue("Learn:IncludePageImage", false),
        };

        records ??= new BlobRecordStore(store);
        var webhookUrl = config["Webhook:Url"] is { Length: > 0 } url ? new Uri(url) : null;
        IChatIndexQueue? chatIndex = config["ChatIndexQueue:queueServiceUri"] is { Length: > 0 } queueUri && config["Chat:IndexQueue"] is { Length: > 0 } queueName
            ? new StorageChatIndexQueue(StorageChatIndexQueue.Client(new Uri(queueUri), queueName, credential))
            : null;
        return new LossRunRuntime(store, registry, cu, ingestion, learn,
            meter => new AzureOpenAiEmbeddingService(aiEndpoint, credential, learn.EmbeddingModel!, meter, ingestion.Region, ingestion.ModelDeploymentType, http),
            meter => new AzureOpenAiLearnModel(aiEndpoint, credential, meter, learn.DeploymentType, learn.Region, http),
            new HmacWebhookSender(webhookUrl, config["Webhook:Secret"], http),
            TimeProvider.System,
            Records: records,
            // Brief 30 item 22: prices from the deployed config file when present (embedded copy otherwise); deployments priced as their model.
            Prices: LossRun.Ingestion.Metering.PriceTable.FromFileOrEmbedded(config["Prices:Path"] ?? Path.Combine(AppContext.BaseDirectory, "config", "azure-prices.json"))
                .WithDeployments(config["Models:DeploymentModels"]),
            ChatIndex: chatIndex,
            Outbox: OutboxRelay(config, records, credential));
    }

    /// <summary>
    /// Brief 12b: the outbox relay over Service Bus (commands) and Event Grid (CloudEvents), both Entra-only. Without the
    /// settings the outbox rows still commit with their audit rows and wait for a configured host's sweep.
    /// </summary>
    private static OutboxRelay? OutboxRelay(IConfiguration config, IRecordStore records, Azure.Core.TokenCredential credential)
    {
        if (config["ServiceBus:fullyQualifiedNamespace"] is not { Length: > 0 } ns || config["Integration:CommandQueue"] is not { Length: > 0 } queue
            || config["Integration:EventGridEndpoint"] is not { Length: > 0 } topic)
        {
            return null;
        }

        var bus = new ServiceBusClient(ns, credential);
        var publisher = new RoutingOutboxPublisher(new ServiceBusOutboxPublisher(bus.CreateSender(queue)),
            new EventGridOutboxPublisher(new EventGridPublisherClient(new Uri(topic), credential), config["Integration:EventSource"] is { Length: > 0 } source ? source : EventGridOutboxPublisher.DefaultSource));
        return new LossRun.Integration.OutboxRelay(records, publisher, TimeProvider.System);
    }

    /// <summary>The dispatcher's target: the quote system's REST API with its function key (a Key Vault reference).</summary>
    public static IntegrationRuntime Integration(IConfiguration config, LossRunRuntime runtime)
    {
        CommandDispatcher? dispatcher = null;
        if (config["QuoteSystem:BaseUrl"] is { Length: > 0 } baseUrl)
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var endpoint = new HttpQuoteSystemEndpoint(http, new Uri(baseUrl.TrimEnd('/') + "/"), config["QuoteSystem:Key"]);
            dispatcher = new CommandDispatcher(endpoint, runtime.RecordStore, runtime.Clock);
        }

        return new IntegrationRuntime(runtime.Outbox, dispatcher);
    }

    /// <summary>
    /// Brief 12: the chat agent over the chat deployment (OpenAI SDK on the Azure v1 endpoint, Entra bearer, store off),
    /// AI Search (RBAC), embeddings metered as chat-embedding. Without Search:Endpoint chat answers 503.
    /// </summary>
    public static ChatRuntime Chat(IConfiguration config, LossRunRuntime runtime)
    {
        var ttl = config.GetValue("Chat:WorkspaceTtlHours", 72);
        if (config["Search:Endpoint"] is not { Length: > 0 } searchEndpoint)
        {
            return new ChatRuntime(null, new ChatWorkspaces(runtime.RecordStore, null, runtime.Clock, ttl), null);
        }

        var credential = AzureCredentials.Default();
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var aiEndpoint = new Uri(config["AzureAI:Endpoint"]!);
        var deployment = config["Models:Chat"] ?? config["Models:Delta"] ?? runtime.Learn.DeltaDeployment;
        // The OpenAI SDK marks token-policy auth and its Responses client "evaluation only" (OPENAI001; MAAI001 in Agent
        // Framework). The service side is GA (Azure OpenAI v1 Responses API, used by the learn path over REST since brief 06);
        // the markers are about SDK API stability, so a package upgrade is the place to re-check them (ADR 0013).
#pragma warning disable OPENAI001, MAAI001
        var openai = new OpenAIClient(new System.ClientModel.Primitives.BearerTokenPolicy(credential, "https://cognitiveservices.azure.com/.default"),
            new OpenAIClientOptions { Endpoint = new Uri(aiEndpoint, "openai/v1/") });
        IChatClient model = openai.GetResponsesClient().AsIChatClientWithStoredOutputDisabled(deployment, false);
#pragma warning restore OPENAI001, MAAI001

        var search = new AzureSearchChatSearch(new Uri(searchEndpoint), config["Search:Index"] ?? "lossrun-docs", credential, http,
            config.GetValue("Chat:ReviewStatusFilter", true)); // brief 31 item 5
        var region = runtime.Ingestion.Region;
        Func<ICostMeter, IEmbeddingService> Embeddings(string stage) => meter =>
            new AzureOpenAiEmbeddingService(aiEndpoint, credential, runtime.Learn.EmbeddingModel!, meter, region, runtime.Ingestion.ModelDeploymentType, http, stage);
        var indexer = new ChatIndexer(runtime.RecordStore, search,
            (uploadId, documentId, ct) => runtime.Store.GetJsonAsync<DocumentGraph>(Containers.Extractions, StorePaths.Document(uploadId, documentId, "graph.json"), ct),
            Embeddings("chat-index"));
        // Brief 24 step 6: the verbatim history window is a token budget; the turn count is a ceiling.
        var options = new ChatServiceOptions(deployment, config["Models:DeltaVersion"] ?? runtime.Learn.DeltaModelVersion, runtime.Learn.DeploymentType, runtime.Learn.Region,
            config["Search:Region"] ?? "centralus", ttl, HistoryTurns: config.GetValue("Chat:HistoryTurns", 12), HistoryTokenBudget: config.GetValue("Chat:HistoryTokenBudget", 6000));
        var service = new ChatService(options, model, search, Embeddings("chat-embedding"), runtime.RecordStore, runtime.PriceTable, runtime.Clock);
        return new ChatRuntime(service, new ChatWorkspaces(runtime.RecordStore, indexer, runtime.Clock, ttl, (u, ct) => WorkspacePurge.UploadAsync(runtime.Store, u, ct)), indexer);
    }

    /// <summary>Entra ID bearer by default; <c>LOSSRUN_DEV_AUTH=basic</c> (or Auth:Mode=basic) for the external demo host. Never anonymous.</summary>
    public static IAuthenticator Authenticator(IConfiguration config)
    {
        var mode = Environment.GetEnvironmentVariable("LOSSRUN_DEV_AUTH") ?? config["Auth:Mode"] ?? "entra";
        return mode switch
        {
            "basic" => new BasicAuthenticator(config["DevAuth:Users"] ?? throw new InvalidOperationException("Auth mode basic needs DevAuth:Users (Key Vault reference).")),
            _ => new EntraAuthenticator(config["Auth:TenantId"] ?? throw new InvalidOperationException("Auth:TenantId is required for Entra auth."),
                config["Auth:Audience"] ?? throw new InvalidOperationException("Auth:Audience is required for Entra auth.")),
        };
    }
}
