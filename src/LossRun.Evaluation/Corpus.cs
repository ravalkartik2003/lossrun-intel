using System.Text.Json;
using LossRun.Contracts;

namespace LossRun.Evaluation;

// The evaluation corpus (brief 08): documents with known truth. Layout on disk:
//   <corpus>/manifest.json            CorpusManifest (seed, generator version, every entry)
//   <corpus>/docs/<id>.<ext>          the document as an uploader would send it (DocsRoot; imports reference the source
//                                     folder in place, or copy it to <corpus>/files with --copy)
//   <corpus>/truth/<id>.json          TruthRecord (canonical document + what the layout prints + strata). An imported real
//                                     archive has none until adjudication: score then reports operational numbers only.

public static class InputTypes
{
    public const string NativePdf = "native-pdf";
    public const string Scan300 = "scan-300dpi-clean";
    public const string Scan200 = "scan-200dpi-rotated-noisy";
    public const string Fax150 = "scan-150dpi-fax";
    public const string Xlsx = "xlsx";
    public const string Csv = "csv";

    /// <summary>A ZIP package (imported archives; the pipeline unpacks it).</summary>
    public const string Zip = "zip";

    public static bool IsScan(string inputType) => inputType.StartsWith("scan-", StringComparison.Ordinal);

    /// <summary>The three reporting buckets of A2: native PDF / spreadsheet / scan.</summary>
    public static string Bucket(string inputType) => inputType switch
    {
        NativePdf => "native-pdf",
        Xlsx or Csv => "xlsx-csv",
        Zip => "zip",
        _ => "scan",
    };
}

public static class Strata
{
    public const string MissingYear = "missing-year";
    public const string PartialRun = "partial-run";
    public const string ClosedOnly = "closed-only";
    public const string Reopened = "reopened";
    public const string CatOutlier = "cat-outlier";
    public const string MultiPolicy = "multi-policy";
    public const string NonCa = "non-ca";
    public const string NonWcRows = "non-wc-rows";
    public const string SummaryOnlyPeriod = "summary-only-period";
    public const string Password = "password-protected";
    public const string Bundle = "bundle";

    // ADR 0018 constructs (brief 19): one stratum per construct rendering.
    public const string TwoLineRecords = "two-line-records";
    public const string ClaimPerTable = "claim-per-table";
    public const string StackedColumns = "stacked-columns";
    public const string ComponentSum = "component-sum";

    // Brief 22 Answers 1 (corpus v5): the fourth ADR 0018 construct.
    public const string MeasuresAsRows = "measures-as-rows";

    /// <summary>The printed totals disagree with the rows: excluded from the DQ first-pass rate, expected to route to Review.</summary>
    public const string SourceInconsistent = "source-inconsistent";

    public static readonly string[] EdgeCases = [MissingYear, PartialRun, ClosedOnly, Reopened, CatOutlier, MultiPolicy, NonCa, SummaryOnlyPeriod, SourceInconsistent];
}

/// <summary>What a layout prints (only printed fields are scored; derived values are checked by the rules, not scored).</summary>
/// <param name="ClaimFields">Claim paths, e.g. <c>claimNumber</c>, <c>medical.paid</c>, <c>total.incurred</c>.</param>
/// <param name="DocumentFields">Document paths: carrierName, insuredName, valuationDate, …</param>
/// <param name="PeriodFields">policyNumber, effectiveDate, expirationDate.</param>
/// <param name="SubtotalFields">Printed per-period subtotal paths (claimCount, medical.paid, …); empty when none are printed.</param>
/// <param name="GrandTotalFields">Printed report-total paths.</param>
public sealed record PrintedFields(
    IReadOnlyList<string> ClaimFields,
    IReadOnlyList<string> DocumentFields,
    IReadOnlyList<string> PeriodFields,
    IReadOnlyList<string> SubtotalFields,
    IReadOnlyList<string> GrandTotalFields);

/// <summary>A non-loss-run segment of a bundle (truth for classification and split scoring).</summary>
public sealed record TruthSegment(string Category, int StartPage, int EndPage);

public sealed class TruthRecord
{
    public string Id { get; set; } = "";
    public string File { get; set; } = "";
    public string Carrier { get; set; } = "";
    public string Layout { get; set; } = "";
    public string InputType { get; set; } = "";

    /// <summary>Truth id shared by all renderings of the same logical document.</summary>
    public string LogicalId { get; set; } = "";
    public List<string> Strata { get; set; } = [];
    public bool SourceConsistent { get; set; } = true;
    public string? Password { get; set; }
    public int Pages { get; set; }

    /// <summary>Loss-run pages of the file (all pages for single documents; the loss-run segment for bundles).</summary>
    public TruthSegment LossRunSegment { get; set; } = new("LossRun", 1, 1);
    public List<TruthSegment> OtherSegments { get; set; } = [];
    public PrintedFields Printed { get; set; } = new([], [], [], [], []);
    public LossRunDocument Document { get; set; } = new();

    /// <summary>Corpus v3: the payroll and FTE the submission carries (both sides of the insights comparison use it).</summary>
    public SubmissionTruth? Context { get; set; }
}

/// <summary>Payroll in cents and FTE per policy year (the API's payrollByPeriod / fteByPeriod, keyed by year as the engine reads them).</summary>
public sealed record SubmissionTruth(IReadOnlyDictionary<int, long> PayrollCentsByYear, IReadOnlyDictionary<int, long> FteByYear);

/// <param name="Members">A package's (ZIP) member files and their input types (imported archives; brief 14 follow-up). The package stays
/// one entry, because a broker sends it as one upload, and is stratified as a bundle.</param>
public sealed record ManifestEntry(string Id, string File, string Carrier, string Layout, string InputType, string LogicalId, IReadOnlyList<string> Strata, string Sha256,
    long Bytes, int Pages, IReadOnlyList<PackageMember>? Members = null);

/// <summary>One file inside a package.</summary>
public sealed record PackageMember(string File, string InputType, int Pages, long Bytes);

public sealed class CorpusManifest
{
    public string GeneratorVersion { get; set; } = CorpusGenerator.Version;

    /// <summary>Generator seed; null for an imported archive.</summary>
    public int? Seed { get; set; }

    /// <summary>Where the files are, relative to the corpus folder: <c>docs</c> (generated), <c>files</c> (import --copy) or the source folder (import).</summary>
    public string? DocsRoot { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
    public List<ManifestEntry> Entries { get; set; } = [];

    public static CorpusManifest Load(string corpusDir) =>
        JsonSerializer.Deserialize<CorpusManifest>(File.ReadAllText(Path.Combine(corpusDir, "manifest.json")), CanonicalJson.Options)!;

    public static TruthRecord Truth(string corpusDir, string id) =>
        JsonSerializer.Deserialize<TruthRecord>(File.ReadAllText(Path.Combine(corpusDir, "truth", id + ".json")), CanonicalJson.Options)!;

    /// <summary>The truth record, or null when the corpus has none for this entry yet (an imported archive before adjudication).</summary>
    public static TruthRecord? TryTruth(string corpusDir, string id) =>
        File.Exists(Path.Combine(corpusDir, "truth", id + ".json")) ? Truth(corpusDir, id) : null;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Roots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Full path of an entry's file (honours <see cref="DocsRoot"/>).</summary>
    public static string DocumentPath(string corpusDir, string file)
    {
        var root = Roots.GetOrAdd(Path.GetFullPath(corpusDir), dir => Load(dir).DocsRoot ?? "docs");
        return Path.GetFullPath(Path.Combine(corpusDir, root, file));
    }
}
