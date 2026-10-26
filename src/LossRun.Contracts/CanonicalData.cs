using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace LossRun.Contracts;

/// <summary>
/// Access to the reference data embedded from docs/evaluation (canonical schema, glossary, code sets).
/// The files are the source of truth; this class only reads them.
/// </summary>
public static class CanonicalData
{
    private static readonly Lazy<string> SchemaText = new(() => ReadResource("canonical-schema.json"));
    private static readonly Lazy<string> GlossaryText = new(() => ReadResource("glossary.json"));
    private static readonly Lazy<string> CodeSetsText = new(() => ReadResource("code-sets.json"));

    private static readonly Lazy<string> SchemaVersionValue = new(() =>
        JsonNode.Parse(SchemaText.Value)?["schemaVersion"]?.GetValue<string>()
        ?? throw new InvalidDataException("canonical-schema.json has no top-level schemaVersion."));

    private static readonly Lazy<Glossary> GlossaryValue = new(() => Ablate(LoadGlossary(GlossaryText.Value)));
    private static readonly List<string> Ablated = [];
    private static readonly Lazy<CodeSets> CodeSetsValue = new(() => LoadCodeSets(CodeSetsText.Value));

    internal static JsonSerializerOptions ReferenceDataOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>Version of the canonical schema (top-level <c>schemaVersion</c> keyword of canonical-schema.json).</summary>
    public static string SchemaVersion => SchemaVersionValue.Value;

    public static string CanonicalSchemaJson => SchemaText.Value;
    public static string GlossaryJson => GlossaryText.Value;
    public static string CodeSetsJson => CodeSetsText.Value;

    public static Glossary Glossary => GlossaryValue.Value;

    private static readonly Lazy<LineOfBusinessVocabulary> LineOfBusinessValue = new(() =>
        JsonSerializer.Deserialize<LineOfBusinessVocabulary>(ReadResource("line-of-business.json"), ReferenceDataOptions)
        ?? throw new InvalidDataException("line-of-business.json deserialized to null."));

    /// <summary>ADR 0020: the printed terms of the lines of business other than workers' comp (docs/evaluation/line-of-business.json).</summary>
    public static LineOfBusinessVocabulary LineOfBusiness => LineOfBusinessValue.Value;

    /// <summary>
    /// The hand aliases removed from the seed for an ablation experiment (brief 21: are they re-learned through the loop?):
    /// <c>LOSSRUN_GLOSSARY_ABLATE</c> names a file of "field|header" lines. Evaluation only; empty in every deployment.
    /// The run's run.json records the list.
    /// </summary>
    public static IReadOnlyList<string> GlossaryAblation
    {
        get
        {
            _ = GlossaryValue.Value;
            return Ablated;
        }
    }

    private static Glossary Ablate(Glossary glossary)
    {
        if (Environment.GetEnvironmentVariable("LOSSRUN_GLOSSARY_ABLATE") is not { Length: > 0 } path)
        {
            return glossary;
        }

        foreach (var line in File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
        {
            var (field, header) = (line[..line.IndexOf('|')], line[(line.IndexOf('|') + 1)..]);
            if (glossary.Fields.TryGetValue(field, out var entry)
                && entry.Aliases.RemoveAll(a => CanonicalComparer.NormalizeText(a.Header) == CanonicalComparer.NormalizeText(header)) > 0)
            {
                Ablated.Add(line);
            }
            else
            {
                throw new InvalidDataException($"LOSSRUN_GLOSSARY_ABLATE: no alias \"{header}\" on {field}.");
            }
        }

        return glossary;
    }
    public static CodeSets CodeSets => CodeSetsValue.Value;

    public static Glossary LoadGlossary(string json) =>
        JsonSerializer.Deserialize<Glossary>(json, ReferenceDataOptions)
        ?? throw new InvalidDataException("glossary.json deserialized to null.");

    public static CodeSets LoadCodeSets(string json) =>
        JsonSerializer.Deserialize<CodeSets>(json, ReferenceDataOptions)
        ?? throw new InvalidDataException("code-sets.json deserialized to null.");

    private static string ReadResource(string fileName)
    {
        var name = "LossRun.Data." + fileName;
        using var stream = typeof(CanonicalData).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException($"Embedded resource {name} not found in {Assembly.GetExecutingAssembly().GetName().Name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
