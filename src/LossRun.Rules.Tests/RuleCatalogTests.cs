using System.Text.Json.Nodes;
using LossRun.Contracts;

namespace LossRun.Rules.Tests;

public class RuleCatalogTests
{
    private static readonly RuleCatalog Catalog = RuleCatalog.Embedded;
    private static readonly JsonObject Schema = JsonNode.Parse(CanonicalData.CanonicalSchemaJson)!.AsObject();

    [Fact]
    public void Embedded_catalog_loads_and_targets_the_current_schema()
    {
        Assert.Equal("0.12.0", Catalog.CatalogVersion);
        Assert.Equal(CanonicalData.SchemaVersion, Catalog.SchemaVersion);
        Assert.Equal(33, Catalog.Rules.Count); // R-01..R-32 and R-34; R-33 is a quote rule
    }

    [Fact]
    public void Rule_ids_are_sequential_and_all_provisional_until_the_client_catalog_lands()
    {
        // Document rules and quote rules share one id sequence (R-33 is the quote rule; brief 35 added R-34 after it).
        Assert.Equal(Enumerable.Range(1, Catalog.Rules.Count + Catalog.QuoteRules.Count).Select(i => $"R-{i:00}"),
            Catalog.Rules.Select(r => r.Id).Concat(Catalog.QuoteRules.Select(q => q.Id)).Order(StringComparer.Ordinal));
        Assert.All(Catalog.Rules, r => Assert.Equal("provisional", r.Status));
        Assert.All(Catalog.Rules, r => Assert.Contains("evaluation-and-wc-domain.md", r.Source));
    }

    [Fact]
    public void Brief_03_must_cover_list_is_present()
    {
        string[] mustCover =
        [
            "Paid does not exceed incurred", "Incurred equals paid plus reserve (±$1)", "No negative amounts outside recovery columns",
            "Closed claims carry zero reserves", "Medical-only claims carry zero indemnity", "Date of injury not after valuation date",
            "Date of injury not after date reported", "Date of injury falls within the claim's policy period", "No duplicate claim numbers within the run",
            "Policy-period money subtotals reconcile to claim rows (±$1)", "Policy-period claim counts reconcile to claim rows",
            "'No losses' statement means zero claim rows for that policy period", // ADR 0016: scoped per period "Valuation date is current for the quote", "Reopened claims show an open reserve", // brief 08: prior-close part dropped (no close-date column ≠ a defect)
            "Jurisdiction is California", "Line of business is workers' compensation",
        ];

        Assert.Empty(mustCover.Except(Catalog.Rules.Select(r => r.Name)));
        Assert.Equal(RuleSeverity.Soft, Catalog.Rules.Single(r => r.Id == "R-04").Severity); // closed ⇒ zero reserves is soft
        Assert.NotNull(Catalog.Rules.Single(r => r.Id == "R-08").CaNote); // LC 5412
    }

    [Fact]
    public void Brief_03b_severities_and_params_are_applied()
    {
        RuleSeverity Severity(string id) => Catalog.Rules.Single(r => r.Id == id).Severity;

        Assert.Equal(RuleSeverity.Flag, Severity("R-17"));
        Assert.Equal(RuleSeverity.Flag, Severity("R-18"));
        Assert.Equal(RuleSeverity.Flag, Severity("R-23"));
        Assert.Equal(RuleSeverity.Flag, Severity("R-24"));
        Assert.Equal(RuleSeverity.Soft, Severity("R-25"));
        Assert.Equal(RuleSeverity.Soft, Severity("R-20")); // hard only via override when expense is separate
        Assert.Single(Catalog.Rules.Single(r => r.Id == "R-07").SeverityOverrides);
        Assert.Equal(30, Catalog.Rules.Single(r => r.Id == "R-23").Params["maxLagDays"]!.GetValue<int>());
        Assert.Equal(100000, Catalog.Rules.Single(r => r.Id == "R-24").Params["largeLossThreshold"]!.GetValue<int>());
    }

    [Fact]
    public void Every_path_in_the_catalog_exists_in_the_canonical_schema()
    {
        var raw = JsonNode.Parse(CatalogJson())!["rules"]!.AsArray();
        foreach (var rule in raw)
        {
            var id = rule!["id"]!.GetValue<string>();
            var bindings = Expand(rule["forEach"] as JsonArray);
            foreach (var path in StringsNamed(rule, "path").Concat(StringsNamed(rule, "over")))
            {
                foreach (var binding in bindings)
                {
                    var concrete = binding.Aggregate(path, (p, kv) => p.Replace("{" + kv.Key + "}", kv.Value));
                    Assert.True(Resolves(concrete), $"{id}: '{concrete}' is not a canonical schema path");
                }
            }
        }
    }

    [Theory]
    [InlineData("""{ "cmp": "approx", "left": { "const": 1 }, "right": { "const": 1 } }""", "unknown cmp operator")]
    [InlineData("""{ "frobnicate": [] }""", "unknown operator")]
    [InlineData("""{ "cmp": "eq", "left": { "path": "claim.{x}.paid" }, "right": { "const": 0 } }""", "undefined forEach var")]
    [InlineData("""{ "cmp": "le", "left": { "path": "claim.dateOfInjury" }, "right": { "param": "missing" } }""", "undefined param")]
    [InlineData("""{ "cmp": "eq", "left": { "path": "item.status" }, "right": { "const": "open" } }""", "outside sum/count/unique")]
    public void Loader_rejects_malformed_rules(string assert, string expectedError)
    {
        var json = $$"""
            { "catalogVersion": "t", "schemaVersion": "{{CanonicalData.SchemaVersion}}",
              "rules": [ { "id": "R-01", "status": "provisional", "name": "n", "severity": "hard", "scope": "claim",
                           "assert": {{assert}}, "message": "m", "source": "s" } ] }
            """;

        var error = Assert.Throws<RuleCatalogException>(() => RuleCatalog.Load(json));
        Assert.Contains(expectedError, error.Message);
    }

    [Fact]
    public void Loader_rejects_paths_outside_the_rule_scope()
    {
        var json = Catalog1("document", """{ "cmp": "eq", "left": { "path": "claim.status" }, "right": { "const": "open" } }""");

        Assert.Contains("not allowed", Assert.Throws<RuleCatalogException>(() => RuleCatalog.Load(json)).Message);
    }

    [Fact]
    public void Loader_rejects_a_schema_version_mismatch_and_duplicate_ids()
    {
        var mismatch = CatalogJson().Replace($"\"schemaVersion\": \"{CanonicalData.SchemaVersion}\"", "\"schemaVersion\": \"9.9.9\"");
        Assert.Contains("bump both together", Assert.Throws<RuleCatalogException>(() => RuleCatalog.Load(mismatch)).Message);

        var duplicate = CatalogJson().Replace("\"id\": \"R-02\"", "\"id\": \"R-01\"");
        Assert.Contains("Duplicate rule id", Assert.Throws<RuleCatalogException>(() => RuleCatalog.Load(duplicate)).Message);
    }

    [Fact]
    public void Loader_rejects_unknown_message_placeholders()
    {
        var json = Catalog1("claim", """{ "notNull": { "path": "claim.claimNumber" } }""", message: "bad {nope}");

        Assert.Contains("placeholder", Assert.Throws<RuleCatalogException>(() => RuleCatalog.Load(json)).Message);
    }

    private static string Catalog1(string scope, string assert, string message = "m") => $$"""
        { "catalogVersion": "t", "schemaVersion": "{{CanonicalData.SchemaVersion}}",
          "rules": [ { "id": "R-01", "status": "provisional", "name": "n", "severity": "hard", "scope": "{{scope}}",
                       "assert": {{assert}}, "message": "{{message}}", "source": "s" } ] }
        """;

    private static string CatalogJson()
    {
        using var stream = typeof(RuleCatalog).Assembly.GetManifestResourceStream("LossRun.Data.rule-catalog.json")!;
        return new StreamReader(stream).ReadToEnd();
    }

    private static List<Dictionary<string, string>> Expand(JsonArray? forEach)
    {
        var combos = new List<Dictionary<string, string>> { new() };
        foreach (var dim in forEach ?? [])
        {
            var name = dim!["var"]!.GetValue<string>();
            combos = combos.SelectMany(c => dim["in"]!.AsArray().Select(v => new Dictionary<string, string>(c) { [name] = v!.GetValue<string>() })).ToList();
        }

        return combos;
    }

    private static IEnumerable<string> StringsNamed(JsonNode node, string name) => node switch
    {
        JsonObject obj => obj.SelectMany(p => p.Key == name && p.Value is JsonValue v && v.TryGetValue<string>(out var s) ? [s] : StringsNamed(p.Value!, name)),
        JsonArray array => array.SelectMany(n => n is null ? [] : StringsNamed(n, name)),
        _ => [],
    };

    private static bool Resolves(string path)
    {
        var segments = path.Split('.');
        if (segments[0] == "context")
        {
            return segments.Length == 2 && typeof(RuleContext).GetProperty(char.ToUpperInvariant(segments[1][0]) + segments[1][1..]) is not null;
        }

        if (path is "document.claims" or "period.claims" or "document.policyPeriods" or "document.keptRows" or "period.keptRows")
        {
            return true;
        }

        var definition = segments[0] switch
        {
            "document" => Schema,
            "period" => Definition("PolicyPeriod"),
            "claim" => Definition("Claim"),
            _ => null,
        };

        // `item` is a claim or a policy period depending on the collection.
        return segments[0] == "item"
            ? Walk(Definition("Claim"), segments) || Walk(Definition("PolicyPeriod"), segments)
            : Walk(definition, segments);
    }

    private static bool Walk(JsonObject? definition, string[] segments)
    {
        foreach (var segment in segments.Skip(1))
        {
            var property = definition?["properties"]?[segment]?.AsObject();
            if (property is null)
            {
                return false;
            }

            var reference = property.ToJsonString();
            definition = new[] { "MoneyTriplet", "Totals", "PolicyPeriod", "Claim", "CodedValue" }
                .Where(d => reference.Contains($"\"#/$defs/{d}\""))
                .Select(Definition)
                .FirstOrDefault() ?? property;
        }

        return true;
    }

    private static JsonObject Definition(string name) => Schema["$defs"]![name]!.AsObject();
}
