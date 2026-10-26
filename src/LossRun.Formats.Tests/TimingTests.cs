using System.Diagnostics;
using LossRun.Formats.Replay;
using Xunit.Abstractions;

namespace LossRun.Formats.Tests;

/// <summary>Replay latency on this machine (reported in brief 05); asserts only a generous ceiling.</summary>
public class TimingTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("native", "synthetic-mutual-pdf")]
    [InlineData("scanned", "synthetic-mutual-pdf")]
    [InlineData("xlsx", "synthetic-mutual-xlsx")]
    [InlineData("csv", "synthetic-mutual-csv")]
    public void Replay_is_fast(string family, string formatId)
    {
        var graph = family switch { "native" => Graphs.Native(), "scanned" => Graphs.Scanned(), "xlsx" => Graphs.Xlsx(), _ => Graphs.Csv() };
        var spec = Graphs.Spec(formatId);
        ReplayEngine.Execute(graph, spec); // warm-up (JIT, regex compilation)

        const int runs = 50;
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < runs; i++)
        {
            ReplayEngine.Execute(graph, spec);
        }

        stopwatch.Stop();
        var perRun = stopwatch.Elapsed.TotalMilliseconds / runs;
        output.WriteLine($"replay {family}: {perRun:0.00} ms per document (mean of {runs}, incl. 26-rule catalog)");
        Assert.True(perRun < 1000);
    }
}
