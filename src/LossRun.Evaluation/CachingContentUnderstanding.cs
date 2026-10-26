using System.Security.Cryptography;
using System.Text;
using LossRun.Ingestion.ContentUnderstanding;

namespace LossRun.Evaluation;

/// <summary>
/// <c>eval run --mode local --cu-cache &lt;dir&gt;</c> (brief 17): keeps each Content Understanding result on disk, keyed by
/// SHA-256 of analyzer, content type and the file's bytes, so a re-run after a glossary or rule fix replays the whole pipeline
/// without paying for CU again. The cache holds the documents' extracted content: keep it under <c>eval-runs/</c> (ignored).
/// A cached call still meters its pages (the usage is in the stored body), so a re-run's report shows what the run would cost;
/// <see cref="Hits"/> says how many calls were not paid for.
/// </summary>
internal sealed class CachingContentUnderstanding(IContentUnderstandingService inner, string directory) : IContentUnderstandingService
{
    private int _hits;
    private int _misses;

    public int Hits => _hits;

    public int Misses => _misses;

    public Task EnsureAnalyzerAsync(string analyzerId, CancellationToken cancellationToken) => inner.EnsureAnalyzerAsync(analyzerId, cancellationToken);

    public async Task<CuCallResult> AnalyzeAsync(string analyzerId, BinaryData content, string contentType, CancellationToken cancellationToken)
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{analyzerId}\n{contentType}\n").Concat(content.ToArray()).ToArray()));
        var path = Path.Combine(directory, key + ".json");
        if (File.Exists(path))
        {
            Interlocked.Increment(ref _hits);
            var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
            return new CuCallResult(analyzerId, lines[0], string.Join('\n', lines.Skip(2)), TimeSpan.Zero, lines[1] + "+cache");
        }

        Interlocked.Increment(ref _misses);
        var result = await inner.AnalyzeAsync(analyzerId, content, contentType, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, $"{result.OperationId}\n{result.Mode}\n{result.RawJson}", cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
        return result;
    }
}
