using LossRun.Contracts;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.Extensions.DependencyInjection;

namespace LossRun.Tool;

/// <summary>
/// <c>lossrun admin …</c> (brief 12b): operations on the Durable Task Scheduler with the signed-in identity (Durable Task
/// Data Contributor). Today one command: terminate an upload's orchestration and its document sub-orchestrations, e.g.
/// before deploying orchestration code changes (in-flight instances replay through the new code and fail).
/// </summary>
internal static class Admin
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        var endpoint = Option("--endpoint") ?? Settings.Required("LOSSRUN_DTS_ENDPOINT", "the Durable Task Scheduler endpoint: terraform output durable_task_scheduler_endpoint");
        var hub = Option("--taskhub") ?? "lossrun";
        if (args is not ["terminate", ..] || Option("--upload") is not { } uploadId)
        {
            Console.Error.WriteLine("usage: lossrun admin terminate --upload <uploadId> [--reason <text>] [--endpoint <dts>] [--taskhub lossrun]");
            return 2;
        }

        var services = new ServiceCollection().AddLogging();
        services.AddDurableTaskClient(b => b.UseDurableTaskScheduler(endpoint, hub, LossRun.Ingestion.AzureCredentials.Default()));
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<DurableTaskClient>();
        var before = await client.GetInstanceAsync(uploadId);
        if (before is null)
        {
            Console.Error.WriteLine($"no orchestration {uploadId}");
            return 1;
        }

        await client.TerminateInstanceAsync(uploadId, new TerminateInstanceOptions(Option("--reason") ?? "terminated by operator", Recursive: true));
        var after = await client.WaitForInstanceCompletionAsync(uploadId, new CancellationTokenSource(TimeSpan.FromMinutes(2)).Token);
        Console.WriteLine($"{uploadId}: {before.RuntimeStatus} → {after.RuntimeStatus}");
        return 0;
    }
}
