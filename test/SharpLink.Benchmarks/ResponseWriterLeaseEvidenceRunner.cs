using System;
using System.Threading;
using System.Threading.Tasks;

namespace SharpLink.Benchmarks;

internal static class ResponseWriterLeaseEvidenceRunner
{
    internal static async Task RunAsync(int concurrency = 256, int delayMs = 500)
    {
        await using var env = await BenchmarkEnvironment.CreateAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new Task<int>[concurrency];
        for (var i = 0; i < concurrency; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                return await env.Rpc.SlowAsync(i, delayMs).ConfigureAwait(false);
            });
        }

        start.SetResult();
        await Task.Delay(Math.Max(20, delayMs / 2)).ConfigureAwait(false);

        Console.WriteLine($"concurrency={concurrency}");
        Console.WriteLine($"delay_ms={delayMs}");
        Console.WriteLine($"server_active_lease_count={env.ServerActiveLeaseCount}");
        Console.WriteLine($"server_peak_active_lease_count={env.ServerPeakActiveLeaseCount}");

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
