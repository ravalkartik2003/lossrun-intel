using System.Reflection;
using System.Text.Json;
using LossRun.Api.Contracts;
using LossRun.Contracts;
using LossRun.Rules;
using YamlDotNet.RepresentationModel;

namespace LossRun.Orchestration.Tests;

/// <summary>docs/api/openapi.yaml is the contract; the DTOs must serialize exactly the properties it declares.</summary>
public class ApiContractTests
{
    private static readonly YamlMappingNode Schemas = Load();

    private static YamlMappingNode Load()
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(File.ReadAllText(Fixture.File("api", "openapi.yaml"))));
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        return (YamlMappingNode)((YamlMappingNode)root["components"])["schemas"];
    }

    private static string CamelCase(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);

    public static TheoryData<string, Type> Dtos => new()
    {
        { "UploadAccepted", typeof(UploadAccepted) }, { "SubmissionInfo", typeof(SubmissionInfo) }, { "UploadManifest", typeof(UploadManifest) },
        { "ManifestFile", typeof(ManifestFile) }, { "ManifestItem", typeof(ManifestItem) }, { "UploadCost", typeof(UploadCost) },
        { "UploadProgress", typeof(UploadProgress) }, { "UploadStatusView", typeof(UploadStatusView) }, { "DocumentResult", typeof(DocumentResult) },
        { "RuleOutcome", typeof(RuleOutcome) }, { "ReviewItem", typeof(ReviewItem) }, { "FieldOverride", typeof(FieldOverride) },
        { "ReviewDecisionRequest", typeof(ReviewDecisionRequest) }, { "ReviewDecision", typeof(ReviewDecision) }, { "FormatSummary", typeof(FormatSummary) },
        { "QuotePayload", typeof(QuotePayload) }, { "QuotePeriod", typeof(QuotePeriod) }, { "MoneySums", typeof(MoneySums) },
        { "WebhookEnvelope", typeof(WebhookEnvelope) }, { "ApiError", typeof(ApiError) },
        { "MeView", typeof(MeView) }, { "QuoteSummary", typeof(QuoteSummary) }, { "PeriodExposure", typeof(PeriodExposure) },
        { "QuoteContext", typeof(QuoteContext) }, { "AuditTrailEntry", typeof(AuditTrailEntry) }, { "AuditTrailView", typeof(AuditTrailView) },
        { "CostByPath", typeof(CostByPath) }, { "CostByCarrier", typeof(CostByCarrier) }, { "DocumentCostLine", typeof(DocumentCostLine) },
        { "CostReconciliation", typeof(CostReconciliation) }, { "CostSummary", typeof(CostSummary) }, { "FlagPushRequest", typeof(FlagPushRequest) },
        { "FlagPushResult", typeof(FlagPushResult) }, { "FollowUpRequest", typeof(FollowUpRequest) }, { "FollowUp", typeof(FollowUp) },
        { "ChatTurnRequest", typeof(ChatTurnRequest) }, { "ChatCitation", typeof(ChatCitation) }, { "ChatToolCall", typeof(ChatToolCall) },
        { "ChatTurnUsage", typeof(ChatTurnUsage) }, { "ChatTurnResult", typeof(ChatTurnResult) }, { "ChatConversation", typeof(ChatConversation) },
        { "WorkspaceRequest", typeof(WorkspaceRequest) }, { "ChatWorkspace", typeof(ChatWorkspace) },
        { "ReopenRequest", typeof(ReopenRequest) }, { "OverrideRequest", typeof(OverrideRequest) }, { "OverrideResult", typeof(OverrideResult) },
        { "AnnotationRequest", typeof(AnnotationRequest) }, { "Annotation", typeof(Annotation) }, { "OutboxEntry", typeof(OutboxEntry) },
        { "AliasDecision", typeof(LossRun.Formats.AliasDecision) }, { "LearnedAlias", typeof(LossRun.Contracts.LearnedMapping) },
        { "AliasEvidence", typeof(LossRun.Contracts.AliasEvidence) },
    };

    [Theory]
    [MemberData(nameof(Dtos))]
    public void Dto_properties_match_the_openapi_schema(string schemaName, Type dto)
    {
        var schema = (YamlMappingNode)Schemas[schemaName];
        var declared = ((YamlMappingNode)schema["properties"]).Children.Keys.Select(k => ((YamlScalarNode)k).Value!).Order(StringComparer.Ordinal).ToList();
        var actual = dto.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is null)
            .Select(p => CamelCase(p.Name)).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(declared, actual);
    }

    [Fact]
    public void Status_values_in_the_contract_are_the_ones_the_code_writes()
    {
        string[] Allowed(string schema, string property) =>
            ((YamlSequenceNode)((YamlMappingNode)((YamlMappingNode)((YamlMappingNode)Schemas[schema])["properties"])[property])["enum"]).Children
            .Select(c => ((YamlScalarNode)c).Value!).ToArray();

        string[] Constants(Type type) => type.GetFields().Select(f => (string)f.GetValue(null)!).ToArray();

        Assert.Equal(Constants(typeof(UploadStatuses)).Order(), Allowed("UploadManifest", "status").Order());
        Assert.Equal(Constants(typeof(ItemStatuses)).Order(), Allowed("ManifestItem", "status").Order());
        Assert.Equal(Constants(typeof(ReviewKinds)).Order(), Allowed("ReviewItem", "kind").Order());
        Assert.Equal(Constants(typeof(ReviewActions)).Order(), Allowed("ReviewDecisionRequest", "action").Order());
        Assert.Equal(System.Enum.GetNames<RuleStatus>().Select(CamelCase).Order(), Allowed("RuleOutcome", "status").Order());
    }
}
