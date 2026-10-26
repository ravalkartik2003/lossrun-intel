using System.Text.Json.Nodes;
using Json.Schema;
using LossRun.Contracts;
using LossRun.Testing;

namespace LossRun.Contracts.Tests;

public class SchemaTests
{
    private static readonly JsonSchema Schema = JsonSchema.FromText(CanonicalData.CanonicalSchemaJson);
    private static readonly JsonObject SchemaNode = JsonNode.Parse(CanonicalData.CanonicalSchemaJson)!.AsObject();

    private static readonly Dictionary<string, Type> DefinitionTypes = new()
    {
        ["#"] = typeof(LossRunDocument),
        ["PolicyPeriod"] = typeof(PolicyPeriod),
        ["Claim"] = typeof(Claim),
        ["MoneyTriplet"] = typeof(MoneyTriplet),
        ["CodedValue"] = typeof(CodedValue),
        ["Totals"] = typeof(Totals),
        ["RecoveryAmounts"] = typeof(RecoveryAmounts),
        ["ComponentPresence"] = typeof(ComponentPresence),
        ["Presence"] = typeof(Presence),
        ["LineageRecord"] = typeof(LineageRecord),
    };

    private static EvaluationResults Validate(JsonNode instance) =>
        Schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });

    private static string Errors(EvaluationResults results) =>
        string.Join("\n", results.Details.Where(d => d.HasErrors).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Key} {e.Value}")));

    [Fact]
    public void Synthetic_three_claim_document_conforms_to_the_schema()
    {
        var document = SyntheticLossRuns.ThreeClaims();
        Assert.Equal(3, document.PolicyPeriods.Sum(p => p.Claims.Count));

        var results = Validate(JsonNode.Parse(CanonicalJson.Serialize(document))!);

        Assert.True(results.IsValid, Errors(results));
    }

    [Fact]
    public void Serialization_round_trips()
    {
        var json = CanonicalJson.Serialize(SyntheticLossRuns.ThreeClaims());

        Assert.Equal(json, CanonicalJson.Serialize(CanonicalJson.Deserialize(json)));
        Assert.Contains("\"claimType\":\"medicalOnly\"", json);
        Assert.Contains("\"valuationDate\":\"2024-09-30\"", json);
        Assert.Contains("\"expenseTreatment\":\"separate\"", json);
        Assert.Contains("\"detailLevel\":\"claims\"", json);
    }

    [Theory]
    [InlineData("policyPeriods/0/claims/0/status", "\"pending\"")]
    [InlineData("policyPeriods/0/claims/0/claimType", "\"lostTime\"")]
    [InlineData("valuationDate", "\"09/30/2024\"")]
    [InlineData("policyPeriods/0/claims/0/indemnity/paid", "\"12,000.00\"")]
    [InlineData("policyPeriods/0/claims/0/unexpectedField", "1")]
    [InlineData("noLossesStatement", "null")]
    [InlineData("expenseTreatment", "\"merged\"")]
    [InlineData("policyPeriods/0/detailLevel", "\"summary\"")]
    public void Schema_rejects_invalid_values(string pointer, string valueJson)
    {
        var node = JsonNode.Parse(CanonicalJson.Serialize(SyntheticLossRuns.ThreeClaims()))!;
        var segments = pointer.Split('/');
        var parent = segments[..^1].Aggregate(node, (current, s) => int.TryParse(s, out var i) ? current[i]! : current[s]!);
        parent.AsObject()[segments[^1]] = JsonNode.Parse(valueJson);

        Assert.False(Validate(node).IsValid);
    }

    [Fact]
    public void Schema_requires_the_document_envelope()
    {
        Assert.False(Validate(JsonNode.Parse("""{"documentId":"x"}""")!).IsValid);
    }

    [Fact]
    public void Versions_agree_across_data_files()
    {
        Assert.Equal("0.10.0", CanonicalData.SchemaVersion);
        Assert.Equal(CanonicalData.SchemaVersion, new LossRunDocument().SchemaVersion);
        Assert.Equal(CanonicalData.SchemaVersion, CanonicalData.Glossary.SchemaVersion);
    }

    [Fact]
    public void Csharp_model_matches_schema_properties()
    {
        foreach (var (definition, type) in DefinitionTypes)
        {
            var schemaProperties = Properties(definition).Select(p => p.Key).Order().ToList();
            var modelProperties = CanonicalJson.Options.GetTypeInfo(type).Properties.Select(p => p.Name).Order().ToList();

            Assert.True(schemaProperties.SequenceEqual(modelProperties),
                $"{definition} drift. Schema only: [{string.Join(", ", schemaProperties.Except(modelProperties))}]; " +
                $"model only: [{string.Join(", ", modelProperties.Except(schemaProperties))}]");
        }
    }

    [Fact]
    public void Every_field_carries_the_required_annotations()
    {
        string[] piiLevels = ["none", "low", "medium", "high"];
        foreach (var definition in DefinitionTypes.Keys)
        {
            foreach (var (name, node) in Properties(definition))
            {
                var field = node!.AsObject();
                var where = $"{definition}.{name}";
                Assert.False(string.IsNullOrWhiteSpace(field["description"]?.GetValue<string>()), $"{where}: description");
                Assert.True(field["nullable"] is JsonValue n && n.TryGetValue<bool>(out _), $"{where}: nullable");
                Assert.Contains(field["piiLevel"]?.GetValue<string>(), piiLevels);
                Assert.False(string.IsNullOrWhiteSpace(field["unit"]?.GetValue<string>()), $"{where}: unit");
                Assert.True(field.ContainsKey("aliasesRef"), $"{where}: aliasesRef");
                Assert.Equal(field["nullable"]!.GetValue<bool>(), AllowsNull(field));
            }
        }
    }

    [Fact]
    public void Every_glossary_reference_resolves_and_every_glossary_field_is_referenced()
    {
        var fields = CanonicalData.Glossary.Fields;
        var referenced = new HashSet<string>();
        foreach (var definition in DefinitionTypes.Keys)
        {
            foreach (var (name, node) in Properties(definition))
            {
                var aliasesRef = node!["aliasesRef"]?.GetValue<string>();
                if (aliasesRef is null || aliasesRef.StartsWith("{prefix}", StringComparison.Ordinal))
                {
                    continue;
                }

                const string prefix = "glossary.json#/fields/";
                Assert.StartsWith(prefix, aliasesRef);
                var key = aliasesRef[prefix.Length..];
                string[] leaves = RefersTo(node!.AsObject(), "MoneyTriplet") ? ["paid", "reserve", "incurred"]
                    : RefersTo(node!.AsObject(), "Totals") ? ["claimCount", "openCount", "closedCount"]
                    : [];
                var expanded = leaves.Length == 0 ? [key] : leaves.Select(l => $"{key}.{l}").ToArray();
                foreach (var k in expanded)
                {
                    Assert.True(fields.ContainsKey(k), $"{definition}.{name}: glossary has no field '{k}'");
                    referenced.Add(k);
                }
            }
        }

        Assert.Empty(fields.Keys.Except(referenced));
    }

    [Fact]
    public void Claimant_identity_and_free_text_are_flagged_as_high_pii()
    {
        var claim = Properties("Claim").ToDictionary(p => p.Key, p => p.Value!["piiLevel"]!.GetValue<string>());

        Assert.Equal("high", claim["claimantName"]);
        Assert.Equal("high", claim["accidentDescription"]);
    }

    private static IEnumerable<KeyValuePair<string, JsonNode?>> Properties(string definition)
    {
        var owner = definition == "#" ? SchemaNode : SchemaNode["$defs"]![definition]!.AsObject();
        return owner["properties"]!.AsObject();
    }

    private static bool RefersTo(JsonObject field, string definition) =>
        field.ToJsonString().Contains($"\"#/$defs/{definition}\"", StringComparison.Ordinal);

    private static bool AllowsNull(JsonObject field) =>
        field["type"] switch
        {
            JsonArray types => types.Any(t => t!.GetValue<string>() == "null"),
            JsonValue type => type.GetValue<string>() == "null",
            _ => field["anyOf"] is JsonArray any && any.Any(a => a!["type"]?.GetValue<string>() == "null"),
        };
}
