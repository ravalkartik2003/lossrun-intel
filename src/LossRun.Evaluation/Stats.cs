namespace LossRun.Evaluation;

/// <summary>A proportion with its 95% interval (Agresti-Coull on a cluster-adjusted effective sample size).</summary>
public sealed record Proportion(int Successes, int N, double Value, double Lower, double Upper, double DesignEffect, int Clusters)
{
    public string Format() => N == 0 ? "n/a" : $"{Value:P1} [{Lower:P1}–{Upper:P1}] (n={N}, docs={Clusters})";
}

/// <summary>
/// Confidence intervals for accuracy-type metrics (research A2: Agresti-Coull, cluster-adjusted). Observations are
/// clustered by document (all fields of a document share its rendering and layout), so the binomial variance is
/// inflated by the design effect measured with a document-level bootstrap; the Agresti-Coull interval is then computed
/// on the effective sample size n / deff.
/// </summary>
public static class Stats
{
    private const double Z = 1.959964;

    public static Proportion Estimate(IReadOnlyList<(string Cluster, bool Success)> observations, int bootstrap, int seed = 8)
    {
        var n = observations.Count;
        if (n == 0)
        {
            return new Proportion(0, 0, 0, 0, 0, 1, 0);
        }

        var successes = observations.Count(o => o.Success);
        var p = (double)successes / n;
        var clusters = observations.GroupBy(o => o.Cluster).Select(g => (Count: g.Count(), Successes: g.Count(x => x.Success))).ToArray();

        var deff = 1.0;
        if (bootstrap > 0 && clusters.Length > 1 && p is > 0 and < 1)
        {
            var random = new Random(seed);
            var samples = new double[bootstrap];
            for (var b = 0; b < bootstrap; b++)
            {
                long count = 0, hit = 0;
                for (var i = 0; i < clusters.Length; i++)
                {
                    var c = clusters[random.Next(clusters.Length)];
                    count += c.Count;
                    hit += c.Successes;
                }

                samples[b] = count == 0 ? p : (double)hit / count;
            }

            var mean = samples.Average();
            var varBoot = samples.Sum(s => (s - mean) * (s - mean)) / (bootstrap - 1);
            var varBinomial = p * (1 - p) / n;
            deff = Math.Max(1.0, varBoot / varBinomial);
        }

        var nEff = n / deff;
        var nTilde = nEff + Z * Z;
        var pTilde = (p * nEff + Z * Z / 2) / nTilde;
        var half = Z * Math.Sqrt(pTilde * (1 - pTilde) / nTilde);
        return new Proportion(successes, n, p, Math.Max(0, pTilde - half), Math.Min(1, pTilde + half), Math.Round(deff, 2), clusters.Length);
    }

    public static double Percentile(IReadOnlyList<double> values, double q)
    {
        if (values.Count == 0)
        {
            return double.NaN;
        }

        var sorted = values.Order().ToList();
        return sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Count) - 1, 0, sorted.Count - 1)];
    }
}
