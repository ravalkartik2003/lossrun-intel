using LossRun.Data;
using LossRun.Rules;

namespace LossRun.Orchestration;

/// <summary>
/// Brief 29 §3: the rule context R-15 (valuation within 100 days of the target effective date) and R-25 (requested policy
/// years present) need. It comes from the quote's context (dbo.QuoteContext, which an upload's stated values are merged
/// into and <c>PUT /quotes/{q}/context</c> edits), else from the upload's own submission. Without either it is null and
/// both rules stay not-applicable, as on the synthetic corpus.
/// </summary>
public static class RuleContexts
{
    public static async Task<RuleContext?> ForUploadAsync(IRecordStore records, string uploadId, CancellationToken cancellationToken = default)
    {
        if (await records.GetManifestAsync(uploadId, cancellationToken).ConfigureAwait(false) is not { } manifest)
        {
            return null;
        }

        var submission = manifest.Submission;
        var quote = submission.QuoteId is { Length: > 0 } quoteId ? await records.GetQuoteContextAsync(quoteId, cancellationToken).ConfigureAwait(false) : null;
        var target = quote?.TargetPolicyEffectiveDate ?? submission.TargetPolicyEffectiveDate;
        var years = quote?.RequestedPolicyYears is { Count: > 0 } q ? q : submission.RequestedPolicyYears;
        return target is null && years is not { Count: > 0 } ? null : new RuleContext { TargetPolicyEffectiveDate = target, RequestedPolicyYears = years };
    }
}
