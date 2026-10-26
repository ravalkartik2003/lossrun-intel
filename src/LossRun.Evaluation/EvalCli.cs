using System.Globalization;

namespace LossRun.Evaluation;

/// <summary>
/// <c>lossrun eval …</c> (brief 08):
/// <code>
/// eval generate --out &lt;corpus&gt; [--seed 8] [--constructs | --v4 | --v5]   (brief 19: the ADR 0018 constructs plan alone; corpus v4 = v3 + constructs; brief 22: corpus v5 = v4 + c10)
/// eval run      --corpus &lt;corpus&gt; --out &lt;results&gt; [--mode local|api|stub] [--concurrency 8] [--inline] [--limit n] [--only c1,c2] [--only-blind|--only-tuning &lt;seal-manifest.json&gt;] [--cu-cache &lt;dir&gt;]   (--only-blind needs an --out under a blind/ folder)
/// eval score    --corpus &lt;corpus&gt; --results &lt;results&gt; --out &lt;report-dir&gt; [--bootstrap 1000] [--all-fields]   (brief 31: studies/all-fields.md, every compared field and every wrong delivered value)
/// eval seal     --corpus &lt;corpus&gt; --out &lt;dir&gt; [--tuning 100] [--policies &lt;file,policyNumber csv&gt;]   (brief 31: policy clusters never split)
/// eval import  --in &lt;folder&gt; --out &lt;corpus&gt; [--carriers carriers.csv] [--copy]   (brief 14: a real archive → manifest.json; files are referenced in place unless --copy; passwords only from the environment, LOSSRUN_PASSWORDS)
/// eval verify  --corpus &lt;corpus&gt; --seal &lt;seal-manifest.json&gt; [--published-hash &lt;sha256&gt;]   (exit 1 when the blind list no longer hashes to blindManifestSha256 or the published hash, or a blind file changed after sealing)
/// eval truth   --archive &lt;truth.csv&gt; --aligned &lt;aligned.csv&gt; --corpus &lt;corpus&gt; [--adjudication adjudication.xlsx] [--accept-defaults]   (brief 14: adjudicated alignment → truth/; exit 3 when archive-only rows were left at their default, unless --accept-defaults)
/// eval align    --archive &lt;truth.csv&gt; --results &lt;results&gt; --out &lt;dir&gt; [--corpus &lt;corpus&gt;]   (aligned.csv + adjudication.xlsx)
/// eval chat     --out &lt;dir&gt; [--corpus eval-runs/corpus-v2] [--base &lt;api&gt;] [--run &lt;key&gt; --skip-upload]   (brief 12)
/// eval chat-text --out &lt;dir&gt; [--corpus eval-runs/corpus-v3] [--base &lt;api&gt;] [--run &lt;key&gt; --skip-upload] [--large Q-LOAD-1000 --large-turns 10]   (brief 13)
/// eval chat-corrections --out &lt;dir&gt; [--corpus eval-runs/corpus-v5b] [--base &lt;api&gt;] [--run &lt;key&gt;]   (brief 24 step 5; not yet run)
/// eval chat-multiturn --out &lt;dir&gt; [--base &lt;api&gt;] [--quote Q-DEMO-11] [--runs 3]   (brief 24 step 6; brief 31 Answers 8: the gate is the median of the runs)
/// eval study    &lt;scan-robustness|inter-carrier-cosine|classifier-skip|multi-exemplar-learn|cost-by-path&gt; --corpus … --results … --out …
/// </code>
/// </summary>
public static class EvalCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? Option(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        string Required(string name) => Option(name) ?? throw new ArgumentException($"{name} is required.");
        int Int(string name, int fallback) => Option(name) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

        switch (args.FirstOrDefault())
        {
            case "generate":
            {
                var manifest = CorpusGenerator.Generate(Required("--out"), Int("--seed", 8), Console.WriteLine, args.Contains("--constructs"), args.Contains("--v4"), args.Contains("--v5"));
                Console.WriteLine($"corpus: {manifest.Entries.Count} documents, {manifest.Entries.Sum(e => e.Pages)} pages, " +
                    $"{manifest.Entries.Sum(e => e.Bytes) / 1048576.0:0.0} MB → {Option("--out")}");
                foreach (var g in manifest.Entries.GroupBy(e => (e.Carrier, e.InputType)).OrderBy(g => g.Key))
                {
                    Console.WriteLine($"  {g.Key.Carrier} {g.Key.InputType,-28} {g.Count(),4} docs {g.Sum(e => e.Pages),5} pages");
                }

                return 0;
            }

            case "golden":
                return AnalyticsEval.Golden(Required("--corpus"), Required("--out"), Int("--cases", 60), Int("--max-claims", 40));

            case "insights":
                return AnalyticsEval.Insights(Required("--corpus"), Required("--results"), Required("--out"), Option("--reference"), Option("--source") ?? "delivered");

            case "run":
                if (Option("--only-blind") is not null && !Tune.IsBlindPath(Required("--out")))
                {
                    // Brief 30 item 26: blind results live under $WORK/blind/, the segment .cursorignore and `lossrun tune` key on.
                    Console.Error.WriteLine($"eval run: refusing --only-blind with --out '{Option("--out")}': blind results go under a 'blind' folder (e.g. $WORK/blind/results), " +
                        "where the tuning loop refuses to read them (docs/operations/blind-run.md, section 6).");
                    return 2;
                }

                return await EvalRunner.RunAsync(Required("--corpus"), Required("--out"), Option("--mode") ?? "local", Int("--concurrency", 8), args.Contains("--inline"),
                    Option("--limit") is { } l ? int.Parse(l, CultureInfo.InvariantCulture) : null, Option("--only")?.Split(','), args.Contains("--second-opinion"),
                    Option("--only-blind") is { } sealPath ? [.. Sealing.Load(sealPath).Blind.Select(b => b.Id)]
                    : Option("--only-tuning") is { } tuningSeal ? [.. Sealing.Load(tuningSeal).Tuning.Select(t => t.Id)] : null, Option("--cu-cache"))
                    .ConfigureAwait(false);

            case "verify":
                return Sealing.VerifyCommand(Required("--corpus"), Required("--seal"), Option("--published-hash"));

            case "chat":
                return await ChatEval.RunAsync(Option("--corpus") ?? "eval-runs/corpus-v2", Required("--out"), Option("--base"), Option("--run"), args.Contains("--skip-upload"))
                    .ConfigureAwait(false);

            case "chat-text":
                return await ChatTextEval.RunAsync(Option("--corpus") ?? "eval-runs/corpus-v3", Required("--out"), Option("--base"), Option("--run"), args.Contains("--skip-upload"),
                    Option("--large") ?? "Q-LOAD-1000", Int("--large-turns", 10)).ConfigureAwait(false);

            case "chat-corrections":
                // Brief 24 step 5: written in brief 24, first run in step 7 (it uploads, overrides and asks against the deployed API).
                return await ChatCorrectionsEval.RunAsync(Option("--corpus") ?? "eval-runs/corpus-v5b", Required("--out"), Option("--base"), Option("--run")).ConfigureAwait(false);

            case "chat-multiturn":
                // Brief 24 step 6: written in brief 24, first run in step 7 (it asks against the deployed API).
                return await ChatMultiTurnEval.RunAsync(Required("--out"), Option("--base"), Option("--quote") ?? "Q-DEMO-11", Int("--runs", 3)).ConfigureAwait(false);

            case "reconcile":
                return await Reconcile.RunAsync(Required("--billing"), Option("--runs") ?? "eval-runs", Option("--sql"),
                    DateOnly.Parse(Required("--from"), CultureInfo.InvariantCulture), DateOnly.Parse(Required("--to"), CultureInfo.InvariantCulture), Required("--out"),
                    args.Contains("--publish"), Option("--note")).ConfigureAwait(false);

            case "score":
                return Scorer.Run(Required("--corpus"), Required("--results"), Required("--out"), Int("--bootstrap", 1000), args.Contains("--all-fields"));

            case "import":
                if (args.Contains("--passwords"))
                {
                    // Brief 30 item 49: a password on the command line lands in shell history and process listings.
                    Console.Error.WriteLine("eval import: --passwords is not accepted on the command line; set LOSSRUN_PASSWORDS (';'-separated, from Key Vault) in the environment.");
                    return 2;
                }

                return Import.Run(Required("--in"), Required("--out"), Option("--carriers"), args.Contains("--copy"),
                    (Environment.GetEnvironmentVariable("LOSSRUN_PASSWORDS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));

            case "truth":
                return TruthBuilder.Run(Required("--archive"), Required("--aligned"), Required("--corpus"), Option("--adjudication"), args.Contains("--accept-defaults"));

            case "seal":
                return Sealing.Seal(Required("--corpus"), Required("--out"), Int("--tuning", 100), Int("--seed", 8), Option("--policies"));

            case "align":
                return Alignment.Run(Required("--archive"), Required("--results"), Required("--out"), Option("--filter"), Option("--corpus"));

            case "study":
                return await Studies.RunAsync(args.ElementAtOrDefault(1) ?? "", Required("--corpus"), Required("--results"), Required("--out"), Option)
                    .ConfigureAwait(false);

            case "replay":
            {
                // Debug aid: replay one spec on one graph (eval-runs/<run>/graphs/<id>.json) and print the verification.
                var graph = System.Text.Json.JsonSerializer.Deserialize<LossRun.Contracts.DocumentGraph>(File.ReadAllText(Required("--graph")), LossRun.Contracts.CanonicalJson.Options)!;
                var spec = LossRun.Formats.FormatJson.Parse(File.ReadAllText(Required("--spec")));
                var verification = LossRun.Learning.SpecVerifier.Verify(graph, spec, Option("--carrier") is { } hint ? new LossRun.Contracts.SubmissionContext(hint) : null);
                Console.WriteLine(verification.Summary);
                Console.WriteLine(verification.Feedback());
                Console.WriteLine($"spec {spec.FormatId}@{spec.Version} ({spec.FileKind}, {spec.ColumnMap.Count} columns); graph {graph.Tables.Count} tables, {graph.Paragraphs.Count} paragraphs");
                foreach (var o in verification.Replay.RuleOutcomes.Where(o => o.Status == LossRun.Rules.RuleStatus.Fail))
                {
                    Console.WriteLine($"  {o.RuleId} {o.Severity}: {o.Message} | fields {string.Join(",", o.AffectedFields)} | values {string.Join(",", (o.Values ?? new Dictionary<string, string>()).Select(v => $"{v.Key}={v.Value}"))} | conf {string.Join(",", o.AffectedFields.Select(f => verification.Replay.Document.Lineage?.GetValueOrDefault(f)?.Confidence?.ToString("0.00") ?? "-"))}");
                }

                return 0;
            }

            default:
                Console.Error.WriteLine("usage: lossrun eval generate|run|score|seal|align|study|replay … (see LossRun.Evaluation.EvalCli)");
                return 2;
        }
    }
}
