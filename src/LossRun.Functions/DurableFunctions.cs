using System.Diagnostics;
using LossRun.Api.Contracts;
using LossRun.Orchestration;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

namespace LossRun.Functions;

/// <summary>Thin triggers over LossRun.Orchestration (logic and tests live there).</summary>
public sealed class DurableFunctions(UploadActivities activities, ILogger<DurableFunctions> logger)
{
    [Function(OrchestratorNames.IngestUpload)]
    public static Task<UploadSummary> IngestUpload([OrchestrationTrigger] TaskOrchestrationContext context) =>
        UploadOrchestration.RunAsync(context, context.GetInput<UploadInput>()!);

    [Function(OrchestratorNames.ProcessLossRun)]
    public static Task<DocumentOutcome> ProcessLossRun([OrchestrationTrigger] TaskOrchestrationContext context) =>
        LossRunOrchestration.RunAsync(context, context.GetInput<DocumentWork>()!);

    /// <summary>Learn/delta concurrency cap (brief 08c, 08b Answers 4).</summary>
    [Function(LearnGate.EntityName)]
    public static Task LearnGateEntity([EntityTrigger] TaskEntityDispatcher dispatcher) => dispatcher.DispatchAsync<LearnGate>();

    [Function(ActivityNames.ExpandUpload)]
    public Task<IReadOnlyList<FileWork>> ExpandUpload([ActivityTrigger] string uploadId)
    {
        Tag(uploadId, null);
        return activities.ExpandUploadAsync(uploadId);
    }

    [Function(ActivityNames.ParseFile)]
    public async Task<ParseResult> ParseFile([ActivityTrigger] FileWork file)
    {
        Tag(file.UploadId, null);
        var result = await activities.ParseFileAsync(file);
        foreach (var miss in result.CacheMisses ?? [])
        {
            logger.LogInformation("CU cache miss on {UploadId}: {Miss}", file.UploadId, miss); // brief 34 Answers 1: which hash moved
        }

        return result;
    }

    [Function(ActivityNames.FailUpload)]
    public Task FailUpload([ActivityTrigger] FailInput input)
    {
        Tag(input.UploadId, null);
        return activities.FailUploadAsync(input);
    }

    [Function(ActivityNames.RecordItems)]
    public Task RecordItems([ActivityTrigger] ItemsInput input)
    {
        Tag(input.UploadId, null);
        return activities.RecordItemsAsync(input);
    }

    [Function(ActivityNames.Route)]
    public Task<RouteResult> Route([ActivityTrigger] DocumentWork work)
    {
        Tag(work.UploadId, work.DocumentId);
        return activities.RouteAsync(work);
    }

    [Function(ActivityNames.Delta)]
    public Task<LearnResult> Delta([ActivityTrigger] LearnInput input)
    {
        Tag(input.Work.UploadId, input.Work.DocumentId);
        return activities.DeltaAsync(input);
    }

    [Function(ActivityNames.Learn)]
    public Task<LearnResult> Learn([ActivityTrigger] LearnInput input)
    {
        Tag(input.Work.UploadId, input.Work.DocumentId);
        return activities.LearnAsync(input);
    }

    [Function(ActivityNames.ValidateAndPersist)]
    public Task<DocumentOutcome> ValidateAndPersist([ActivityTrigger] PersistInput input)
    {
        Tag(input.Work.UploadId, input.Work.DocumentId);
        return activities.ValidateAndPersistAsync(input);
    }

    [Function(ActivityNames.CompleteUpload)]
    public Task<UploadCompletion> CompleteUpload([ActivityTrigger] CompleteInput input)
    {
        Tag(input.UploadId, null);
        return activities.CompleteUploadAsync(input);
    }

    [Function(ActivityNames.ApplyDecision)]
    public Task<DocumentOutcome> ApplyDecision([ActivityTrigger] ReviewDecision decision)
    {
        Tag(null, decision.DocumentId);
        return activities.ApplyDecisionAsync(decision);
    }

    [Function(ActivityNames.ExpireReviews)]
    public Task ExpireReviews([ActivityTrigger] ExpireInput input)
    {
        Tag(input.UploadId, null);
        return activities.ExpireReviewsAsync(input);
    }

    /// <summary>uploadId/documentId on the current Application Insights operation (brief 07 §4).</summary>
    private static void Tag(string? uploadId, string? documentId)
    {
        Activity.Current?.SetTag("uploadId", uploadId);
        Activity.Current?.SetTag("documentId", documentId);
    }
}
