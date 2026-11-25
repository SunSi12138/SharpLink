using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.LoadTestBase;
using SharpLink.Runtime;
using SharpLink.Server;

namespace SharpLink.LoadTest;

public sealed record ColdFirstRpcSample(
    int Index, bool FirstInProcess, long StopwatchFrequency, long ConnectTicks, long LocalHandleTicks,
    long? PrepareTicks, long FirstRpcTicks, long ConnectToFirstSuccessTicks);

internal readonly record struct ColdReceiver(ILoadTestService Service, long LocalHandleTicks, long? PrepareTicks);

internal static class ColdFirstRpcEvidence
{
    // One first-in-process receipt plus enough fresh sessions to yield 100,000
    // comparable observations per arm over eight same-host candidate trials.
    internal const int MaximumSamples = 12_501;

    internal static async Task RunAsync(LoadTestOptions options,
        Func<SharpLinkServerBuilder, SharpLinkServerBuilder> configureServer,
        Action<SharpLinkRuntimeOptions> configureRuntime,
        Func<ISharpLinkClient, CancellationToken, ValueTask<ColdReceiver>> open)
    {
        var samples = new List<ColdFirstRpcSample>(options.ColdFirstRpcSamples);
        await using var server = LoadTestTransportFactory.CreateServer(options.Transport, options.BindIp, options.Port,
            options.UdsPath, options.PipeName, options.HeartbeatCheckIntervalSeconds, options.HeartbeatTimeoutSeconds,
            configureServer, options.PerformanceProfile, options.SharedMemoryName, options.SharedMemoryCapacity,
            options.SharedMemorySpinCount, configureRuntime);
        using (var startup = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            await server.StartAsync(startup.Token);

        var payload = new string('x', Qualified32Workload.PayloadChars);
        try
        {
            for (var index = 1; index <= options.ColdFirstRpcSamples; index++)
            {
                // The process and listener stay up; only the physical client is new. Build
                // and disposal sit outside the shared Connect-to-first-success interval.
                await using var client = LoadTestTransportFactory.CreateClient(options.Transport, options.Host, options.Port,
                    options.UdsPath, options.PipeName, options.HeartbeatIntervalSeconds, options.HeartbeatTimeoutSeconds,
                    1, 1, options.PerformanceProfile, options.DisableRequestTimeout, options.RequestTimeout,
                    options.SharedMemoryName, options.SharedMemoryCapacity, options.SharedMemorySpinCount, configureRuntime);
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var start = Stopwatch.GetTimestamp();
                await client.ConnectAsync(budget.Token);
                var connected = Stopwatch.GetTimestamp();
                var receiver = await open(client, budget.Token);
                var firstCall = Stopwatch.GetTimestamp();
                var returned = await receiver.Service.EchoAsync(payload).AsTask().WaitAsync(budget.Token);
                var completed = Stopwatch.GetTimestamp();
                if (!string.Equals(payload, returned, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Cold sample {index} did not receive its original 32-byte response.");
                samples.Add(new(index, index == 1, Stopwatch.Frequency, connected - start, receiver.LocalHandleTicks,
                    receiver.PrepareTicks, completed - firstCall, completed - start));
            }
        }
        finally
        {
            // Any failed sample ends the trial; prior samples remain available to diagnose it.
            PerformanceReportWriter.Write(options.JsonOutputPath, "SharpLink.LoadTest.ColdFirstRpc", options, samples,
                LoadTestJsonContext.Default);
        }
    }
}
