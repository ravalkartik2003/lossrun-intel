using System.Text.Json;
using LossRun.Evaluation;

namespace LossRun.Evaluation.Tests;

/// <summary>Brief 20 Answers 3: the committed learn-prompt artefact is current, and run.json records the same value the CI gate checks.</summary>
public class PromptArtefactTests
{
    [Fact]
    public void The_committed_learn_prompt_artefact_matches_the_prompt_schema_and_lint_files()
    {
        var computed = PromptArtefact.Compute(AppContext.BaseDirectory);
        Assert.NotNull(computed);

        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, PromptArtefact.PathsFile)))
        {
            root = Path.GetDirectoryName(root)!;
        }

        var committed = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "docs", "evaluation", "learn-prompt-artefact.json"))).RootElement.GetProperty("sha256").GetString();
        Assert.True(committed == computed,
            "docs/evaluation/learn-prompt-artefact.json is stale: run `python tools/prompt-gate.py --write` and attach a corpus v4 run made from this code (docs/operations/tuning-loop.md §3).");
    }
}
