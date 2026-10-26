namespace LossRun.Contracts;

/// <summary>
/// What the submission says about an upload (brief 05 Answers 3), passed into fingerprinting and replay. The carrier
/// hint wins over a format's declared constant carrier (letterhead-less exports); a hint that disagrees with the
/// matched format routes to Review with reason <c>carrier-mismatch</c>. Brief 07 fills it from the API.
/// </summary>
/// <param name="CarrierHint">Carrier the uploader or the quote names for this file, if known.</param>
/// <param name="QuoteId">Quote the upload belongs to.</param>
/// <param name="UploaderRole">applicant | broker | underwriter | system.</param>
/// <param name="ValuationDate">Valuation date the submission states, used when the document does not print one (06b Answers 1).</param>
/// <param name="PolicyPeriods">Policy periods the submission states, used to fill dates the document does not print.</param>
public sealed record SubmissionContext(string? CarrierHint = null, string? QuoteId = null, string UploaderRole = "unknown", DateOnly? ValuationDate = null,
    IReadOnlyList<ContextPeriod>? PolicyPeriods = null);

/// <summary>A policy period as the submission states it (matched to the document's periods by normalized policy number).</summary>
public sealed record ContextPeriod(string PolicyNumber, DateOnly? EffectiveDate, DateOnly? ExpirationDate);
