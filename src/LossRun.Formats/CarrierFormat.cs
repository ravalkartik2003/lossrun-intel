using System.Text.Json;
using System.Text.Json.Serialization;
using LossRun.Contracts;

namespace LossRun.Formats;

// A versioned, immutable carrier format spec (docs/evaluation/carrier-format.schema.json; design in
// docs/architecture/carrier-format-memory.md KQ2). Written by hand (this brief) or by the learn path (brief 06);
// replayed deterministically with zero model calls. Never edited once published: a change is a new version.

public sealed class CarrierFormat
{
    /// <summary>
    /// Written by this code. 0.2.0 adds the <c>learning</c> record; 0.3.0 (ADR 0016/0017) adds <c>tableLocator.rowShape</c>,
    /// <c>periods.kind</c> and <c>periods.noLossesPattern</c>; 0.4.0 (ADR 0018) adds <c>tableLocator.records</c> (multi-line
    /// claim records), column-map <c>line</c> / <c>under</c> / <c>part</c> / <c>partPattern</c> / <c>components</c>,
    /// <c>claimStatusRule</c> and the <c>labelNext</c> locator; 0.5.0 (brief 19 Answers 1–2) replaces <c>claimStatusRule</c> with
    /// <c>statusFrom</c> and adds <c>absentComponents</c>. Older specs still load.
    /// </summary>
    public const string SpecVersion = "carrier-format-0.5.0";

    public string SpecSchema { get; set; } = SpecVersion;
    public string FormatId { get; set; } = "";
    public int Version { get; set; }
    public string CarrierName { get; set; } = "";

    /// <summary>
    /// Brief 35 Answers 2: the carrier's other legal names, added by a reviewer who answered "same carrier" in a review
    /// (<c>formatChanges carrierNames.add</c>). One carrier group prints several ("X Indemnity Co", "X Casualty Insurance Co").
    /// </summary>
    public List<string> CarrierNames { get; set; } = [];

    /// <summary>The learned carrier name and every added one.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<string> KnownCarrierNames => new[] { CarrierName }.Concat(CarrierNames).Where(n => !string.IsNullOrWhiteSpace(n));

    /// <summary><c>pdf</c> (PDF and image pages through CU) or <c>spreadsheet</c> (native XLS/XLSX/CSV).</summary>
    public string FileKind { get; set; } = "";

    /// <summary><c>hand</c>, <c>learn:{model}@{modelVersion}</c> or <c>delta:{model}@{modelVersion}</c>.</summary>
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string? ReviewedBy { get; set; }
    public string? Description { get; set; }

    /// <summary>Format this one supersedes (previous version of the same formatId, or another format id).</summary>
    public string? Supersedes { get; set; }

    /// <summary>
    /// Status at publication: <c>shadow</c> (first learned version of a new carrier) or <c>active</c>; null means active
    /// (hand specs). The live status is in the stats sidecar (<see cref="FieldStats.Lifecycle"/>), because a published
    /// spec never changes; see <see cref="FormatLifecycle"/>.
    /// </summary>
    public string? Status { get; set; }

    public FormatFingerprintSpec Fingerprint { get; set; } = new();
    public TableLocator TableLocator { get; set; } = new();
    public List<ColumnMapping> ColumnMap { get; set; } = [];

    /// <summary>Printed headers deliberately not mapped (so they are not reported as unmapped).</summary>
    public List<string> IgnoredHeaders { get; set; } = [];
    public PeriodSpec Periods { get; set; } = new();
    public List<DocumentFieldSpec> DocumentFields { get; set; } = [];
    public List<DerivedRule> DerivedRules { get; set; } = [];

    /// <summary>
    /// How a claim's status is read when the run prints none (brief 19 Answers 2; per format, never assumed):
    /// <c>closeDate</c> — closed when a close date is printed, open otherwise, kept only when the learn loop verified it
    /// against printed open/closed counts; <c>unknown</c> — status <c>unknown</c> on every claim (R-30 flags it).
    /// Null: status only from a mapped column.
    /// </summary>
    public string? StatusFrom { get; set; }

    /// <summary>Loss components the run prints no column for (brief 19 Answers 1): e.g. ["medical"].</summary>
    public List<string> AbsentComponents { get; set; } = [];
    public PeriodBoundaries PeriodBoundaries { get; set; } = new();
    public ValidationSpec Validation { get; set; } = new();

    /// <summary>How a learned spec was produced (null for hand-written specs): model, prompt, iterations, tokens.</summary>
    public LearningRecord? Learning { get; set; }

    [JsonIgnore]
    public string Ref => $"{FormatId}@{Version}";
}

public sealed class LearningRecord
{
    /// <summary><c>learn</c> or <c>delta</c>.</summary>
    public string Stage { get; set; } = "";
    public string Model { get; set; } = "";
    public string? ModelVersion { get; set; }
    public string PromptVersion { get; set; } = "";
    public int Iterations { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public bool PageImage { get; set; }
    public string SourceDocumentId { get; set; } = "";

    /// <summary>Verification the spec passed before publication (rules, unmapped headers, totals).</summary>
    public string Verification { get; set; } = "";

    /// <summary>Gate that admitted the spec: <c>pdf-totals</c>, <c>spreadsheet-totals</c> or <c>spreadsheet-rules</c> (06 Answers 2).</summary>
    public string? VerificationGate { get; set; }

    /// <summary>
    /// Rendering of the exemplar the version was learned from: <c>native</c>, <c>scan-300</c> (resolution rounded to
    /// 50 dpi), <c>scan</c> (resolution unknown) or <c>spreadsheet</c> (08b Answers 5).
    /// </summary>
    public string? LearnedFrom { get; set; }

    public static string? Rendering(SourceFile source) => source.Kind switch
    {
        FileKind.Pdf when source.TextLayer == true => "native",
        FileKind.Pdf when source.TextLayer == false => source.ScanDpi is { } dpi ? $"scan-{(int)Math.Round(dpi / 50.0) * 50}" : "scan",
        FileKind.Pdf => null,
        _ => "spreadsheet",
    };
}

public sealed class FormatFingerprintSpec
{
    /// <summary>sha256 of the normalized identity text (title/page headers, or spreadsheet title + sheet names), digits removed.</summary>
    public string IdentityKey { get; set; } = "";

    /// <summary>sha256 of the normalized first-table header tuple (+ sheet count for spreadsheets).</summary>
    public string HeaderKey { get; set; } = "";

    /// <summary>The normalized texts the keys were computed from (for drift diffs and review).</summary>
    public string IdentityText { get; set; } = "";
    public string HeaderText { get; set; } = "";

    /// <summary>Semantic tier: centroid stored at formats/{formatId}/centroid.json; null for spreadsheets.</summary>
    public EmbeddingTier? Embedding { get; set; }
}

public sealed class EmbeddingTier
{
    public string Model { get; set; } = "";
    public double Threshold { get; set; } = 0.95;
    public string TextNormalization { get; set; } = FingerprintText.Normalization;
}

public sealed class TableLocator
{
    /// <summary>Spreadsheet sheet holding the claim table; null for PDFs.</summary>
    public string? Sheet { get; set; }

    /// <summary>Share of the column map's distinct source headers a table must show to be used.</summary>
    public double HeaderMatchMin { get; set; } = 0.8;

    /// <summary>PDFs: paragraph that opens a policy-period block (named groups policyNumber, effectiveDate, expirationDate).</summary>
    public string? BlockAnchor { get; set; }

    public List<RowKindRule> RowKinds { get; set; } = [];
    public LineFallback? LineFallback { get; set; }

    /// <summary>
    /// <c>claims</c> (default): each data row is a claim. <c>periodSummary</c> (ADR 0017): each data row is a period of a
    /// summary-only run (fiscal, report or policy year); money and count columns are its printed subtotals.
    /// </summary>
    public string RowShape { get; set; } = "claims";

    /// <summary>ADR 0018: claim records printed on more than one line; null when every claim is one row.</summary>
    public RecordSpec? Records { get; set; }
}

/// <summary>
/// ADR 0018 (P2): a claim record spanning several printed lines.
/// <c>rows</c>: a row with a value under <see cref="KeySource"/> opens a record; the rows after it without one (up to
/// <see cref="MaxLines"/>, a total row or the next key row) are its lines 1, 2, …, read by column-map entries with
/// <c>line</c> (and <c>under</c>: the line-0 header the value sits under).
/// <c>lines</c>: each claim row owns the loose lines printed after it (paragraphs and label tables outside the claim table,
/// up to the next claim row, a total line or <see cref="MaxLines"/>); <see cref="LineFields"/> read them. A loose line a
/// row-kind regex matches is a total row, its amounts assigned to columns by position.
/// <c>measures</c> (brief 22 Answers 1): a claim (or total) prints one row per measure, each row labelled with its measure
/// ("Inc:", "Pd:", "O/S:") under component columns ("Total | Claim | Medical | Expense"). A key row or a total row opens the
/// record; the labelled rows after it are its measures. Column-map entries targeting a component without a measure
/// (<c>claim.indemnity</c>, <c>claim.total</c>) read that component's value on each measure row.
/// </summary>
public sealed class RecordSpec
{
    public string Mode { get; set; } = "rows";
    public string? KeySource { get; set; }
    public int MaxLines { get; set; } = 8;
    public List<LineFieldSpec> LineFields { get; set; } = [];

    /// <summary>measures: regexes over a row's measure label; null uses the <see cref="LossRun.Contracts.MeasureLabels"/> defaults.</summary>
    public MeasureLabels? MeasureLabels { get; set; }
}

/// <summary>A value read from a record's loose lines: the first line matching <see cref="Pattern"/>, or the line at <see cref="Offset"/>.</summary>
public sealed class LineFieldSpec
{
    /// <summary>.NET regex; group <c>value</c> is the value (else the whole match).</summary>
    public string Pattern { get; set; } = "";
    public string Target { get; set; } = "";
    public NormalizerSpec Normalizer { get; set; } = new();

    /// <summary>0-based index of the loose line after the claim row (negative counts from the record's last line); null: any line.</summary>
    public int? Offset { get; set; }
}

/// <summary>Classifies a non-claim row by a regex over the row's text (cells joined in column order).</summary>
public sealed class RowKindRule
{
    /// <summary><c>subtotal</c> (period subtotals), <c>grandTotal</c> (report totals) or <c>skip</c>.</summary>
    public string Kind { get; set; } = "";
    public string Regex { get; set; } = "";

    /// <summary>Optional regex with named groups claimCount, openCount, closedCount over the row text.</summary>
    public string? CountsRegex { get; set; }
}

/// <summary>Rows from y-clustered OCR words when CU returned no table for a block (brief 04 Answers 2).</summary>
public sealed class LineFallback
{
    public bool Enabled { get; set; } = true;

    /// <summary>Rotate word coordinates by the page angle CU reports before clustering into lines.</summary>
    public bool Deskew { get; set; } = true;

    /// <summary>Words belong to the same line when their centre y differs by at most this fraction of the median word height.</summary>
    public double LineTolerance { get; set; } = 0.5;

    /// <summary>Share of the column map's headers that must be found in a line for it to be the header line.</summary>
    public double MinHeaderMatch { get; set; } = 0.7;

    /// <summary><c>headerLeftEdge</c>: a word belongs to the rightmost column whose header starts at or left of the word's centre.</summary>
    public string Assignment { get; set; } = "headerLeftEdge";
}

public sealed class ColumnMapping
{
    /// <summary>Printed header text (matched after normalization); stacked headers are matched in composed form ("Indemnity Paid").</summary>
    public string Source { get; set; } = "";

    /// <summary>Canonical path: <c>claim.*</c> (row values; money columns also fill subtotal/total rows).</summary>
    public string Target { get; set; } = "";
    public NormalizerSpec Normalizer { get; set; } = new();

    /// <summary>Printed values that mean "absent" (e.g. "", "N/A"); the field stays null.</summary>
    public List<string> NullWhen { get; set; } = [];

    /// <summary>ADR 0018 records: the record line the value is printed on (0 = the key line).</summary>
    public int Line { get; set; }

    /// <summary>ADR 0018 records: for <see cref="Line"/> ≥ 1, the line-0 header the value is printed under.</summary>
    public string? Under { get; set; }

    /// <summary>ADR 0018 (P3): position of the value in a stacked cell ("Claim Number" over "Claim Type"); null = the whole cell.</summary>
    public int? Part { get; set; }

    /// <summary>Regex with groups p0, p1, … splitting a stacked cell printed on one line; default: the cell's printed lines.</summary>
    public string? PartPattern { get; set; }

    /// <summary>ADR 0018 (composite headers): the components a total column names ("Total Incurred (Ind+Med)" → indemnity, medical).</summary>
    public List<string>? Components { get; set; }

    /// <summary>
    /// Records mode <c>measures</c>: the measure this expanded entry reads (paid, reserve, incurred); set by replay when it expands
    /// a component target (<c>claim.indemnity</c>) into one entry per measure. Never serialized.
    /// </summary>
    internal string? RowMeasure { get; set; }
}

public sealed class NormalizerSpec
{
    /// <summary><c>text</c>, <c>date</c>, <c>currency</c>, <c>integer</c>, <c>enum</c>, <c>claimNumber</c>, <c>code</c>, <c>boolean</c>.</summary>
    public string Type { get; set; } = "text";

    /// <summary>enum: glossary field whose valueAliases map printed values (e.g. <c>claim.status</c>).</summary>
    public string? GlossaryField { get; set; }

    /// <summary>claimNumber: optional regex (named groups) and format to rebuild split OCR tokens, e.g. "WC 22-0001" → "WC-22-0001".</summary>
    public string? Pattern { get; set; }
    public string? Format { get; set; }

    /// <summary>code: code system (for padding to its width).</summary>
    public string? CodeSystem { get; set; }
}

public sealed class PeriodSpec
{
    /// <summary><c>blockAnchor</c> (PDF period headings) or <c>column</c> (a policy-number column on each row).</summary>
    public string Source { get; set; } = "blockAnchor";
    public string? Column { get; set; }
    public PeriodLookup? Lookup { get; set; }

    /// <summary>What the periods are (canonical <c>periodKind</c>: policyTerm, fiscalYear, reportYear, accidentYear, calendarYear); ADR 0017.</summary>
    public string? Kind { get; set; }

    /// <summary>Regex for this carrier's per-period "no losses" statement; null uses the default (ADR 0016).</summary>
    public string? NoLossesPattern { get; set; }

    /// <summary>
    /// Brief 30 item 20 (<c>source: column</c>): the layout prints the policy number on a group's first row only, so a row with a blank
    /// policy cell belongs to the period above. False (the default): such a row is still attached there, but as an issue the router
    /// sends to Review, with a <c>periodAssignment</c> lineage note.
    /// </summary>
    public bool FillDown { get; set; }
}

/// <summary>Spreadsheet summary table keyed by policy number: dates and printed subtotals per period.</summary>
public sealed class PeriodLookup
{
    public string Sheet { get; set; } = "";
    public string KeyHeader { get; set; } = "";
    public string? RangeHeader { get; set; }

    /// <summary>Regex with named groups effectiveDate and expirationDate over the range cell.</summary>
    public string? RangeRegex { get; set; }

    /// <summary>Separate effective / expiration date columns (brief 08: summary sheets that do not print the period as one range).</summary>
    public string? EffectiveHeader { get; set; }
    public string? ExpirationHeader { get; set; }

    /// <summary>Summary header → subtotal path (claimCount, openCount, closedCount, total.incurred, …).</summary>
    public Dictionary<string, string> Subtotals { get; set; } = [];
}

public sealed class DocumentFieldSpec
{
    /// <summary>Canonical path: <c>document.*</c>.</summary>
    public string Target { get; set; } = "";
    public FieldLocator Locator { get; set; } = new();
    public NormalizerSpec Normalizer { get; set; } = new();

    /// <summary>Why a constant is declared (required for <c>const</c> locators; shown in review).</summary>
    public string? Reason { get; set; }
}

public sealed class FieldLocator
{
    /// <summary><c>paragraphRole</c>, <c>regex</c> (group "value"), <c>regexPresent</c>, <c>cell</c> (sheet + a1), <c>labelRight</c> (sheet + label), <c>labelNext</c> (PDF: the paragraph printed after the label paragraph; ADR 0018), <c>const</c>.</summary>
    public string Type { get; set; } = "";
    public string? Role { get; set; }
    public string? Pattern { get; set; }
    public string? Sheet { get; set; }
    public string? A1 { get; set; }
    public string? Label { get; set; }
    public JsonElement? Value { get; set; }
}

/// <summary>A value computed from others when the carrier does not print it, e.g. indemnity incurred = paid + reserve.</summary>
public sealed class DerivedRule
{
    /// <summary>Path relative to a claim or totals block, e.g. <c>indemnity.incurred</c>.</summary>
    public string Target { get; set; } = "";

    /// <summary>Operands summed strictly (all must be present).</summary>
    public List<string> Sum { get; set; } = [];

    /// <summary><c>absent</c>: only when the target was not printed.</summary>
    public string When { get; set; } = "absent";

    /// <summary>Apply to claim rows, subtotal/total rows, or both.</summary>
    public List<string> AppliesTo { get; set; } = ["claim", "totals"];
}

public sealed class PeriodBoundaries
{
    /// <summary>True when the run prints the last covered day (06/30) as the expiration; replay adds one day.</summary>
    public bool ExpirationInclusive { get; set; }
}

public sealed class ValidationSpec
{
    /// <summary>Rules this format is expected to make applicable (a drop in applicability is a drift signal).</summary>
    public List<string> ExpectedApplicable { get; set; } = [];
    public List<SoftOverride> SoftOverrides { get; set; } = [];
}

public sealed class SoftOverride
{
    public string RuleId { get; set; } = "";
    public string Reason { get; set; } = "";
}

/// <summary>Mutable sidecar (formats/{formatId}/stats.json): CACHE-ED2-style per-field success counters.</summary>
public sealed class FieldStats
{
    public string FormatId { get; set; } = "";
    public Dictionary<string, FieldStat> Fields { get; set; } = [];

    /// <summary>Live status per published version ("1", "2", …): shadow → active (06 Answers 4).</summary>
    public Dictionary<string, VersionLifecycle> Lifecycle { get; set; } = [];

    /// <summary>Cosines of accepted documents to the format's centroid (08b): the adaptive embedding threshold.</summary>
    public EmbeddingHits? Embedding { get; set; }
}

/// <summary>
/// Running mean/variance (Welford) of the cosine between accepted documents and the format centroid. After
/// <see cref="MinHits"/> hits the format's embedding threshold becomes max(<see cref="Floor"/>, mean − 2σ) (08 Answers 2):
/// noisy renderings of a known format stop falling below a fixed 0.95. Kept in the stats sidecar / SQL, never in the spec.
/// </summary>
public sealed class EmbeddingHits
{
    public const int MinHits = 5;
    public const double Floor = 0.90;

    public int Count { get; set; }
    public double Mean { get; set; }
    public double M2 { get; set; }

    public double StdDev => Count < 2 ? 0 : Math.Sqrt(M2 / (Count - 1));

    public void Add(double cosine)
    {
        Count++;
        var delta = cosine - Mean;
        Mean += delta / Count;
        M2 += delta * (cosine - Mean);
    }

    /// <summary>The adaptive threshold, or null while fewer than <see cref="MinHits"/> documents have hit the format.</summary>
    public double? Threshold() => Count < MinHits ? null : Math.Max(Floor, Math.Round(Mean - 2 * StdDev, 4));
}

public sealed class VersionLifecycle
{
    /// <summary><c>shadow</c> or <c>active</c>.</summary>
    public string Status { get; set; } = FormatLifecycle.Active;

    /// <summary>ReplayAccepted documents while in shadow (3 flips the version to active).</summary>
    public int CleanReplays { get; set; }

    /// <summary><c>clean-replays</c>, <c>approval:{user}</c>, or <c>inherited:{formatId@version}</c>.</summary>
    public string? ActivatedBy { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }

    /// <summary>Who retired the version and why (status <c>retired</c>: hidden from matching, kept for lineage).</summary>
    public string? RetiredBy { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
    public string? RetiredReason { get; set; }
}

/// <summary>
/// Format status lifecycle (06 Answers 4). The first learned version of a new carrier is published as <c>shadow</c>:
/// its replays are ReplayAccepted for processing and also queued as "confirm new format" review items until an
/// underwriter approves it (brief 10) or <see cref="ShadowReplays"/> clean replays flip it to <c>active</c>. Drift and
/// delta versions inherit the live status of the version they supersede.
/// </summary>
public static class FormatLifecycle
{
    public const string Shadow = "shadow";
    public const string Active = "active";

    /// <summary>Hidden from fingerprint matching; the spec stays published so documents that used it keep their lineage (07 Answers 4).</summary>
    public const string Retired = "retired";

    public const int ShadowReplays = 3;

    /// <summary>Retires one version (idempotent). Published blobs are never deleted.</summary>
    public static Task RetireAsync(IFormatRegistry registry, string formatId, int version, string retiredBy, string reason, DateTimeOffset at,
        CancellationToken cancellationToken = default) =>
        registry.UpdateStatsAsync(formatId, stats =>
        {
            var key = version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var live = stats.Lifecycle.TryGetValue(key, out var l) ? l : stats.Lifecycle[key] = new VersionLifecycle();
            if (live.Status != Retired)
            {
                (live.Status, live.RetiredBy, live.RetiredAt, live.RetiredReason) = (Retired, retiredBy, at, reason);
            }
        }, cancellationToken);

    /// <summary>Resets a shadow version's clean-replay count after a hard failure or Review routing (06b Answers 2).</summary>
    public static Task ResetShadowCountAsync(IFormatRegistry registry, CarrierFormat spec, CancellationToken cancellationToken = default) =>
        registry.UpdateStatsAsync(spec.FormatId, stats =>
        {
            if (stats.Lifecycle.TryGetValue(Key(spec), out var live) && live.Status == Shadow)
            {
                live.CleanReplays = 0;
            }
        }, cancellationToken);

    public static string Status(CarrierFormat spec, FieldStats stats) =>
        stats.Lifecycle.TryGetValue(spec.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), out var live) ? live.Status : spec.Status ?? Active;

    public static async Task<string> StatusAsync(IFormatRegistry registry, CarrierFormat spec, CancellationToken cancellationToken = default) =>
        Status(spec, await registry.GetStatsAsync(spec.FormatId, cancellationToken).ConfigureAwait(false));

    /// <summary>Records the publication status of a new version.</summary>
    public static Task InitializeAsync(IFormatRegistry registry, CarrierFormat spec, string? activatedBy, DateTimeOffset at, CancellationToken cancellationToken = default) =>
        registry.UpdateStatsAsync(spec.FormatId, stats => stats.Lifecycle[Key(spec)] = new VersionLifecycle
        {
            Status = spec.Status ?? Active,
            ActivatedBy = spec.Status == Shadow ? null : activatedBy,
            ActivatedAt = spec.Status == Shadow ? null : at,
        }, cancellationToken);

    /// <summary>Underwriter approval of a shadow version ("confirm new format"); idempotent. Brief 30 item 17: a retired version is refused
    /// (an approval never resurrects it; <see cref="UnretireAsync"/> is the explicit, audited way back).</summary>
    public static Task ApproveAsync(IFormatRegistry registry, string formatId, int version, string approvedBy, DateTimeOffset at, CancellationToken cancellationToken = default) =>
        registry.UpdateStatsAsync(formatId, stats =>
        {
            var key = version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var live = stats.Lifecycle.TryGetValue(key, out var l) ? l : stats.Lifecycle[key] = new VersionLifecycle { Status = Shadow };
            if (live.Status == Retired)
            {
                throw new InvalidOperationException($"{formatId}@{version} is retired ({live.RetiredReason}); un-retire it explicitly before it can be approved.");
            }

            if (live.Status != Active)
            {
                (live.Status, live.ActivatedBy, live.ActivatedAt) = (Active, $"approval:{approvedBy}", at);
            }
        }, cancellationToken);

    /// <summary>Brief 30 item 17: an engineer puts a retired version back in matching (active). The caller audits it; a version that is not retired is left as it is.</summary>
    public static Task UnretireAsync(IFormatRegistry registry, string formatId, int version, string by, string reason, DateTimeOffset at,
        CancellationToken cancellationToken = default) =>
        registry.UpdateStatsAsync(formatId, stats =>
        {
            var key = version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (stats.Lifecycle.TryGetValue(key, out var live) && live.Status == Retired)
            {
                (live.Status, live.ActivatedBy, live.ActivatedAt) = (Active, $"unretire:{by}", at);
                (live.RetiredBy, live.RetiredAt, live.RetiredReason) = (null, null, $"un-retired by {by}: {reason}");
            }
        }, cancellationToken);

    internal static string Key(CarrierFormat spec) => spec.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class FieldStat
{
    public int SuccessCount { get; set; }
    public DateTimeOffset? LastSeen { get; set; }
    public string? LastFormatVersion { get; set; }
}

public static class FormatJson
{
    public static CarrierFormat Parse(string json) =>
        JsonSerializer.Deserialize<CarrierFormat>(json, CanonicalJson.Options) ?? throw new JsonException("Empty format spec.");

    public static string Write(CarrierFormat format) => JsonSerializer.Serialize(format, Indented);

    private static readonly JsonSerializerOptions Indented = new(CanonicalJson.Options) { WriteIndented = true };
}
