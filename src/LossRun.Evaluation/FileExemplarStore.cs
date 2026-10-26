using System.Text.Json;
using LossRun.Contracts;
using LossRun.Learning;

namespace LossRun.Evaluation;

/// <summary>
/// Brief 34 A2: the evaluation runner's exemplar store, under the run folder (<c>registry/exemplars/</c>): per format, the last
/// <see cref="Kept"/> accepted documents (newest first) with their graphs, of which a drift repair is verified on the first
/// <see cref="LearnGate.Exemplars"/> (<c>learn-gate.json</c> <c>exemplars.count</c>), as production does with Blob.
/// </summary>
public sealed class FileExemplarStore(string folder) : IExemplarStore
{
    /// <summary>Kept per format, as production keeps them (UploadActivities: 5).</summary>
    public const int Kept = 5;

    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<IReadOnlyList<DocumentGraph>> GetAsync(string formatId, CancellationToken cancellationToken = default)
    {
        var graphs = new List<DocumentGraph>();
        foreach (var id in await ListAsync(formatId, cancellationToken).ConfigureAwait(false))
        {
            var path = GraphPath(formatId, id);
            if (File.Exists(path))
            {
                graphs.Add(JsonSerializer.Deserialize<DocumentGraph>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), CanonicalJson.Options)!);
            }

            if (graphs.Count == LearnGate.Default.Exemplars)
            {
                break;
            }
        }

        return graphs;
    }

    public async Task RecordAsync(string formatId, DocumentGraph graph, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, Safe(formatId)));
            await File.WriteAllTextAsync(GraphPath(formatId, graph.DocumentId), JsonSerializer.Serialize(graph, CanonicalJson.Options), cancellationToken).ConfigureAwait(false);
            var previous = await ListAsync(formatId, cancellationToken).ConfigureAwait(false);
            var ids = new List<string> { graph.DocumentId };
            ids.AddRange(previous.Where(i => i != graph.DocumentId).Take(Kept - 1));
            await File.WriteAllTextAsync(ListPath(formatId), JsonSerializer.Serialize(ids), cancellationToken).ConfigureAwait(false);
            foreach (var dropped in previous.Except(ids, StringComparer.Ordinal))
            {
                File.Delete(GraphPath(formatId, dropped)); // only the kept documents' graphs stay under the run folder
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<string>> ListAsync(string formatId, CancellationToken cancellationToken) =>
        File.Exists(ListPath(formatId))
            ? JsonSerializer.Deserialize<List<string>>(await File.ReadAllTextAsync(ListPath(formatId), cancellationToken).ConfigureAwait(false)) ?? []
            : [];

    private string ListPath(string formatId) => Path.Combine(folder, Safe(formatId) + ".json");

    private string GraphPath(string formatId, string documentId) => Path.Combine(folder, Safe(formatId), Safe(documentId) + ".json");

    private static string Safe(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
}
