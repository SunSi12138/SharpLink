using System;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using SharpLink.Abstractions;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class AdmissionPartitionControllerBenchmarks
{
    private static readonly Func<SharpLinkAdmissionContext, string?> SelectorA =
        static context => context.ConnectionId;
    private static readonly Func<SharpLinkAdmissionContext, string?> SelectorB =
        static context => $"replacement:{context.ConnectionId}";

    private SharpLinkAdmissionController _nonPartition = null!;
    private SharpLinkAdmissionController _partitionPermit = null!;
    private SharpLinkAdmissionController _partitionReject = null!;
    private SharpLinkAdmissionController _partitionQueue = null!;
    private AdmissionLease _partitionRejectBlocker = null!;
    private ProgramEnvironment _afterMaxIdleUpdates = null!;
    private ProgramEnvironment _afterConcurrencyUpdates = null!;
    private ProgramEnvironment _afterRateUpdates = null!;
    private ProgramEnvironment _afterSelectorReplacements = null!;
    private SharpLinkAdmissionContext _hotContext = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _hotContext = Context("hot-key");
        _nonPartition = CreateNonPartitionController();
        _partitionPermit = CreatePartitionController(
            maxPartitions: 64,
            idleTimeout: TimeSpan.FromHours(1),
            concurrency: 1024);
        WarmPartition(_partitionPermit, _hotContext);

        _partitionReject = CreatePartitionController(
            maxPartitions: 64,
            idleTimeout: TimeSpan.FromHours(1),
            concurrency: 1);
        _partitionRejectBlocker = (await _partitionReject.AcquireAsync(
            _hotContext, 1, false, CancellationToken.None)).Lease!;

        _partitionQueue = CreatePartitionController(
            maxPartitions: 64,
            idleTimeout: TimeSpan.FromHours(1),
            concurrency: 1,
            queue: true);

        _afterMaxIdleUpdates = ProgramEnvironment.Create(
            PartitionOptions(
                SelectorA,
                maxPartitions: 64,
                idleTimeout: TimeSpan.FromHours(1),
                concurrency: 1024));
        WarmPartition(_afterMaxIdleUpdates.Controller, _hotContext);
        for (var index = 0; index < 64; index++)
        {
            var expanded = (index & 1) == 0;
            _afterMaxIdleUpdates.Update(PartitionOptions(
                SelectorA,
                maxPartitions: expanded ? 128 : 64,
                idleTimeout: expanded ? TimeSpan.FromHours(2) : TimeSpan.FromHours(1),
                concurrency: 1024));
        }

        _afterConcurrencyUpdates = ProgramEnvironment.Create(
            PartitionOptions(
                SelectorA,
                maxPartitions: 64,
                idleTimeout: TimeSpan.FromHours(1),
                concurrency: 1024));
        WarmPartition(_afterConcurrencyUpdates.Controller, _hotContext);
        for (var index = 0; index < 64; index++)
        {
            _afterConcurrencyUpdates.Update(PartitionOptions(
                SelectorA,
                maxPartitions: 64,
                idleTimeout: TimeSpan.FromHours(1),
                concurrency: (index & 1) == 0 ? 2048 : 1024));
        }

        _afterRateUpdates = ProgramEnvironment.Create(
            PartitionRateOptions(SelectorA, tokenLimit: 1_000_000_000, tokensPerPeriod: 10_000));
        WarmPartition(_afterRateUpdates.Controller, _hotContext);
        for (var index = 0; index < 64; index++)
        {
            var expanded = (index & 1) == 0;
            _afterRateUpdates.Update(PartitionRateOptions(
                SelectorA,
                tokenLimit: expanded ? 999_000_000 : 1_000_000_000,
                tokensPerPeriod: expanded ? 9_000 : 10_000));
        }

        _afterSelectorReplacements = ProgramEnvironment.Create(
            PartitionOptions(
                SelectorA,
                maxPartitions: 64,
                idleTimeout: TimeSpan.FromHours(1),
                concurrency: 1024));
        for (var index = 0; index < 64; index++)
        {
            _afterSelectorReplacements.Update(PartitionOptions(
                (index & 1) == 0 ? SelectorB : SelectorA,
                maxPartitions: 64,
                idleTimeout: TimeSpan.FromHours(1),
                concurrency: 1024));
        }
        WarmPartition(_afterSelectorReplacements.Controller, _hotContext);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _partitionRejectBlocker.Dispose();
        await _nonPartition.DisposeAsync();
        await _partitionPermit.DisposeAsync();
        await _partitionReject.DisposeAsync();
        await _partitionQueue.DisposeAsync();
        await _afterMaxIdleUpdates.DisposeAsync();
        await _afterConcurrencyUpdates.DisposeAsync();
        await _afterRateUpdates.DisposeAsync();
        await _afterSelectorReplacements.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public void NonPartitionImmediatePermit()
        => AcquireAndDispose(_nonPartition, _hotContext);

    [Benchmark]
    public void ExistingPartitionKeyImmediatePermit()
        => AcquireAndDispose(_partitionPermit, _hotContext);

    [Benchmark]
    public bool ExistingPartitionKeyImmediateReject()
        => _partitionReject.AcquireAsync(
            _hotContext, 1, false, CancellationToken.None).Result.IsAcquired;

    [Benchmark(OperationsPerInvoke = 64)]
    public void NewPartitionKeyCreationBatch()
    {
        var controller = CreatePartitionController(
            maxPartitions: 64,
            idleTimeout: TimeSpan.FromHours(1),
            concurrency: 1);
        try
        {
            for (var index = 0; index < 64; index++)
                AcquireAndDispose(controller, Context($"new-key-{index}"));
        }
        finally
        {
            controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    [Benchmark]
    public void SteadyStateAfterRepeatedMaxPartitionsAndIdleTimeoutUpdates()
        => AcquireAndDispose(_afterMaxIdleUpdates.Controller, _hotContext);

    [Benchmark]
    public void SteadyStateAfterRepeatedPartitionConcurrencyUpdates()
        => AcquireAndDispose(_afterConcurrencyUpdates.Controller, _hotContext);

    [Benchmark]
    public void SteadyStateAfterRepeatedPartitionRateUpdates()
        => AcquireAndDispose(_afterRateUpdates.Controller, _hotContext);

    [Benchmark]
    public void SteadyStateAfterRepeatedSelectorReplacements()
        => AcquireAndDispose(_afterSelectorReplacements.Controller, _hotContext);

    [Benchmark]
    public async ValueTask QueueAndReleaseOnPartitionLimiter()
    {
        var blocker = (await _partitionQueue.AcquireAsync(
            _hotContext, 1, true, CancellationToken.None)).Lease!;
        var pending = _partitionQueue.AcquireAsync(
            _hotContext, 1, true, CancellationToken.None);
        blocker.Dispose();
        var admitted = await pending.ConfigureAwait(false);
        admitted.Lease!.Dispose();
    }

    private static SharpLinkAdmissionController CreateNonPartitionController()
    {
        var options = new SharpLinkAdmissionControlOptions();
        options.Global.UseConcurrency(1024);
        return SharpLinkAdmissionController.Create(options, []);
    }

    private static SharpLinkAdmissionController CreatePartitionController(
        int maxPartitions,
        TimeSpan idleTimeout,
        int concurrency,
        bool queue = false)
    {
        var options = PartitionOptions(SelectorA, maxPartitions, idleTimeout, concurrency);
        if (queue)
        {
            options.MaxQueuedCalls = 64;
            options.MaxQueuedBytes = 64 * 1024;
            options.MaxQueueDelay = TimeSpan.FromSeconds(5);
        }
        return SharpLinkAdmissionController.Create(options, []);
    }

    private static SharpLinkAdmissionControlOptions PartitionOptions(
        Func<SharpLinkAdmissionContext, string?> selector,
        int maxPartitions,
        TimeSpan idleTimeout,
        int concurrency)
    {
        var options = new SharpLinkAdmissionControlOptions();
        options.UsePartition(selector, partition =>
        {
            partition.MaxPartitions = maxPartitions;
            partition.IdleTimeout = idleTimeout;
            partition.UseConcurrency(concurrency);
        });
        return options;
    }

    private static SharpLinkAdmissionControlOptions PartitionRateOptions(
        Func<SharpLinkAdmissionContext, string?> selector,
        int tokenLimit,
        int tokensPerPeriod)
    {
        var options = new SharpLinkAdmissionControlOptions();
        options.UsePartition(selector, partition =>
        {
            partition.MaxPartitions = 64;
            partition.IdleTimeout = TimeSpan.FromHours(1);
            partition.UseTokenBucket(rate =>
            {
                rate.TokenLimit = tokenLimit;
                rate.TokensPerPeriod = tokensPerPeriod;
                rate.ReplenishmentPeriod = TimeSpan.FromHours(1);
            });
        });
        return options;
    }

    private static SharpLinkAdmissionContext Context(string connectionId)
        => new(1, 2, RpcMethodKind.Unary, connectionId, null, null, null);

    private static void WarmPartition(
        SharpLinkAdmissionController controller,
        SharpLinkAdmissionContext context)
        => AcquireAndDispose(controller, context);

    private static void AcquireAndDispose(
        SharpLinkAdmissionController controller,
        SharpLinkAdmissionContext context)
    {
        var decision = controller.AcquireAsync(
            context, 1, false, CancellationToken.None).Result;
        decision.Lease!.Dispose();
    }

    private sealed class ProgramEnvironment : IAsyncDisposable
    {
        private readonly SharpLinkAdmissionController _owner;

        private ProgramEnvironment(
            SharpLinkAdmissionController owner,
            AdmissionProgram program)
        {
            _owner = owner;
            Program = program;
        }

        internal AdmissionProgram Program { get; private set; }

        internal SharpLinkAdmissionController Controller => Program.Controller;

        internal static ProgramEnvironment Create(SharpLinkAdmissionControlOptions options)
        {
            var owner = SharpLinkAdmissionController.CreateDisabled();
            try
            {
                var program = owner.Kernel.CreateProgram(options, []);
                return new ProgramEnvironment(owner, program);
            }
            catch
            {
                owner.DisposeAsync().AsTask().GetAwaiter().GetResult();
                throw;
            }
        }

        internal void Update(SharpLinkAdmissionControlOptions options)
        {
            var source = Program;
            var candidate = source.Kernel.CreateUpdateProgram(source, options, [], out var plan);
            try
            {
                if (plan.RequiresTargetCommit)
                {
                    source.Kernel.BeginConcurrencyTargetCommit();
                    try
                    {
                        plan.Commit();
                    }
                    finally
                    {
                        source.Kernel.CompleteConcurrencyTargetCommit();
                    }
                    candidate.Controller.GrantConcurrencyWaitersAfterTargetCommit();
                }
                else
                {
                    plan.Commit();
                }

                Program = candidate;
                source.Retire();
            }
            catch
            {
                candidate.Retire();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Program.Retire();
            await _owner.DisposeAsync();
        }
    }
}
