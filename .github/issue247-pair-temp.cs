using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

internal static class Issue247PairEvidence
{
    private const int Warmup = 20_000;
    private const int Iterations = 300_000;

    internal static async Task RunAsync(string outputPath)
    {
        var context = new SharpLinkAdmissionContext(
            1, 2, RpcMethodKind.Unary, "issue247", null, null);
        var measurements = new List<Measurement>();

        await using var disabled = SharpLinkAdmissionController.CreateDisabled();
        await using var globalConcurrency = Create(o => o.Global.UseConcurrency(1024));
        await using var globalRate = Create(o => ConfigureTokenBucket(o.Global));
        await using var contract = Create(o => o.AddContract(1, r => r.UseConcurrency(1024)));
        await using var method = Create(o => o.AddMethod(1, 2, r => r.UseConcurrency(1024)));
        await using var globalContractMethod = Create(o =>
        {
            o.Global.UseConcurrency(1024);
            o.AddContract(1, r => r.UseConcurrency(1024));
            o.AddMethod(1, 2, r => r.UseConcurrency(1024));
        });
        await using var concurrencyRate = Create(o =>
        {
            o.Global.UseConcurrency(1024);
            ConfigureTokenBucket(o.Global);
        });
        await using var partition = Create(o => o.UsePartition(
            _ => "hot",
            p =>
            {
                p.MaxPartitions = 1;
                p.UseConcurrency(1024);
            }));
        await using var reject = Create(o => o.Global.UseConcurrency(1));
        using var rejectBlocker = (await reject.AcquireAsync(
            context, 1, false, CancellationToken.None)).Lease!;

        measurements.Add(Measure("Disabled", () => AcquireAndDispose(disabled, context)));
        measurements.Add(Measure("GlobalConcurrencyImmediate", () => AcquireAndDispose(globalConcurrency, context)));
        measurements.Add(Measure("GlobalRateImmediate", () => AcquireAndDispose(globalRate, context)));
        measurements.Add(Measure("ContractImmediate", () => AcquireAndDispose(contract, context)));
        measurements.Add(Measure("MethodImmediate", () => AcquireAndDispose(method, context)));
        measurements.Add(Measure("GlobalContractMethodImmediate", () => AcquireAndDispose(globalContractMethod, context)));
        measurements.Add(Measure("GlobalConcurrencyRateImmediate", () => AcquireAndDispose(concurrencyRate, context)));
        measurements.Add(Measure("PartitionImmediate", () => AcquireAndDispose(partition, context)));
        measurements.Add(Measure("ImmediateReject", () =>
        {
            if (reject.AcquireAsync(context, 1, false, CancellationToken.None).Result.IsAcquired)
                throw new InvalidOperationException("Expected rejection.");
        }));

        for (var i = 0; i < 100_000; i++)
            AcquireAndDispose(globalConcurrency, context);
        if (globalConcurrency.ActivePermits != 0 ||
            globalConcurrency.QueuedCalls != 0 ||
            globalConcurrency.QueuedBytes != 0)
            throw new InvalidOperationException("100k churn left accounting non-zero.");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new
        {
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ProcessorCount = Environment.ProcessorCount,
            Iterations,
            Measurements = measurements
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static async Task TraceAsync(string scenario)
    {
        var context = new SharpLinkAdmissionContext(
            1, 2, RpcMethodKind.Unary, "issue247-trace", null, null);
        await using var controller = scenario switch
        {
            "global-concurrency" => Create(o => o.Global.UseConcurrency(1024)),
            "global-rate" => Create(o => ConfigureTokenBucket(o.Global)),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        };

        for (var i = 0; i < 1_500_000; i++)
            AcquireAndDispose(controller, context);
    }

    private static Measurement Measure(string name, Action action)
    {
        for (var i = 0; i < Warmup; i++)
            action();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < Iterations; i++)
            action();
        var elapsed = Stopwatch.GetTimestamp() - started;
        var after = GC.GetAllocatedBytesForCurrentThread();

        return new Measurement(
            name,
            elapsed * 1_000_000_000.0 / Stopwatch.Frequency / Iterations,
            (after - before) / (double)Iterations);
    }

    private static void AcquireAndDispose(
        SharpLinkAdmissionController controller,
        SharpLinkAdmissionContext context)
    {
        var decision = controller.AcquireAsync(
            context, 1, false, CancellationToken.None).Result;
        if (!decision.IsAcquired)
            throw new InvalidOperationException(decision.Reason);
        decision.Lease!.Dispose();
    }

    private static SharpLinkAdmissionController Create(
        Action<SharpLinkAdmissionControlOptions> configure)
    {
        var options = new SharpLinkAdmissionControlOptions();
        configure(options);
        return SharpLinkAdmissionController.Create(options, []);
    }

    private static void ConfigureTokenBucket(SharpLinkAdmissionRuleOptions rule)
        => rule.UseTokenBucket(rate =>
        {
            rate.TokenLimit = 1_000_000_000;
            rate.TokensPerPeriod = 1_000_000_000;
            rate.ReplenishmentPeriod = TimeSpan.FromHours(1);
        });

    private sealed record Measurement(
        string Name,
        double NanosecondsPerOperation,
        double BytesPerOperation);
}
