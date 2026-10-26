using System.Text.Json;
using LossRun.Contracts;
using LossRun.Formats;

namespace LossRun.Evaluation.Tests;

/// <summary>
/// Brief 29 §1: the brief 21 proof (five hand aliases removed, corpus v4 run), re-scored offline from its committed run.json
/// under the configured policy (5 documents across 3 learned formats; hand-spec replays never count as formats). The
/// evidence is replayed in time order through the same promotion rule; no model call.
/// </summary>
public class AliasRescoreTests
{
    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "docs", "evaluation", "results", "2026-10-24-glossary-learning")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("docs/evaluation/results/2026-10-24-glossary-learning");
    }

    [Fact]
    public void Under_five_documents_across_three_learned_formats_two_of_the_five_removed_aliases_activate()
    {
        var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "docs", "evaluation", "results", "2026-10-24-glossary-learning", "run.json"))).RootElement;
        var hand = run.GetProperty("registryAtEnd").EnumerateArray().Where(f => f.GetProperty("createdBy").GetString() == "hand")
            .Select(f => f.GetProperty("formatId").GetString()!).ToHashSet(StringComparer.Ordinal);
        var learned = JsonSerializer.Deserialize<LearnedGlossary>(run.GetProperty("learnedAliasesAtEnd").GetRawText(), CanonicalJson.Options)!;
        var policy = new AliasPolicy(5, 3).WithHandFormats(hand); // brief 29 Answers 4: the registry names the hand specs

        string Outcome(string header, string field)
        {
            var recorded = learned.Mappings.Single(m => string.Equals(m.Header, header, StringComparison.OrdinalIgnoreCase) && m.CanonicalField == field);
            // The run predates the hand-replay marker; its evidence is read as stored, and the policy's hand formats exclude them.
            var alias = new LearnedMapping { Header = recorded.Header, CanonicalField = field, Status = AliasStatus.Candidate };
            foreach (var e in recorded.Evidence.OrderBy(e => e.AtUtc))
            {
                alias.Evidence.Add(e);
                if (alias.DistinctDocuments >= policy.MinDocuments && policy.FormatsOf(alias) >= policy.MinFormats)
                {
                    return $"active ({alias.DistinctDocuments} documents, {policy.FormatsOf(alias)} learned formats)";
                }
            }

            return $"candidate ({alias.DistinctDocuments} documents, {policy.FormatsOf(alias)} learned formats)";
        }

        Assert.StartsWith("active", Outcome("Claimant", "claim.claimantName"), StringComparison.Ordinal);
        Assert.StartsWith("active", Outcome("Loss Date", "claim.dateOfInjury"), StringComparison.Ordinal);
        // Only two learned formats print these in corpus v4: under 5/3 they stay candidates (a reviewer can still confirm them).
        Assert.Equal("candidate (122 documents, 2 learned formats)", Outcome("Reported", "claim.dateReported"));
        Assert.Equal("candidate (46 documents, 2 learned formats)", Outcome("Claim Number", "claim.claimNumber"));
        Assert.Equal("candidate (46 documents, 2 learned formats)", Outcome("Claim Status", "claim.status"));
    }
}
