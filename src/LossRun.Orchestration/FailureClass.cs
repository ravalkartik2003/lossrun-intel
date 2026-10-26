using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.DurableTask;

namespace LossRun.Orchestration;

/// <summary>Brief 33 (ADR 0028): what the Durable layer may do with an activity failure.</summary>
public enum FailureClass
{
    /// <summary>Recoverable by running the activity again (the lower layers already retried and gave up).</summary>
    Transient,

    /// <summary>A defect or a missing input: running it again changes nothing. Fails fast with its cause.</summary>
    Permanent,

    /// <summary>Not in config/retry-policy.json: not retried, surfaced in the Needs-investigation queue by its type.</summary>
    Unknown,
}

/// <summary>One rule of config/retry-policy.json.</summary>
public sealed record FailureRule
{
    public string Type { get; init; } = "";

    /// <summary>A regex on the failure's message (written from a captured message).</summary>
    public string? WhenMessageMatches { get; init; }

    /// <summary>A property the app's exception-properties provider adds (<see cref="ExceptionProperties"/>), and the values that match.</summary>
    public PropertyCondition? WhenPropertyIn { get; init; }

    /// <summary>Classify the wrapped inner failure instead (a <c>DbUpdateException</c> over a <c>SqlException</c>).</summary>
    public bool InnerIsTransient { get; init; }

    /// <summary>
    /// Brief 34 A1.1: what the status and the escalation say for this rule, e.g. <c>EF retries exhausted on {inner.sqlErrorNumber}</c>.
    /// <c>{inner.name}</c> is the first property of that name on the inner chain (never a message: no value ever reaches a trace).
    /// </summary>
    public string? Reason { get; init; }
}

public sealed record PropertyCondition(string Name, IReadOnlyList<double> Values);

public sealed record StoragePolicy
{
    public int MaxAttempts { get; init; } = 3;
    public double FirstDelaySeconds { get; init; } = 5;
    public double Backoff { get; init; } = 2.0;
    public double MaxTotalSeconds { get; init; } = 60;

    [JsonIgnore]
    public TimeSpan MaxTotal => TimeSpan.FromSeconds(MaxTotalSeconds);

    /// <summary>The wait before the retry that follows attempt <paramref name="attempt"/> (1-based): first delay × backoff^(attempt − 1).</summary>
    public TimeSpan Delay(int attempt) => TimeSpan.FromSeconds(FirstDelaySeconds * Math.Pow(Backoff, Math.Max(0, attempt - 1)));
}

/// <summary>config/retry-policy.json. Passed to the orchestrators in their input (they never read a file: they replay).</summary>
public sealed record RetryConfig
{
    public string SchemaVersion { get; init; } = "";
    public StoragePolicy Storage { get; init; } = new();
    public IReadOnlyList<FailureRule> Transient { get; init; } = [];
    public IReadOnlyList<FailureRule> Permanent { get; init; } = [];
    public bool UnknownIsTransient { get; init; }

    private static readonly Lazy<RetryConfig> EmbeddedConfig = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LossRun.Config.retry-policy.json")
            ?? throw new InvalidOperationException("config/retry-policy.json is not embedded");
        return Parse(stream);
    });

    /// <summary>The copy embedded at build time.</summary>
    public static RetryConfig Embedded => EmbeddedConfig.Value;

    public static RetryConfig Parse(Stream json) =>
        JsonSerializer.Deserialize<RetryConfig>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip })
        ?? throw new InvalidDataException("retry-policy.json");

    /// <summary>The file at <paramref name="path"/> when it is there and valid (the app's config path), else the embedded copy.</summary>
    public static RetryConfig FromFileOrEmbedded(string? path)
    {
        if (path is { Length: > 0 } && File.Exists(path))
        {
            try
            {
                using var stream = File.OpenRead(path);
                return Parse(stream);
            }
            catch (Exception e) when (e is JsonException or InvalidDataException or IOException)
            {
                Console.Error.WriteLine($"retry policy {path} unreadable ({e.GetType().Name}); using the embedded copy");
            }
        }

        return Embedded;
    }
}

/// <summary>
/// The properties the Functions worker adds to an activity's failure details (its <c>IExceptionPropertiesProvider</c>; the capture shows they
/// reach the orchestrator's <see cref="TaskFailureDetails.Properties"/>). A <see cref="SqlException"/>'s message is the server's text only, without
/// its number, so the number travels here.
/// </summary>
public static class ExceptionProperties
{
    public const string SqlErrorNumber = "sqlErrorNumber";

    public static IDictionary<string, object?>? Of(Exception exception) => exception switch
    {
        SqlException sql => new Dictionary<string, object?> { [SqlErrorNumber] = sql.Number },
        _ => null,
    };
}

/// <summary>A failure as the classifier reads it: from the orchestrator's <see cref="TaskFailureDetails"/>, or from an exception in process.</summary>
public sealed record FailureInfo(string ErrorType, string Message, IReadOnlyDictionary<string, object?> Properties, FailureInfo? Inner)
{
    public static FailureInfo From(TaskFailureDetails failure) =>
        new(failure.ErrorType, failure.ErrorMessage ?? "", failure.Properties is { } p ? p.ToDictionary(kv => kv.Key, kv => kv.Value) : new Dictionary<string, object?>(),
            failure.InnerFailure is { } inner ? From(inner) : null);

    /// <summary>The same shape the worker's conversion produces (type full name, message, provider properties, inner chain).</summary>
    public static FailureInfo From(Exception exception) =>
        new(exception.GetType().FullName ?? exception.GetType().Name, exception.Message,
            ExceptionProperties.Of(exception) is { } p ? p.ToDictionary(kv => kv.Key, kv => kv.Value) : new Dictionary<string, object?>(),
            exception.InnerException is { } inner ? From(inner) : null);
}

/// <summary>Brief 33: classifies a failure by config/retry-policy.json. The chain is walked outermost first; the first matching rule decides.</summary>
public static class FailureClassifier
{
    public static FailureClass Classify(TaskFailureDetails failure, RetryConfig config) => Classify(FailureInfo.From(failure), config);

    public static FailureClass Classify(Exception exception, RetryConfig config) => Classify(FailureInfo.From(exception), config);

    public static FailureClass Classify(FailureInfo failure, RetryConfig config) => Match(failure, config).Class;

    /// <summary>
    /// "permanent: RetryLimitExceededException (EF retries exhausted on 40613)": the class and type, plus the matching rule's reason when it has one.
    /// Type and properties only, never the message: this goes into escalation traces.
    /// </summary>
    public static string Reason(FailureInfo failure, RetryConfig config)
    {
        var (@class, rule) = Match(failure, config);
        var text = rule?.Reason is { } template ? $" ({Fill(template, failure)})" : "";
        return $"{@class.ToString().ToLowerInvariant()}: {Short(failure.ErrorType)}{text}";
    }

    public static string Reason(TaskFailureDetails failure, RetryConfig config) => Reason(FailureInfo.From(failure), config);

    public static string Reason(Exception exception, RetryConfig config) => Reason(FailureInfo.From(exception), config);

    private static string Fill(string template, FailureInfo failure) =>
        Regex.Replace(template, @"\{inner\.(?<name>[A-Za-z0-9_]+)\}", m =>
        {
            for (var level = failure.Inner; level is not null; level = level.Inner)
            {
                if (level.Properties.TryGetValue(m.Groups["name"].Value, out var value) && Number(value) is { } n)
                {
                    return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            return "?";
        }, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static (FailureClass Class, FailureRule? Rule) Match(FailureInfo failure, RetryConfig config)
    {
        for (FailureInfo? level = failure; level is not null; level = level.Inner)
        {
            if (config.Transient.FirstOrDefault(r => Matches(r, level)) is { } transient)
            {
                return transient.InnerIsTransient
                    ? (level.Inner is { } inner && Classify(inner, config) == FailureClass.Transient ? FailureClass.Transient : FailureClass.Permanent, transient)
                    : (FailureClass.Transient, transient);
            }

            if (config.Permanent.FirstOrDefault(r => Matches(r, level)) is { } permanent)
            {
                return (FailureClass.Permanent, permanent);
            }
        }

        return (config.UnknownIsTransient ? FailureClass.Transient : FailureClass.Unknown, null);
    }

    /// <summary>"permanent: InvalidOperationException — Graph doc-1 is missing." (the status and escalation say whether to expect it to heal).</summary>
    public static string Describe(FailureClass @class, string? errorType, string? message) =>
        $"{@class.ToString().ToLowerInvariant()}: {Short(errorType)}" + (string.IsNullOrWhiteSpace(message) ? "" : $" — {FirstLine(message)}");

    public static string Short(string? errorType) => errorType is { Length: > 0 } t ? t[(t.LastIndexOf('.') + 1)..] : "error";

    private static string FirstLine(string message)
    {
        var line = message.Split('\n')[0].Trim();
        return line.Length <= 200 ? line : line[..200] + "…";
    }

    private static bool Matches(FailureRule rule, FailureInfo failure) =>
        string.Equals(rule.Type, failure.ErrorType, StringComparison.Ordinal)
        && (rule.WhenMessageMatches is not { } pattern || Regex.IsMatch(failure.Message, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        && (rule.WhenPropertyIn is not { } property || failure.Properties.TryGetValue(property.Name, out var value) && Number(value) is { } n && property.Values.Contains(n));

    /// <summary>Property values cross the wire as protobuf values (numbers are doubles) or arrive in process as ints.</summary>
    private static double? Number(object? value) => value switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.Number } e => e.GetDouble(),
        IConvertible c when value is not string => Convert.ToDouble(c, System.Globalization.CultureInfo.InvariantCulture),
        string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };
}
