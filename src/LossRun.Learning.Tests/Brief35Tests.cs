using System.Text.Json;
using LossRun.Contracts;
using LossRun.Formats;
using LossRun.Formats.Replay;

namespace LossRun.Learning.Tests;

/// <summary>
/// Brief 35 Part A: the c8 carrier-name learn gap (brief 34 wake). One learn mapped <c>document.carrierName</c> and <c>document.insuredName</c>
/// to the header label "Insured", the learn gate accepted it, and every later c8 document replayed the insured as the carrier.
/// </summary>
public class Brief35Tests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "constructs", name);

    private static DocumentGraph C8Graph() =>
        JsonSerializer.Deserialize<DocumentGraph>(File.ReadAllText(Fixture("c8-stacked-columns.graph.json")), CanonicalJson.Options)!;

    private static CarrierFormat C8Spec() => FormatJson.Parse(File.ReadAllText(Fixture("c8-stacked-columns.spec.json")));

    /// <summary>The brief 34 wake's c8-000 spec, on the construct fixture: both fields read the header label "Insured".</summary>
    private static CarrierFormat C8InsuredAsCarrier()
    {
        var spec = C8Spec();
        foreach (var target in new[] { "document.carrierName", "document.insuredName" })
        {
            spec.DocumentFields.Single(f => f.Target == target).Locator = new FieldLocator { Type = "headerLabel", Label = "Insured" };
        }

        return spec;
    }

    [Fact]
    public void The_learn_gate_refuses_the_c8_000_mapping_and_says_which_pair()
    {
        var graph = C8Graph();
        var spec = C8InsuredAsCarrier();
        var replay = ReplayEngine.Execute(graph, spec);
        Assert.Equal(replay.Document.InsuredName, replay.Document.CarrierName); // what the wake delivered: the insured, as the carrier

        var issues = SpecLint.Check(spec, replay, graph);
        // §1: one label, one field.
        Assert.Contains(issues, i => i.StartsWith("lint: documentFields document.carrierName and document.insuredName all read the label \"Insured\"", StringComparison.Ordinal));
        // §2: the glossary maps "Insured" to document.insuredName, not to the carrier.
        Assert.Contains(issues, i => i.StartsWith("lint: documentFields document.carrierName reads the label \"Insured\", which the glossary maps to document.insuredName", StringComparison.Ordinal)
            && i.Contains("\"Carrier\"", StringComparison.Ordinal));
        // §3 at learn time: the page's carrier is not the spec's own.
        Assert.Contains(issues, i => i.StartsWith("lint: document.carrierName reads", StringComparison.Ordinal) && i.Contains("Illustrative Comp Assurance", StringComparison.Ordinal));

        // The learn gate publishes nothing with an issue (LearnGate: Issues.Count > 0 fails), and the repair prompt carries the text.
        var verification = SpecVerifier.Verify(graph, spec);
        Assert.Null(verification.Gate()); // no verification gate passes with an issue
        Assert.Contains("one label is one field", verification.Feedback(), StringComparison.Ordinal);

        // The construct's own spec reads both from their own places: none of the three.
        var clean = C8Spec();
        Assert.DoesNotContain(SpecLint.Check(clean, ReplayEngine.Execute(graph, clean), graph),
            i => i.Contains("one label is one field", StringComparison.Ordinal) || i.Contains("which the glossary maps", StringComparison.Ordinal) || i.StartsWith("lint: document.carrierName reads", StringComparison.Ordinal));
    }

    [Fact]
    public void A_policy_period_label_may_carry_both_dates_and_an_unknown_label_is_not_judged()
    {
        var graph = C8Graph();
        var spec = C8Spec();
        spec.DocumentFields.Add(new DocumentFieldSpec { Target = "period.effectiveDate", Locator = new FieldLocator { Type = "headerLabel", Label = "Policy Period" } });
        spec.DocumentFields.Add(new DocumentFieldSpec { Target = "period.expirationDate", Locator = new FieldLocator { Type = "headerLabel", Label = "Policy Period" } });
        spec.DocumentFields.Single(f => f.Target == "document.insuredName").Locator = new FieldLocator { Type = "headerLabel", Label = "Account" };

        var issues = SpecLint.Check(spec, ReplayEngine.Execute(graph, spec), graph);
        Assert.DoesNotContain(issues, i => i.Contains("one label is one field", StringComparison.Ordinal) || i.Contains("which the glossary maps", StringComparison.Ordinal));
    }

    [Fact]
    public void A_replay_that_reads_another_partys_name_as_the_carrier_goes_to_review_without_a_hint()
    {
        // §3 at replay: a format published before the lint (or a page that moved) still cannot deliver the insured as the carrier.
        var graph = C8Graph();
        var spec = C8InsuredAsCarrier();
        var replay = ReplayEngine.Execute(graph, spec);
        var match = new FingerprintMatch(spec, "deterministic", 1.0, false, FingerprintText.Compute(graph), 0, new Dictionary<string, double>(), []);

        var decision = new FormatRouter().Decide(match, replay, null);
        Assert.Equal(RoutingTier.Review, decision.Tier);
        Assert.Contains(decision.Reasons, r => r.StartsWith(FormatRouter.CarrierOnPageReasonPrefix, StringComparison.Ordinal) && r.Contains("Illustrative Comp Assurance", StringComparison.Ordinal));

        // The construct's own spec reads the carrier the format names: delivered as before.
        var clean = C8Spec();
        Assert.Null(FormatRouter.CarrierOnPageDiffers(clean, ReplayEngine.Execute(graph, clean)));
        // A hand spec without a carrier name is exempt.
        var anonymous = C8InsuredAsCarrier();
        anonymous.CarrierName = "";
        Assert.Null(FormatRouter.CarrierOnPageDiffers(anonymous, replay));
    }

    [Fact]
    public void A_learn_model_named_by_the_environment_takes_its_listed_version()
    {
        // §7: a learn-model study sets only LOSSRUN_LEARN_MODEL; the version recorded on specs and usage must be that model's.
        var defaults = LearnOptions.FromConfiguration(_ => null);
        var other = "gpt-5.2";
        var study = LearnOptions.FromConfiguration(name => name == "LOSSRUN_LEARN_MODEL" ? other : null);
        Assert.Equal(other, study.LearnDeployment);
        Assert.Equal("2025-12-11", study.LearnModelVersion);
        Assert.Equal(defaults.DeltaModelVersion, study.DeltaModelVersion);

        // An explicit version still wins; the default model keeps its configured version.
        Assert.Equal("pinned", LearnOptions.FromConfiguration(name => name switch { "LOSSRUN_LEARN_MODEL" => other, "LOSSRUN_LEARN_MODEL_VERSION" => "pinned", _ => null }).LearnModelVersion);
        Assert.Equal(defaults.LearnModelVersion, LearnOptions.FromConfiguration(name => name == "LOSSRUN_LEARN_MODEL" ? defaults.LearnDeployment : null).LearnModelVersion);
    }

    [Theory]
    [InlineData("Synthetic Mutual Insurance Co.", "synthetic")]
    [InlineData("The Synthetic Group, Inc.", "synthetic")]
    [InlineData("Synthetic Indemnity Company", "synthetic")]
    [InlineData("Synthetic Property Casualty Co of America", "synthetic property")]
    [InlineData("Illustrative Comp Assurance", "illustrative comp")]
    [InlineData("Mutual Insurance Company", "mutual insurance company")] // only corporate words: kept whole
    [InlineData("Kappa Foods ( synthetic )", "kappa foods synthetic")]
    public void Answers2_a_carrier_name_compares_without_its_corporate_words(string printed, string core) =>
        Assert.Equal(core, CarrierNames.Normalize(printed));

    [Fact]
    public void Answers2_a_spec_knows_its_learned_name_and_the_names_a_reviewer_added()
    {
        var spec = C8Spec();
        Assert.True(FormatFingerprinter.KnowsCarrier(spec, "Illustrative Comp Assurance Company"));
        Assert.False(FormatFingerprinter.KnowsCarrier(spec, "Illustrative Comp Assurance of the West Indemnity"));
        spec.CarrierNames.Add("Illustrative Comp Assurance of the West Indemnity");
        Assert.True(FormatFingerprinter.KnowsCarrier(spec, "Illustrative Comp Assurance of the West Indemnity Co"));
        Assert.False(FormatFingerprinter.KnowsCarrier(spec, "Kappa Foods (synthetic)"));
    }
}
