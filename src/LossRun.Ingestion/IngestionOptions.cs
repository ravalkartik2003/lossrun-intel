namespace LossRun.Ingestion;

/// <summary>Configuration for preprocessing. Analyzer ids and model names are configuration, never literals in callers.</summary>
public sealed record IngestionOptions
{
    public const int DefaultCuMaxAttempts = 5;

    /// <summary>Brief 33: attempts per Content Understanding HTTP request (the client's pipeline retry; <c>ContentUnderstanding__MaxAttempts</c>).</summary>
    public int CuMaxAttempts { get; init; } = DefaultCuMaxAttempts;

    /// <summary>
    /// Brief 34 A1.2: a hash of the CU resource defaults (which model deployment answers each prebuilt alias), set by Terraform from the mapping it
    /// sends (<c>terraform_data.cu_defaults</c>; app setting <c>Cu__DefaultsHash</c>). Part of the raw-result reuse key: another model behind the
    /// classifier's completion alias is another result. Null: not part of the key (local runs).
    /// </summary>
    public string? CuDefaultsHash { get; init; }

    /// <summary>Brief 34 A1.2: never reuse a stored CU result (the eval runner measures real calls).</summary>
    public bool ForceReparse { get; init; }

    /// <summary>Content Understanding endpoint of the Foundry resource (Terraform output <c>content_understanding_endpoint</c>).</summary>
    /// <summary>Set by the host (Functions: <c>ContentUnderstanding__Endpoint</c>; CLI: <c>LOSSRUN_CU_ENDPOINT</c>); no environment is built in.</summary>
    public Uri? ContentUnderstandingEndpoint { get; init; }

    public string LayoutAnalyzerId { get; init; } = "prebuilt-layout";

    /// <summary>Custom classifier/splitter analyzer; its definition is checked in under Analyzers/{id}.json.</summary>
    public string ClassifierAnalyzerId { get; init; } = "lossrun_classifier_v2";

    /// <summary>PDFs with at least this many pages are treated as possible bundles and classified/split.</summary>
    public int ClassifyWhenPagesAtLeast { get; init; } = 2;

    /// <summary>Set false to skip classification (layout only), e.g. for a known single loss run.</summary>
    public bool ClassifyBundles { get; init; } = true;

    /// <summary>Skip the classifier when the free text-layer check says the PDF is one loss run (brief 04 Answers 3).</summary>
    public bool FirstPageCheck { get; init; } = true;

    /// <summary>Header-resolution ratio for the first-page check and for accepting a LossRun segment.</summary>
    public double AcceptThreshold { get; init; } = 0.8;

    /// <summary>Below this header-resolution ratio a "LossRun" segment is treated as not a loss run.</summary>
    public double ReviewThreshold { get; init; } = 0.5;

    /// <summary>Category whose segments become their own documents.</summary>
    public string LossRunCategory { get; init; } = "LossRun";

    /// <summary>The classifier's catch-all category; the only one the single-document fallback overrides (brief 08d).</summary>
    public string OtherCategory { get; init; } = "Other";

    /// <summary>Text density (OCR words per page) the single-document fallback requires: a loss-run table, not a cover page.</summary>
    public int FallbackMinWordsPerPage { get; init; } = 150;

    /// <summary>
    /// Brief 18 (P6): a table counts for the fallback only when at least this many of its headers resolve (laccd: an RFP
    /// whose one "loss" table was "Claim Type | Total Claims").
    /// </summary>
    public int FallbackMinResolvedHeaders { get; init; } = 3;

    /// <summary>
    /// Brief 18 (P6): share of the document's pages that must carry a qualifying table for the fallback to send those
    /// pages to learn. A manual or RFP with a sample loss table on a few pages stays kept-not-processed.
    /// </summary>
    public double FallbackMinTablePageShare { get; init; } = 0.5;

    /// <summary>Passwords to try on encrypted PDFs and workbooks (from Key Vault via configuration; empty in tests).</summary>
    public IReadOnlyList<string> Passwords { get; init; } = [];

    /// <summary>Region of the Foundry resource, recorded on every UsageEvent.</summary>
    public string Region { get; init; } = "eastus2";

    /// <summary>Deployment type of the model deployments CU calls through the default aliases.</summary>
    public string ModelDeploymentType { get; init; } = "DataZoneStandard";

    /// <summary>When set, document ids hash this scope with the content (the API uses the upload id); null keeps them content-addressed.</summary>
    public string? DocumentIdScope { get; init; }

    public int MaxArchiveDepth { get; init; } = 3;
    public long MaxArchiveBytes { get; init; } = 200L * 1024 * 1024;
}
