using System.Text.Json;
using LossRun.Contracts;

namespace LossRun.Formats.Tests;

/// <summary>ADR 0019: learned glossary aliases are candidates until verified or confirmed, active aliases resolve headers, corrections retire them.</summary>
public class GlossaryAliasTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static LearnedMapping Learned(string formatId, string documentId, string header = "Comp Pd", string field = "claim.indemnity.paid") => new()
    {
        Header = header, CanonicalField = field, CarrierName = "Synthetic", FormatId = formatId, FormatVersion = "1",
        LearnedBy = new LearnedBy { Model = "learn-deployment", PromptVersion = "learn-v8" }, Confidence = 0.9, FirstSeenDocumentId = documentId, FirstSeenUtc = T0,
        TotalsTie = true, VerificationGate = "pdf-totals",
        Evidence = [new AliasEvidence { DocumentId = documentId, FormatRef = $"{formatId}@1", Route = "learn", TotalsTie = true, AtUtc = T0 }],
    };

    private static string SeedGlossary()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docs", "evaluation", "glossary.json")))
            {
                return Path.Combine(dir.FullName, "docs", "evaluation", "glossary.json");
            }
        }

        throw new FileNotFoundException("docs/evaluation/glossary.json");
    }

    private static CarrierFormat Spec(string formatId, string header = "Comp Pd") => new()
    {
        FormatId = formatId, Version = 1, ColumnMap = [new ColumnMapping { Source = header, Target = "claim.indemnity.paid", Normalizer = new() { Type = "currency" } }],
    };

    [Fact]
    public void Promotion_proposes_long_lived_well_evidenced_active_aliases_not_in_the_seed()
    {
        // Brief 29 §2: active ≥ 4 weeks, never retired, ≥ 10 documents, not already a hand alias; glossary.json is only read.
        LearnedMapping Active(string header, int docs, int weeksAgo, bool retiredBefore = false)
        {
            var m = Learned("fmt-a", "d0", header);
            m.Evidence = [.. Enumerable.Range(0, docs).Select(i => new AliasEvidence { DocumentId = $"d{i}", FormatRef = $"fmt-{i % 3}@1", Route = "replay", AtUtc = T0 })];
            (m.Status, m.ActivatedBy, m.ActivatedUtc, m.ActivationReason) = (AliasStatus.Active, "system", T0.AddDays(-7 * weeksAgo), "verified");
            m.RetiredReason = retiredBefore ? "corrected once" : null;
            return m;
        }

        var learned = new LearnedGlossary { Mappings = [Active("Comp Pd", 12, 6), Active("Ind Pd X", 12, 2), Active("Ind Pd Y", 4, 6), Active("Ind Pd Z", 12, 6, retiredBefore: true), Active("Indemnity Paid", 20, 9)] };
        var seed = File.ReadAllText(SeedGlossary());
        var (promote, skipped) = GlossaryPromotion.Select(learned, seed, T0, 4, 10, new AliasPolicy(5, 3));

        Assert.Equal(["Comp Pd"], promote.Select(c => c.Header));
        Assert.StartsWith("learned:adr-0019:fmt-a@1", promote[0].Source, StringComparison.Ordinal);
        Assert.Equal(["active 2.0 weeks (< 4)", "4 verified documents (< 10)", "has been retired", "already in glossary.json"],
            skipped.OrderBy(s => s.Header).Select(s => s.Reason));
        Assert.Contains("Comp Pd", GlossaryPromotion.PullRequest(promote, "test", 4, 10), StringComparison.Ordinal);
        Assert.Contains("\"additions\"", GlossaryPromotion.Json(promote, skipped, "test", T0, 4, 10), StringComparison.Ordinal);
    }

    [Fact]
    public void The_configured_policy_is_five_documents_across_three_learned_formats()
    {
        // Brief 29 §1 (brief 21 Answer 4): config/alias-policy.json, unless the run overrides it in the environment.
        if (Environment.GetEnvironmentVariable("LOSSRUN_ALIAS_MIN_DOCUMENTS") is null && Environment.GetEnvironmentVariable("LOSSRUN_ALIAS_MIN_FORMATS") is null)
        {
            Assert.Equal(new AliasPolicy(5, 3, false), AliasPolicy.Default);
        }
    }

    [Fact]
    public async Task Mixed_evidence_counts_only_learned_formats_once_the_registry_names_the_hand_specs()
    {
        // Brief 29 Answers 4: evidence rows are append-only, so rows recorded before the hand-replay marker (route "replay" on a
        // hand spec) are corrected in the reading: the registry says which formats are hand-written.
        var registry = new InMemoryFormatRegistry();
        await FormatDirectory.LoadIntoAsync(Graphs.File("formats"), registry); // hand specs: synthetic-mutual-pdf, -csv, -csv-dated, -xlsx
        AliasEvidence E(string doc, string format, string route) => new() { DocumentId = doc, FormatRef = $"{format}@1", Route = route, TotalsTie = true, AtUtc = T0 };
        var alias = Learned("fmt-a", "d1");
        alias.Evidence.AddRange([
            E("d2", "fmt-b", "replay"),                         // learned
            E("d3", "synthetic-mutual-pdf", "replay"),          // hand spec, recorded before the marker existed
            E("d4", "synthetic-mutual-xlsx", "replay"),         // hand spec, recorded before the marker existed
            E("d5", "synthetic-mutual-csv", AliasEvidenceRoutes.HandReplay), // hand spec, marked
            E("d6", "fmt-b", "replay"),
        ]);
        var glossary = new LearnedGlossary { Mappings = [alias] };

        var byRouteOnly = new AliasPolicy(5, 3);
        Assert.Equal((6, 5, 4), (alias.DistinctDocuments, alias.DistinctFormats, byRouteOnly.FormatsOf(alias))); // the old rows still count
        var withRegistry = await byRouteOnly.WithRegistryAsync(registry);
        Assert.Contains("synthetic-mutual-pdf", withRegistry.HandFormatIds);
        Assert.DoesNotContain("fmt-a", withRegistry.HandFormatIds);
        Assert.Equal(2, withRegistry.FormatsOf(alias)); // fmt-a and fmt-b only

        // Six documents, two learned formats: no activation under 5/3 with the registry; the old reading would have activated it.
        AliasLifecycle.RecordVerified(glossary, Spec("fmt-b"), "d7", "replay", true, T0.AddHours(1), withRegistry);
        Assert.Equal(AliasStatus.Candidate, alias.Status);
        var oldReading = new LearnedGlossary { Mappings = [Learned("fmt-a", "d1")] };
        oldReading.Mappings[0].Evidence.AddRange(alias.Evidence.Skip(1));
        AliasLifecycle.RecordVerified(oldReading, Spec("fmt-b"), "d7", "replay", true, T0.AddHours(1), byRouteOnly);
        Assert.Equal(AliasStatus.Active, oldReading.Mappings[0].Status);

        // A third learned format activates it, and the reason counts learned formats only.
        AliasLifecycle.RecordVerified(glossary, Spec("fmt-c"), "d8", "replay", true, T0.AddHours(2), withRegistry);
        Assert.Equal((AliasStatus.Active, "verified on 8 documents across 3 learned formats"), (alias.Status, alias.ActivationReason));
    }

    [Fact]
    public void A_hand_spec_replay_counts_toward_documents_but_never_toward_formats()
    {
        // Brief 21 Answer 2: a hand-written spec is not independent evidence that a learned meaning is global.
        var glossary = new LearnedGlossary();
        var policy = new AliasPolicy(5, 3);
        AliasLifecycle.RecordLearned(glossary, [Learned("fmt-a", "d1")], policy);
        var hand = Spec("hand-x");
        hand.CreatedBy = "hand";
        foreach (var d in new[] { "d2", "d3", "d4" })
        {
            AliasLifecycle.RecordVerified(glossary, hand, d, "replay", true, T0.AddHours(1), policy);
        }

        AliasLifecycle.RecordVerified(glossary, Spec("fmt-b"), "d5", "replay", true, T0.AddHours(2), policy);
        var alias = Assert.Single(glossary.Mappings);
        Assert.Equal(AliasEvidenceRoutes.HandReplay, alias.Evidence.Single(e => e.DocumentId == "d2").Route);
        Assert.Equal((5, 3, 2, AliasStatus.Candidate), (alias.DistinctDocuments, alias.DistinctFormats, policy.FormatsOf(alias), alias.Status)); // 2 learned formats < 3

        var withHand = new AliasPolicy(5, 3, CountHandFormats: true);
        Assert.Equal(3, withHand.FormatsOf(alias));
        AliasLifecycle.RecordVerified(glossary, Spec("fmt-c"), "d6", "replay", true, T0.AddHours(3), policy);
        Assert.Equal((AliasStatus.Active, "verified on 6 documents across 3 learned formats"), (alias.Status, alias.ActivationReason));
    }

    [Fact]
    public void A_learned_mapping_is_a_candidate_and_does_not_resolve_headers()
    {
        var glossary = new LearnedGlossary();

        var changes = AliasLifecycle.RecordLearned(glossary, [Learned("fmt-a", "d1")], new AliasPolicy(3, 2));

        Assert.Equal("glossary.alias.candidate", Assert.Single(changes).Kind);
        Assert.Equal(AliasStatus.Candidate, Assert.Single(glossary.Mappings).Status);
        Assert.Null(CanonicalData.Glossary.WithLearned(glossary.Mappings).ResolveHeader("Comp Pd"));
    }

    [Fact]
    public void Three_documents_across_two_formats_activate_a_candidate_and_it_then_resolves_like_a_hand_alias()
    {
        var glossary = new LearnedGlossary();
        AliasLifecycle.RecordLearned(glossary, [Learned("fmt-a", "d1")], new AliasPolicy(3, 2));
        AliasLifecycle.RecordVerified(glossary, Spec("fmt-a"), "d2", "replay", true, T0.AddHours(1), new AliasPolicy(3, 2));
        AliasLifecycle.RecordVerified(glossary, Spec("fmt-a"), "d2", "replay", true, T0.AddHours(1), new AliasPolicy(3, 2)); // the same document twice counts once
        Assert.Equal(AliasStatus.Candidate, glossary.Mappings[0].Status); // 2 documents, 1 format

        var changes = AliasLifecycle.RecordVerified(glossary, Spec("fmt-b"), "d3", "replay", true, T0.AddHours(2), new AliasPolicy(3, 2));

        var alias = Assert.Single(glossary.Mappings);
        Assert.Equal("glossary.alias.activated", Assert.Single(changes).Kind);
        Assert.Equal((AliasStatus.Active, 3, 2), (alias.Status, alias.DistinctDocuments, alias.DistinctFormats));
        Assert.Equal("verified on 3 documents across 2 learned formats", alias.ActivationReason);
        var match = CanonicalData.Glossary.WithLearned(glossary.Mappings).ResolveHeader("COMP PD");
        Assert.Equal(("claim.indemnity.paid", "learned"), (match?.Field, match?.Method));
    }

    [Fact]
    public void The_confirm_format_review_accepts_edits_or_rejects_each_alias()
    {
        var glossary = new LearnedGlossary();
        AliasLifecycle.RecordLearned(glossary, [Learned("fmt-a", "d1"), Learned("fmt-a", "d1", "Rptd"), Learned("fmt-a", "d1", "Exp Pd")], new AliasPolicy(3, 2));

        var changes = AliasLifecycle.Review(glossary,
        [
            new AliasDecision("Comp Pd", "claim.indemnity.paid", AliasDecision.Accept),
            new AliasDecision("Rptd", "claim.indemnity.paid", AliasDecision.Edit, "claim.dateReported"),
            new AliasDecision("Exp Pd", "claim.indemnity.paid", AliasDecision.Reject),
        ], "uw-manager", T0);

        Assert.Equal(["glossary.alias.accepted", "glossary.alias.edited", "glossary.alias.rejected"], changes.Select(c => c.Kind));
        Assert.Equal((AliasStatus.Active, "reviewer", "uw-manager"), (glossary.Find("Comp Pd", "claim.indemnity.paid")!.Status, glossary.Find("Comp Pd", "claim.indemnity.paid")!.ActivationReason,
            glossary.Find("Comp Pd", "claim.indemnity.paid")!.ReviewedBy));
        var edited = glossary.Find("Rptd", "claim.dateReported")!;
        Assert.Equal((AliasStatus.Active, "claim.indemnity.paid"), (edited.Status, edited.EditedFrom));
        Assert.Equal(AliasStatus.Retired, glossary.Find("Exp Pd", "claim.indemnity.paid")!.Status);
        Assert.Throws<InvalidOperationException>(() => AliasLifecycle.Review(glossary, [new AliasDecision("Nope", "claim.status", AliasDecision.Accept)], "uw", T0));
    }

    [Fact]
    public void A_correction_of_a_value_read_through_an_alias_retires_it_and_it_is_not_learned_again()
    {
        var glossary = new LearnedGlossary();
        AliasLifecycle.RecordLearned(glossary, [Learned("fmt-a", "d1")], new AliasPolicy(3, 2));
        AliasLifecycle.Review(glossary, [new AliasDecision("Comp Pd", "claim.indemnity.paid", AliasDecision.Accept)], "uw", T0);

        Assert.Empty(AliasLifecycle.RetireOnCorrection(glossary, Spec("fmt-a"), "policyPeriods[0].claims[1].medical.paid", "uw", T0, "e1")); // another field
        var retired = AliasLifecycle.RetireOnCorrection(glossary, Spec("fmt-a"), "policyPeriods[0].claims[1].indemnity.paid", "uw", T0.AddDays(1), "override:d1@2:x");

        var alias = Assert.Single(retired).Alias;
        Assert.Equal((AliasStatus.Retired, "override:d1@2:x"), (alias.Status, alias.RetiredAuditEventId));
        Assert.Null(CanonicalData.Glossary.WithLearned(glossary.Mappings).ResolveHeader("Comp Pd"));
        Assert.Empty(AliasLifecycle.RecordLearned(glossary, [Learned("fmt-c", "d9")], new AliasPolicy(3, 2)));
        Assert.Equal(AliasStatus.Retired, Assert.Single(glossary.Mappings).Status);
    }

    [Theory]
    [InlineData("policyPeriods[0].claims[3].indemnity.paid", "claim.indemnity.paid")]
    [InlineData("policyPeriods[1].policyNumber", "period.policyNumber")]
    [InlineData("valuationDate", "document.valuationDate")]
    [InlineData("policyPeriods[1].subtotals.claimCount", "period.subtotals.claimCount")]
    [InlineData("policyPeriods[0].claims[1]", null)]
    public void A_field_path_maps_to_its_canonical_field(string path, string? field) => Assert.Equal(field, AliasLifecycle.CanonicalFieldOf(path));

    [Fact]
    public void A_0_1_0_sidecar_loads_as_one_candidate_per_alias_with_each_format_as_evidence()
    {
        var legacy = """
            {"sidecarVersion":"glossary-learned-0.1.0","mappings":[
              {"header":"Comp Pd","canonicalField":"claim.indemnity.paid","carrierName":"A","formatId":"fmt-a","formatVersion":"1","learnedBy":{"model":"m","promptVersion":"learn-v6"},"confidence":0.9,"firstSeenDocumentId":"d1","firstSeenUtc":"2026-10-01T00:00:00+00:00"},
              {"header":"COMP PD","canonicalField":"claim.indemnity.paid","carrierName":"B","formatId":"fmt-b","formatVersion":"1","learnedBy":{"model":"m","promptVersion":"learn-v6"},"confidence":0.9,"firstSeenDocumentId":"d2","firstSeenUtc":"2026-10-02T00:00:00+00:00"}]}
            """;

        var glossary = JsonSerializer.Deserialize<LearnedGlossary>(legacy, CanonicalJson.Options)!.Upgrade();

        var alias = Assert.Single(glossary.Mappings);
        Assert.Equal((LearnedGlossary.CurrentVersion, AliasStatus.Candidate, 2, 2), (glossary.SidecarVersion, alias.Status, alias.DistinctDocuments, alias.DistinctFormats));
        Assert.Equal(alias.Key, alias.Id);
        Assert.Same(glossary, glossary.Upgrade());
    }

    [Fact]
    public async Task Registries_persist_each_lifecycle_step()
    {
        var directory = Directory.CreateTempSubdirectory("aliases-").FullName;
        try
        {
            foreach (IFormatRegistry registry in new IFormatRegistry[] { new InMemoryFormatRegistry(), new DirectoryFormatRegistry(directory) })
            {
                await registry.AppendLearnedMappingsAsync([Learned("fmt-a", "d1")]);
                await registry.UpdateLearnedGlossaryAsync(g => AliasLifecycle.RecordVerified(g, Spec("fmt-a"), "d2", "replay", true, T0, new AliasPolicy(3, 2)));
                var changes = await registry.UpdateLearnedGlossaryAsync(g => AliasLifecycle.RecordVerified(g, Spec("fmt-b"), "d3", "replay", true, T0, new AliasPolicy(3, 2)));

                Assert.Equal("glossary.alias.activated", Assert.Single(changes).Kind);
                var stored = Assert.Single((await registry.GetLearnedGlossaryAsync()).Mappings);
                Assert.Equal((AliasStatus.Active, 3), (stored.Status, stored.Evidence.Count));
                Assert.Equal("claim.indemnity.paid", (await registry.GlossaryAsync()).ResolveHeader("Comp Pd")?.Field);
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
