namespace LossRun.Learning;

/// <summary>
/// Learn-path configuration. Model names are configuration, never literals in code (CLAUDE.md): the deployments come
/// from the Terraform outputs <c>learn_model</c> / <c>delta_model</c> (ADR 0004: both gpt-5.4-mini today) and the
/// version from the deployment definition, because the Responses API returns only the deployment name.
/// </summary>
public sealed record LearnOptions
{
    public required string LearnDeployment { get; init; }
    public string? LearnModelVersion { get; init; }
    public required string DeltaDeployment { get; init; }
    public string? DeltaModelVersion { get; init; }

    /// <summary>When a learned glossary alias becomes active without a reviewer (ADR 0019; brief 29: config/alias-policy.json, 5 documents across 3 learned formats).</summary>
    public LossRun.Formats.AliasPolicy AliasPolicy { get; init; } = LossRun.Formats.AliasPolicy.Default;

    /// <summary>Versioned with the prompt text; recorded on every learned spec and usage event.</summary>
    public string PromptVersion { get; init; } = PromptBuilder.Version;

    /// <summary>Propose → replay → verify rounds before the best attempt goes to Review (brief 06: 3); PDFs, and spreadsheets unless overridden.</summary>
    public int MaxIterations { get; init; } = 3;

    /// <summary>Iteration budget for spreadsheets (config <c>maxIterations.spreadsheet</c>; set from the 08b learn-reliability study); null = <see cref="MaxIterations"/>.</summary>
    public int? MaxIterationsSpreadsheet { get; init; }

    public int IterationsFor(LossRun.Contracts.DocumentGraph graph) =>
        graph.SourceFile.Kind is LossRun.Contracts.FileKind.Pdf ? MaxIterations : MaxIterationsSpreadsheet ?? MaxIterations;

    /// <summary>Send a rendered page-1 image with the learn request (PDFs only). Measured in the brief-06 report.</summary>
    public bool IncludePageImage { get; init; }

    /// <summary>Overrides learn-gate.json's minimum coverage for spreadsheets (null = the data file's value).</summary>
    public double? SpreadsheetMinRequiredCoverage { get; init; }

    public string DeploymentType { get; init; } = "DataZoneStandard";
    public string Region { get; init; } = "eastus2";

    /// <summary>Embedding deployment the centroid of a learned PDF format is built with (null: no semantic tier).</summary>
    public string? EmbeddingModel { get; init; }

    /// <summary>config/models.json (embedded), overridden by LOSSRUN_* environment variables.</summary>
    public static LearnOptions FromConfiguration() => FromConfiguration(Environment.GetEnvironmentVariable);

    /// <summary>config/models.json (embedded), overridden by the variables <paramref name="environment"/> returns.</summary>
    public static LearnOptions FromConfiguration(Func<string, string?> environment)
    {
        using var stream = typeof(LearnOptions).Assembly.GetManifestResourceStream("LossRun.Learning.models.json")
            ?? throw new InvalidOperationException("Embedded config/models.json is missing.");
        var config = System.Text.Json.JsonDocument.Parse(stream).RootElement;
        string Value(string env, string key) => environment(env) is { Length: > 0 } v ? v : config.GetProperty(key).GetString()!;

        // Brief 35 §7: a model named by the environment without its version takes the version config/models.json lists for it.
        string Version(string modelEnv, string versionEnv, string modelKey, string versionKey) =>
            environment(versionEnv) is { Length: > 0 } v ? v
            : environment(modelEnv) is { Length: > 0 } model && model != config.GetProperty(modelKey).GetString()
                && config.TryGetProperty("modelVersions", out var versions) && versions.TryGetProperty(model, out var listed) ? listed.GetString()!
            : config.GetProperty(versionKey).GetString()!;
        return new LearnOptions
        {
            LearnDeployment = Value("LOSSRUN_LEARN_MODEL", "learnModel"),
            LearnModelVersion = Version("LOSSRUN_LEARN_MODEL", "LOSSRUN_LEARN_MODEL_VERSION", "learnModel", "learnModelVersion"),
            DeltaDeployment = Value("LOSSRUN_DELTA_MODEL", "deltaModel"),
            DeltaModelVersion = Version("LOSSRUN_DELTA_MODEL", "LOSSRUN_DELTA_MODEL_VERSION", "deltaModel", "deltaModelVersion"),
            EmbeddingModel = Value("LOSSRUN_EMBEDDING_MODEL", "embeddingModel"),
            IncludePageImage = environment("LOSSRUN_LEARN_PAGE_IMAGE") is "1" or "true",
            // LOSSRUN_LEARN_MAX_ITERATIONS overrides the PDF budget for one run (brief 17: a re-run inside a spend cap).
            MaxIterations = environment("LOSSRUN_LEARN_MAX_ITERATIONS") is { Length: > 0 } cap ? int.Parse(cap, System.Globalization.CultureInfo.InvariantCulture)
                : config.TryGetProperty("maxIterations", out var budget) && budget.TryGetProperty("pdf", out var pdf) ? pdf.GetInt32() : 3,
            MaxIterationsSpreadsheet = config.TryGetProperty("maxIterations", out var budget2) && budget2.TryGetProperty("spreadsheet", out var sheet) ? sheet.GetInt32() : null,
        };
    }
}
