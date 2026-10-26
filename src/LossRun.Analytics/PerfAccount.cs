namespace LossRun.Analytics;

/// <summary>
/// A deterministic synthetic account for the brief 10 §7 performance check (1,000 claims over 5 policy years). The same 32-bit
/// LCG is implemented in src/web/packages/metrics/test/perf-account.ts, so both engines time the same input and their snapshot
/// hashes can be compared.
/// </summary>
public static class PerfAccount
{
    private static readonly string[] Statuses = ["open", "closed", "closed", "reopened"];
    private static readonly string[] Types = ["indemnity", "medicalOnly", "medicalOnly"];
    private static readonly string[] Causes = ["56", "29", "97", "31", "45", "60", "81", "98"];
    private static readonly string[] Parts = ["42", "36", "38", "53", "90", "21"];
    private static readonly string[] Natures = ["52", "40", "60", "78", "10", "49"];
    private static readonly string[] Jurisdictions = ["CA", "CA", "CA", "CA", "CA", "CA", "CA", "NV", "AZ"];

    public static AnalyticsInput Build(int claims = 1000, int years = 5, int firstYear = 2020)
    {
        var x = 20261022u;
        uint Next()
        {
            unchecked
            {
                x = x * 1664525u + 1013904223u;
            }

            return x;
        }

        int Pick(int n) => (int)(Next() % (uint)n);
        static string Date(int year, int dayOfYear) => new DateOnly(year, 1, 1).AddDays(dayOfYear).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        var list = new List<AnalyticsClaim>(claims);
        for (var i = 0; i < claims; i++)
        {
            var year = firstYear + Pick(years);
            var doi = Pick(360);
            var status = Statuses[Pick(Statuses.Length)];
            var type = Types[Pick(Types.Length)];
            var indPaid = type == "indemnity" ? (long)Pick(6_000_000) : 0;
            var medPaid = (long)Pick(2_500_000);
            var open = status != "closed";
            list.Add(new AnalyticsClaim(
                $"perf/{i / 200}/{i % 200}", $"PF-{year % 100:00}-{i:0000}", $"PERF-{year}", year,
                Date(year, doi), Date(year, doi + 1 + Pick(60)), open ? null : Date(year, doi + 30 + Pick(600)), status, type,
                Pick(6) == 0 ? null : Natures[Pick(Natures.Length)], Pick(5) == 0 ? null : Causes[Pick(Causes.Length)], Pick(6) == 0 ? null : Parts[Pick(Parts.Length)],
                null, Pick(4) == 0 ? "cumulative strain" : null, Pick(3) == 0 ? null : Pick(5) == 0, null, Jurisdictions[Pick(Jurisdictions.Length)],
                Pick(100) == 0 ? "GL" : "WC",
                indPaid, open && type == "indemnity" ? Pick(4_000_000) : 0, medPaid, open ? Pick(1_500_000) : 0, Pick(400_000), open ? Pick(200_000) : 0,
                Pick(15) == 0 ? Pick(300_000) : null));
        }

        var payroll = Enumerable.Range(firstYear, years).ToDictionary(y => y, y => 5_000_000_00L + (y - firstYear) * 250_000_00L);
        var fte = Enumerable.Range(firstYear, years).ToDictionary(y => y, y => 120L + (y - firstYear) * 5);
        return new AnalyticsInput(list, new AnalyticsContext("Q-PERF", "2025-09-30", payroll, fte, "2024-06-30"), []);
    }
}
