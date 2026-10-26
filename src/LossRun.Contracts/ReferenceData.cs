using System.Text.Json;

namespace LossRun.Contracts;

// Typed views of docs/evaluation/glossary.json and code-sets.json.

public sealed class Glossary
{
    public string GlossaryVersion { get; set; } = "";
    public string SchemaVersion { get; set; } = "";
    public Dictionary<string, string> Sources { get; set; } = [];
    public Dictionary<string, GlossaryField> Fields { get; set; } = [];
    public CompositeHeaders CompositeHeaders { get; set; } = new();

    /// <summary>Printed columns deliberately left unmapped (ADR 0017): the learn path lists them as ignored headers.</summary>
    public IgnoredHeaders IgnoredHeaders { get; set; } = new();
    public List<CarrierProvenance> CarrierProvenance { get; set; } = [];

    /// <summary>
    /// Header mappings learned and verified by the learn path (ADR 0019; SQL <c>dbo.GlossaryAlias</c> or the registry sidecar
    /// <c>glossary-learned.json</c>, never the checked-in seed). Only <b>active</b> aliases resolve headers, after the seed.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<LearnedMapping> Learned { get; private set; } = [];

    /// <summary>The same seed glossary with learned mappings attached (the seed itself is never modified).</summary>
    public Glossary WithLearned(IEnumerable<LearnedMapping> learned)
    {
        var copy = (Glossary)MemberwiseClone();
        copy.Learned = learned.ToList();
        return copy;
    }

    /// <summary>
    /// Maps a printed value of an enumerated field (e.g. "Re-opened" for <c>claim.status</c>) to its canonical value,
    /// using the glossary's valueAliases. Matching is case- and punctuation-insensitive. Returns null when unmapped.
    /// </summary>
    public string? ResolveValue(string canonicalField, string? printed)
    {
        if (printed is null || !Fields.TryGetValue(canonicalField, out var field) || field.ValueAliases is null)
        {
            return null;
        }

        var key = CanonicalComparer.NormalizeText(printed);
        foreach (var (canonical, aliases) in field.ValueAliases)
        {
            if (CanonicalComparer.NormalizeText(canonical) == key
                || aliases.Any(a => a.Status is null or AliasStatus.Active && CanonicalComparer.NormalizeText(a.Value) == key))
            {
                return canonical;
            }
        }

        return null;
    }

    /// <summary>The claim flags a printed enumerated value states besides its canonical value (<see cref="ValueAlias.Sets"/>).</summary>
    public IReadOnlyDictionary<string, bool> ValueSets(string canonicalField, string? printed)
    {
        if (printed is null || !Fields.TryGetValue(canonicalField, out var field) || field.ValueAliases is null)
        {
            return new Dictionary<string, bool>();
        }

        var key = CanonicalComparer.NormalizeText(printed);
        return field.ValueAliases.Values.SelectMany(a => a).FirstOrDefault(a => a.Sets is not null && CanonicalComparer.NormalizeText(a.Value) == key)?.Sets
            ?? new Dictionary<string, bool>();
    }

    /// <summary>
    /// Resolves a printed column header to a canonical field without a model call: (1) exact alias match after
    /// normalization (highest-confidence alias wins); (2) composite headers — one component word plus one measure word
    /// (e.g. "Ind Paid", "Indemnity | Paid" composed from stacked headers) → <c>claim.{component}.{measure}</c>;
    /// a measure word alone → <c>claim.total.{measure}</c>. Returns null when unresolved.
    /// </summary>
    /// <summary>Brief 30 item 43: the text is a label-only alias (a header-block label or a grand-total row label), which column resolution skips.</summary>
    public bool IsLabel(string? text)
    {
        var key = CanonicalComparer.NormalizeText(text);
        return key.Length > 0 && Fields.Values.SelectMany(f => f.Aliases).Any(a => a.AppliesTo is "label" or "grandTotalRow" && CanonicalComparer.NormalizeText(a.Header) == key);
    }

    /// <summary>
    /// Brief 31: a header whose aliases are conditioned on the values' shape resolves by the column's values (most non-blank values a 1–3 digit code →
    /// the <c>code</c> alias, else the <c>word</c> one). Without such aliases, or without values, it is <see cref="ResolveHeader(string?)"/>.
    /// </summary>
    public HeaderMatch? ResolveHeader(string? header, IEnumerable<string?>? values)
    {
        var key = CanonicalComparer.NormalizeText(header);
        var shaped = key.Length == 0 ? [] : Fields.SelectMany(f => f.Value.Aliases.Select(a => (Field: f.Key, Alias: a)))
            .Where(x => x.Alias.ValueShape is not null && x.Alias.AppliesTo is null or "column" && CanonicalComparer.NormalizeText(x.Alias.Header) == key).ToList();
        var printed = (values ?? []).Select(v => v?.Trim()).Where(v => !string.IsNullOrEmpty(v)).ToList();
        if (shaped.Count < 2 || printed.Count == 0)
        {
            return ResolveHeader(header);
        }

        var shape = ShapeOf(printed!);
        return shaped.FirstOrDefault(x => x.Alias.ValueShape == shape) is { Field: not null } hit ? new HeaderMatch(hit.Field, hit.Alias.Confidence, "alias") : ResolveHeader(header);
    }

    /// <summary><c>code</c> when most values are 1–3 digit codes, else <c>word</c>.</summary>
    public static string ShapeOf(IReadOnlyCollection<string> values) =>
        values.Count(v => v.Length is >= 1 and <= 3 && v.All(char.IsAsciiDigit)) * 2 > values.Count ? "code" : "word";

    public HeaderMatch? ResolveHeader(string? header)
    {
        if (ResolveSeedHeader(header) is { } seed)
        {
            return seed;
        }

        var key = CanonicalComparer.NormalizeText(header);
        var learned = key.Length == 0 ? null : Learned.Where(l => l.Status == AliasStatus.Active && CanonicalComparer.NormalizeText(l.Header) == key)
            .OrderByDescending(l => l.Confidence).FirstOrDefault();
        return learned is null ? null : new HeaderMatch(learned.CanonicalField, learned.Confidence, "learned");
    }

    /// <summary>
    /// Resolves every header of one table. A conditional match (e.g. "Loss Paid" → <c>claim.indemnity.paid</c>) is kept only
    /// when another column of the table resolves to its required component (a separate medical column); otherwise it is
    /// unresolved and its index is reported as ambiguous-composite, so the document goes to review with that reason.
    /// </summary>
    public TableHeaderResolution ResolveTableHeaders(IReadOnlyList<string?> headers)
    {
        var matches = headers.Select(ResolveHeader).ToList();
        var ambiguous = new List<int>();
        for (var i = 0; i < matches.Count; i++)
        {
            if (matches[i] is { RequiresComponent: { } required }
                && !matches.Any(m => m is { RequiresComponent: null } && m.Field.StartsWith($"claim.{required}.", StringComparison.Ordinal)))
            {
                matches[i] = null;
                ambiguous.Add(i);
            }
        }

        return new TableHeaderResolution(matches, ambiguous);
    }

    /// <summary>
    /// ADR 0018: a header that names its components joined by "+" or "&amp;" ("Paid CLM + MED + EXP", "Total Incurred (Ind+Med)")
    /// is the total of those components, not one of them. Each joined part must name a component; within such a sum the
    /// claim / loss words (CLM, Claim, Loss, Comp) mean indemnity, since medical and expense are named beside them.
    /// </summary>
    private HeaderMatch? ComponentSum(string header, (string Name, double Confidence) measure)
    {
        if (header.IndexOfAny(['+', '&']) < 0)
        {
            return null;
        }

        var parts = header.Split(['+', '&'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        string[] indemnityInSum = ["clm", "claim", "loss", "losses", "comp", "ind"];
        var components = new List<string>();
        foreach (var part in parts)
        {
            var padded = $" {CanonicalComparer.NormalizeText(part)} ";
            var name = CompositeHeaders.Components
                .SelectMany(w => w.Value.Select(a => (Name: w.Key, Word: CanonicalComparer.NormalizeText(a.Word))))
                .Where(w => padded.Contains($" {w.Word} ", StringComparison.Ordinal))
                .OrderByDescending(w => w.Word.Length)
                .Select(w => w.Name)
                .FirstOrDefault()
                ?? (indemnityInSum.Any(w => padded.Contains($" {w} ", StringComparison.Ordinal)) ? "indemnity" : null);
            if (name is null || components.Contains(name))
            {
                return null;
            }

            components.Add(name);
        }

        return new HeaderMatch($"claim.total.{measure.Name}", measure.Confidence, "component-sum", Components: components);
    }

    /// <summary>The printed header is on the glossary's ignored list (ADR 0017).</summary>
    public bool IsIgnoredHeader(string? header)
    {
        var key = CanonicalComparer.NormalizeText(header);
        return key.Length > 0 && IgnoredHeaders.Headers.Any(h => CanonicalComparer.NormalizeText(h.Header) == key);
    }

    private HeaderMatch? ResolveSeedHeader(string? header)
    {
        var key = CanonicalComparer.NormalizeText(header);
        if (key.Length == 0)
        {
            return null;
        }

        var exact = Fields
            .SelectMany(f => f.Value.Aliases.Select(a => (Field: f.Key, Alias: a)))
            .Where(x => x.Alias.AppliesTo is null or "column" && CanonicalComparer.NormalizeText(x.Alias.Header) == key)
            .OrderByDescending(x => x.Alias.Confidence)
            .FirstOrDefault();
        if (exact.Field is not null)
        {
            return new HeaderMatch(exact.Field, exact.Alias.Confidence, "alias");
        }

        var padded = $" {key} ";
        static (string Name, double Confidence)? Find(string padded, Dictionary<string, List<WordAlias>> words) =>
            words.SelectMany(w => w.Value.Select(a => (Name: w.Key, a.Word, a.Confidence)))
                .Where(w => padded.Contains($" {CanonicalComparer.NormalizeText(w.Word)} ", StringComparison.Ordinal))
                .OrderByDescending(w => CanonicalComparer.NormalizeText(w.Word).Length)
                .ThenByDescending(w => w.Confidence)
                .Select(w => ((string, double)?)(w.Name, w.Confidence))
                .FirstOrDefault();

        var measure = Find(padded, CompositeHeaders.Measures);
        if (measure is null)
        {
            return null;
        }

        if (ComponentSum(header!, measure.Value) is { } sum)
        {
            return sum;
        }

        var component = Find(padded, CompositeHeaders.Components);
        if (component is { } c)
        {
            return new HeaderMatch($"claim.{c.Name}.{measure.Value.Name}", Math.Min(c.Confidence, measure.Value.Confidence), "composite");
        }

        // A conditional component word ("Loss Paid") resolves only in the context of its table (brief 17 Answers 2):
        // ResolveTableHeaders keeps it when the table has the required column and marks it ambiguous otherwise.
        var conditional = CompositeHeaders.ConditionalComponents.Words
            .Where(w => padded.Contains($" {CanonicalComparer.NormalizeText(w.Word)} ", StringComparison.Ordinal))
            .OrderByDescending(w => CanonicalComparer.NormalizeText(w.Word).Length)
            .FirstOrDefault();
        if (conditional is not null)
        {
            return new HeaderMatch($"claim.{conditional.Component}.{measure.Value.Name}", measure.Value.Confidence, "conditional", conditional.RequiresComponent);
        }

        // A measure word alone means the total — but only when nothing else is printed ("Exp Paid" with an unknown
        // component must stay unresolved rather than be guessed as the total).
        var measureWords = CompositeHeaders.Measures[measure.Value.Name].Select(w => CanonicalComparer.NormalizeText(w.Word)).ToList();
        var leftover = key.Split(' ').Where(t => t is not ("total" or "to" or "date" or "amount" or "amt") && !measureWords.Any(m => m.Split(' ').Contains(t)));
        return leftover.Any() ? null : new HeaderMatch($"claim.total.{measure.Value.Name}", measure.Value.Confidence * 0.8, "measure-only");
    }
}

/// <param name="Field">Canonical glossary field, e.g. <c>claim.indemnity.paid</c>.</param>
/// <param name="Confidence">Prior confidence of the match.</param>
/// <param name="Method">alias | composite | component-sum | conditional | measure-only | learned.</param>
/// <param name="RequiresComponent">For a conditional match: the component another column of the same table must resolve to.</param>
/// <param name="Components">For a component-sum match (ADR 0018): the components the header names, in printed order.</param>
public sealed record HeaderMatch(string Field, double Confidence, string Method, string? RequiresComponent = null, IReadOnlyList<string>? Components = null);

/// <summary>Header resolution for one table (brief 17 Answers 2): <paramref name="Ambiguous"/> lists the column indexes whose
/// conditional component word has no reading in this table (marked ambiguous-composite, not mapped).</summary>
public sealed record TableHeaderResolution(IReadOnlyList<HeaderMatch?> Matches, IReadOnlyList<int> Ambiguous);

/// <summary>
/// Learned glossary aliases (ADR 0019): one entry per printed header (normalized) and canonical field, with its lifecycle and
/// verification evidence. Sidecar <c>glossary-learned.json</c> in the Blob and directory registries; <c>dbo.GlossaryAlias</c>
/// and <c>dbo.GlossaryAliasEvidence</c> in SQL; exported for review as <c>glossary.learned.json</c>.
/// </summary>
public sealed class LearnedGlossary
{
    /// <summary>0.2.0 (brief 21): status, evidence, activation and retirement. 0.1.0 sidecars load as candidates (<see cref="Upgrade"/>).</summary>
    public const string CurrentVersion = "glossary-learned-0.2.0";

    public string SidecarVersion { get; set; } = CurrentVersion;
    public List<LearnedMapping> Mappings { get; set; } = [];

    /// <summary>The aliases a confirm-format review decides: those the format introduced (the learn that recorded them).</summary>
    public IEnumerable<LearnedMapping> IntroducedBy(string formatId) => Mappings.Where(m => m.FormatId == formatId);

    public LearnedMapping? Find(string header, string canonicalField) =>
        Mappings.FirstOrDefault(m => m.Key == LearnedMapping.KeyOf(header, canonicalField));

    /// <summary>
    /// A 0.1.0 sidecar held one row per format that learned a mapping and no lifecycle: rows are merged per alias key and each
    /// becomes a candidate with its first sighting as evidence. Idempotent on a 0.2.0 glossary.
    /// </summary>
    public LearnedGlossary Upgrade()
    {
        if (SidecarVersion == CurrentVersion && Mappings.All(m => m.Evidence.Count > 0 && m.Status.Length > 0 && m.Id is not null))
        {
            return this;
        }

        var merged = new List<LearnedMapping>();
        foreach (var row in Mappings.OrderBy(m => m.FirstSeenUtc))
        {
            row.Status = string.IsNullOrEmpty(row.Status) ? AliasStatus.Candidate : row.Status;
            row.Id ??= row.Key;
            if (row.Evidence.Count == 0)
            {
                row.Evidence.Add(new AliasEvidence { DocumentId = row.FirstSeenDocumentId, FormatRef = $"{row.FormatId}@{row.FormatVersion}", Route = "learn", AtUtc = row.FirstSeenUtc });
            }

            if (merged.FirstOrDefault(m => m.Key == row.Key) is { } existing)
            {
                existing.Evidence.AddRange(row.Evidence.Where(e => existing.Evidence.All(x => x.DocumentId != e.DocumentId)));
            }
            else
            {
                merged.Add(row);
            }
        }

        return new LearnedGlossary { SidecarVersion = CurrentVersion, Mappings = merged };
    }
}

/// <summary>ADR 0019 lifecycle: a candidate is never used; an active alias resolves headers like a hand alias; a retired one never again.</summary>
public static class AliasStatus
{
    public const string Candidate = "candidate";
    public const string Active = "active";
    public const string Retired = "retired";
}

/// <summary>
/// A learned alias (ADR 0019). <see cref="FormatId"/>, <see cref="FormatVersion"/> and <see cref="FirstSeenDocumentId"/> name the
/// learn that introduced it.
/// </summary>
public sealed class LearnedMapping
{
    /// <summary>Stable id: the alias key at creation (<see cref="KeyOf"/>); kept when a reviewer edits the field.</summary>
    public string? Id { get; set; }

    public string Header { get; set; } = "";
    public string CanonicalField { get; set; } = "";
    public string CarrierName { get; set; } = "";
    public string FormatId { get; set; } = "";
    public string FormatVersion { get; set; } = "";
    public LearnedBy LearnedBy { get; set; } = new();
    public string? ReviewedBy { get; set; }
    public double Confidence { get; set; }
    public string FirstSeenDocumentId { get; set; } = "";
    public DateTimeOffset FirstSeenUtc { get; set; }

    /// <summary><see cref="AliasStatus"/>; empty in 0.1.0 sidecars (read as candidate).</summary>
    public string Status { get; set; } = AliasStatus.Candidate;

    /// <summary>Whether the learn that introduced the alias tied to printed totals, and the gate that published it.</summary>
    public bool? TotalsTie { get; set; }
    public string? VerificationGate { get; set; }

    /// <summary>Every document that verified with a spec mapping this alias (the introducing learn first).</summary>
    public List<AliasEvidence> Evidence { get; set; } = [];

    public string? ActivatedBy { get; set; }
    public DateTimeOffset? ActivatedUtc { get; set; }

    /// <summary>"reviewer" (confirm-format review) or "verified on n documents across m formats".</summary>
    public string? ActivationReason { get; set; }

    public string? RetiredBy { get; set; }
    public DateTimeOffset? RetiredUtc { get; set; }
    public string? RetiredReason { get; set; }

    /// <summary>The audit event of the correction that retired the alias (the corrected value's lineage traced to it).</summary>
    public string? RetiredAuditEventId { get; set; }

    /// <summary>The field the learn mapped, when a reviewer edited the alias to another field.</summary>
    public string? EditedFrom { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Key => KeyOf(Header, CanonicalField);

    public static string KeyOf(string header, string canonicalField) => $"{CanonicalComparer.NormalizeText(header)}|{canonicalField}";

    [System.Text.Json.Serialization.JsonIgnore]
    public int DistinctDocuments => Evidence.Select(e => e.DocumentId).Distinct(StringComparer.Ordinal).Count();

    [System.Text.Json.Serialization.JsonIgnore]
    public int DistinctFormats => Evidence.Select(e => e.FormatRef.Split('@')[0]).Distinct(StringComparer.Ordinal).Count();
}

/// <summary>One verification of a learned alias: a document whose spec maps it verified (learn published or replay accepted).</summary>
public sealed class AliasEvidence
{
    public string DocumentId { get; set; } = "";
    public string FormatRef { get; set; } = "";

    /// <summary>learn | replay.</summary>
    public string Route { get; set; } = "";
    public bool? TotalsTie { get; set; }
    public DateTimeOffset AtUtc { get; set; }
}

public sealed class LearnedBy
{
    public string Model { get; set; } = "";
    public string? ModelVersion { get; set; }
    public string PromptVersion { get; set; } = "";
}

public sealed class GlossaryField
{
    public List<HeaderAlias> Aliases { get; set; } = [];
    public Dictionary<string, List<ValueAlias>>? ValueAliases { get; set; }
}

public sealed class HeaderAlias
{
    /// <summary>
    /// Brief 30 item 43 (ADR 0026: a table-dependent meaning is a conditional alias): null or <c>column</c> = a table column header;
    /// <c>label</c> = a header-block label (the document's own field), <c>grandTotalRow</c> = a label on the grand-total row. Column
    /// resolution never returns a label-only meaning ("Report Date" as a column is the claim's date reported, not the run date).
    /// </summary>
    public string? AppliesTo { get; set; }

    /// <summary>
    /// Brief 31 (ADR 0026: a meaning that depends on the table is a conditional alias): <c>code</c> or <c>word</c> when one header names two fields by the
    /// shape of the values under it ("Injury Type": 2-digit stat-plan codes → <c>claim.injuryTypeCode</c>, words → <c>claim.claimType</c>). Null: no condition.
    /// </summary>
    public string? ValueShape { get; set; }

    public string Header { get; set; } = "";
    public string Source { get; set; } = "";
    public double Confidence { get; set; }
    public string? Note { get; set; }
}

public sealed class ValueAlias
{
    /// <summary>Brief 30 item 43: <c>candidate</c> = recorded but not resolved (an unverified meaning); null = active.</summary>
    public string? Status { get; set; }

    public string Value { get; set; } = "";
    public string Source { get; set; } = "";
    public double Confidence { get; set; }
    public string? Note { get; set; }

    /// <summary>Brief 22 Answers 4: claim flags this printed value also states (status "X", census only → <c>claim.reportOnly</c>: true).</summary>
    public Dictionary<string, bool>? Sets { get; set; }
}

public sealed class CompositeHeaders
{
    public string? Description { get; set; }
    public Dictionary<string, List<WordAlias>> Components { get; set; } = [];
    public Dictionary<string, List<WordAlias>> Measures { get; set; } = [];

    /// <summary>Component words whose meaning depends on the table (brief 17 Answers 2).</summary>
    public ConditionalComponents ConditionalComponents { get; set; } = new();
}

public sealed class ConditionalComponents
{
    public string? Description { get; set; }
    public List<ConditionalWord> Words { get; set; } = [];
}

/// <summary>A component word that means <see cref="Component"/> only beside a column of <see cref="RequiresComponent"/>.</summary>
public sealed class ConditionalWord
{
    public string Word { get; set; } = "";
    public string Component { get; set; } = "";
    public string RequiresComponent { get; set; } = "";
    public string Source { get; set; } = "";
    public string? Note { get; set; }
}

public sealed class IgnoredHeaders
{
    public string? Description { get; set; }
    public List<IgnoredHeader> Headers { get; set; } = [];
}

public sealed class IgnoredHeader
{
    public string Header { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Source { get; set; } = "";
}

public sealed class WordAlias
{
    public string Word { get; set; } = "";
    public string Source { get; set; } = "";
    public double Confidence { get; set; }
    public string? Note { get; set; }
}

public sealed class CarrierProvenance
{
    public string CarrierId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Note { get; set; }
    public List<JsonElement> LearnedMappings { get; set; } = [];
}

public sealed class CodeSets
{
    public string CodeSetsVersion { get; set; } = "";
    public Dictionary<string, CodeTable> Tables { get; set; } = [];

    /// <summary>Code width for a code system (left-zero padding), or null when the system is unknown.</summary>
    public int? CodeWidth(string? codeSystem) =>
        codeSystem is not null && Tables.TryGetValue(codeSystem, out var table) ? table.CodeWidth : null;
}

public sealed class CodeTable
{
    public string System { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public int CodeWidth { get; set; }
    public string Source { get; set; } = "";
    public string? SourceUrl { get; set; }
    public DateOnly? RetrievedOn { get; set; }
    public string Completeness { get; set; } = "";
    public string? CompletenessNote { get; set; }
    public List<CodeGroup>? Groups { get; set; }
    public List<CodeEntry> Codes { get; set; } = [];
}

public sealed class CodeGroup
{
    public string Name { get; set; } = "";
    public string? From { get; set; }
    public string? To { get; set; }
    public List<string>? Codes { get; set; }
    public bool? CumulativeTrauma { get; set; }
}

public sealed class CodeEntry
{
    public string Code { get; set; } = "";
    public string? Description { get; set; }
    public string? Group { get; set; }
    public string? ClaimType { get; set; }
    public string? Status { get; set; }
    public bool? CumulativeTrauma { get; set; }
    public bool? Subrogation { get; set; }
    public bool? Verified { get; set; }
    public string? Note { get; set; }
}

/// <summary>docs/evaluation/line-of-business.json (ADR 0020): printed terms of the lines of business out of the POC's scope.</summary>
public sealed class LineOfBusinessVocabulary
{
    public string LineOfBusinessVersion { get; set; } = "";
    public string InScope { get; set; } = "";
    public List<string> Labels { get; set; } = [];
    public Dictionary<string, List<string>> Lines { get; set; } = [];
    public List<string> Ambiguous { get; set; } = [];

    /// <summary>Brief 23 §2: signals that classify a claim row (claim-type codes), with provenance.</summary>
    public List<RowSignal> RowSignals { get; set; } = [];

    /// <summary>Glossary document.lineOfBusiness value keys → this file's lines (WC and employers liability are in scope).</summary>
    private static readonly Dictionary<string, string> GlossaryLines = new(StringComparer.Ordinal)
    {
        ["WC"] = "workersCompensation", ["EL"] = "workersCompensation", ["GL"] = "generalLiability", ["AL"] = "auto",
    };

    /// <summary>
    /// Brief 23 §2: the line of business one printed row value names, with the evidence; null when it names none. For
    /// <c>claim.lineOfBusiness</c>: the glossary's value aliases (WC, GL, AL, …), then this file's line terms. For
    /// <c>claim.claimType</c>: a workers' comp claim type the glossary resolves is in scope; else a row signal's pattern.
    /// </summary>
    public (string Line, string Evidence)? RowLine(string field, string? printed, Glossary glossary)
    {
        var text = printed?.Trim() ?? "";
        if (text.Length == 0)
        {
            return null;
        }

        if (field == "claim.claimType" && glossary.ResolveValue("claim.claimType", text) is null
            && !RowSignals.Any(s => s.Field == field && System.Text.RegularExpressions.Regex.IsMatch(text, s.Pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            && RowLine("claim.lineOfBusiness", text, glossary) is { } asLine)
        {
            // A claim-type column that prints a line code ("GL", "Auto") names the row's line.
            return (asLine.Line, $"claim type \"{text}\"");
        }

        if (field == "claim.lineOfBusiness")
        {
            if (glossary.ResolveValue("document.lineOfBusiness", text) is { } key && GlossaryLines.TryGetValue(key, out var mapped))
            {
                return (mapped, $"line \"{text}\"");
            }

            var normalized = CanonicalComparer.NormalizeText(text);
            foreach (var (line, terms) in Lines)
            {
                if (terms.Any(t => CanonicalComparer.NormalizeText(t) == normalized))
                {
                    return (line, $"line \"{text}\"");
                }
            }

            return null;
        }

        if (field == "claim.claimType" && glossary.ResolveValue("claim.claimType", text) is not null)
        {
            return (InScope, $"claim type \"{text}\"");
        }

        var signal = RowSignals.FirstOrDefault(s => s.Field == field && System.Text.RegularExpressions.Regex.IsMatch(text, s.Pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        return signal is null ? null : (signal.Line, $"{(field == "claim.claimType" ? "claim-type code" : field)} \"{text}\"");
    }
}

/// <summary>A row-level line-of-business signal (line-of-business.json rowSignals).</summary>
public sealed class RowSignal
{
    public string Field { get; set; } = "";
    public string Pattern { get; set; } = "";
    public string Line { get; set; } = "";
    public string? Source { get; set; }
    public string? Note { get; set; }
}
