namespace LossRun.Contracts;

// Contract for per-document cost metering (docs/architecture/cost-tracking.md). Stub only: briefs 04+
// implement ICostMeter and must record one UsageEvent per billable SDK call. Prices are never in code;
// they come from config/azure-prices.json at roll-up time.

/// <summary>One billable call, recorded at the point it is made.</summary>
/// <param name="DocumentId">Uploaded document the call is attributed to.</param>
/// <param name="RunId">Pipeline run (orchestration instance) id.</param>
/// <param name="Stage">Pipeline stage, e.g. classify, layout, learn, delta, validate.</param>
/// <param name="Service">Azure service, e.g. ContentUnderstanding, AzureOpenAI, DocumentIntelligence, DurableTask.</param>
/// <param name="Meter">Billing meter, e.g. documentPagesStandard, inputTokens, cachedInputTokens, outputTokens, actions.</param>
/// <param name="Quantity">Quantity in <paramref name="Unit"/>.</param>
/// <param name="Unit">pages, tokens, actions, executions, ...</param>
/// <param name="ModelOrAnalyzer">Deployment or analyzer name (configuration value, never a literal in code).</param>
/// <param name="DeploymentType">DataZoneStandard, GlobalStandard, DataZoneBatch, ...; null for non-model meters.</param>
/// <param name="Region">Azure region of the resource that billed.</param>
/// <param name="TimestampUtc">When the call completed.</param>
/// <param name="CorrelationId">Request/operation id from the service response, for reconciliation.</param>
/// <param name="ModelVersion">Model version behind the deployment (configuration; the API returns only the deployment name).</param>
/// <param name="ModelName">Brief 30 item 22: the model behind <paramref name="ModelOrAnalyzer"/> when the deployment is named apart from it; prices match on it.</param>
public sealed record UsageEvent(
    string DocumentId,
    string RunId,
    string Stage,
    string Service,
    string Meter,
    decimal Quantity,
    string Unit,
    string? ModelOrAnalyzer,
    string? DeploymentType,
    string Region,
    DateTimeOffset TimestampUtc,
    string? CorrelationId,
    string? ModelVersion = null,
    string? ModelName = null);

/// <summary>Records usage events. Implementations persist append-only (ledger) rows; see cost-tracking.md §6.</summary>
public interface ICostMeter
{
    ValueTask RecordAsync(UsageEvent usage, CancellationToken cancellationToken = default);
}
