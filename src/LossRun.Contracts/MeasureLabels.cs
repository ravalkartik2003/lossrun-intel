namespace LossRun.Contracts;

/// <summary>
/// Brief 22 Answers 1 (ADR 0018, measures as rows): the printed labels of measure rows, as .NET regexes over a whole cell
/// ("Inc:", "Pd:", "O/S:"). Ingestion uses the defaults as layout evidence; a spec may override them
/// (<c>tableLocator.records.measureLabels</c>).
/// </summary>
public sealed class MeasureLabels
{
    public const string DefaultPaid = @"(?i)^(?:pd|paid)\.?:?$";
    public const string DefaultReserve = @"(?i)^(?:o/s|os|outstanding|res(?:erve)?s?)\.?:?$";
    public const string DefaultIncurred = @"(?i)^(?:inc|incurred|incd)\.?:?$";

    public string? Paid { get; set; }
    public string? Reserve { get; set; }
    public string? Incurred { get; set; }

    public IEnumerable<(string Measure, string Pattern)> Patterns()
    {
        yield return ("paid", Paid ?? DefaultPaid);
        yield return ("reserve", Reserve ?? DefaultReserve);
        yield return ("incurred", Incurred ?? DefaultIncurred);
    }
}
