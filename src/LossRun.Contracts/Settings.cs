namespace LossRun.Contracts;

/// <summary>
/// Environment settings the CLI tools and opt-in Azure tests need (brief 15): no environment's resource names are compiled
/// in as defaults. A missing setting fails with a message naming the variable and where its value comes from.
/// </summary>
public static class Settings
{
    /// <param name="name">The environment variable, e.g. <c>LOSSRUN_AOAI_ENDPOINT</c>.</param>
    /// <param name="source">Where the value comes from, e.g. <c>terraform output ai_endpoint</c>.</param>
    public static string Required(string name, string source) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Set {name} ({source}). No environment's resource names are built in.");

    public static Uri RequiredUri(string name, string source) => new(Required(name, source));
}
