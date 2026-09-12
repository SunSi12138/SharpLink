using System.Net;
using System.Threading;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class DynamicFixedWindowPartitionTests
{
    private static readonly Func<SharpLinkAdmissionContext, string?> Selector = static _ => "tenant-a";

    [Test]
    public async Task LimitOnlyPartitionUpdateShouldShareCounterAcrossProgramSnapshots()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => Configure(options, 3));
        var source = Current(server);
        Ensure(source.TryAcquireUse(), "test must pin the old partition policy snapshot");

        try
        {
            await ConsumeAsync(source);
            await ConsumeAsync(source);

            publicServer.UpdateAdmissionControl(options => Configure(options, 1));
            var shrunk = Current(server);
            await EnsureRateRejectedAsync(shrunk,
                "new partition snapshot must see the immediate limit-one target behind consumed=2");

            await ConsumeAsync(source);
            await EnsureRateRejectedAsync(shrunk,
                "old snapshot's third grant must charge the same partition counter seen by the new snapshot");

            publicServer.UpdateAdmissionControl(options => Configure(options, 4));
            var expanded = Current(server);
            await ConsumeAsync(expanded);
            await EnsureRateRejectedAsync(expanded,
                "3 consumed permits followed by 1 -> 4 must expose exactly one additional permit");
        }
        finally
        {
            source.ReleaseUse();
        }
    }

    [Test]
    public async Task OldSnapshotCreatingEntryAfterPublicationShouldSeePublishedQueuedTarget()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
        {
            Configure(options, 1);
            ConfigureQueue(options);
        });
        var source = Current(server);
        Ensure(source.TryAcquireUse(), "test must pin source before the partition entry exists");

        try
        {
            publicServer.UpdateAdmissionControl(options =>
            {
                Configure(options, 3);
                ConfigureQueue(options);
            });

            await ConsumeAsync(source, allowQueue: true);
            var rebound = await source.Controller.AcquireAsync(
                Context(), 1, allowQueue: true, CancellationToken.None);
            Ensure(rebound.IsAcquired,
                "old snapshot retry on an entry created after publication must use the published queued target");
            rebound.Lease!.Dispose();
        }
        finally
        {
            source.ReleaseUse();
        }
    }

    [Test]
    [NotInParallel]
    public async Task PartitionImmediateIncreaseMustNotWakeQueuedWorkBeforeProgramPublication()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options =>
        {
            Configure(options, 1);
            ConfigureQueue(options);
        });
        var source = Current(server);
        Ensure(source.TryAcquireUse(), "queued old partition request must keep its source snapshot alive");
        using var candidateBuilt = new ManualResetEventSlim();
        using var releaseCandidate = new ManualResetEventSlim();

        try
        {
            await ConsumeAsync(source, allowQueue: true);
            var queued = source.Controller.AcquireAsync(
                Context(), 5, allowQueue: true, CancellationToken.None).AsTask();
            await WaitUntilAsync(
                () => source.Kernel.QueuedCalls == 1 && source.Kernel.QueuedBytes == 5,
                "old partition rate waiter must own one outer queue reservation before update");

            SharpLinkServer.AfterAdmissionCandidateBuiltForTests = (owner, _) =>
            {
                if (!ReferenceEquals(owner, server))
                    return;
                candidateBuilt.Set();
                if (!releaseCandidate.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("partition FixedWindow publication barrier timed out");
            };

            var updateTask = Task.Run(() => publicServer.UpdateAdmissionControl(options =>
            {
                Configure(options, 3);
                ConfigureQueue(options);
            }));
            Ensure(candidateBuilt.Wait(TimeSpan.FromSeconds(5)),
                "partition target must reach the post-build/pre-publication barrier");
            Ensure(!queued.IsCompleted && ReferenceEquals(source, Current(server)),
                "candidate preparation must not expose the larger partition limit before Program publication");

            releaseCandidate.Set();
            await updateTask.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(!ReferenceEquals(source, Current(server)),
                "replacement Program must become current before queued target activation is observed");

            var admitted = await queued.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(admitted.IsAcquired,
                "post-publication partition target must wake the old queued waiter on the shared counter");
            admitted.Lease!.Dispose();
            Ensure(source.Kernel.QueuedCalls == 0 && source.Kernel.QueuedBytes == 0,
                "partition queued continuation must release outer accounting exactly once");
        }
        finally
        {
            SharpLinkServer.AfterAdmissionCandidateBuiltForTests = null;
            releaseCandidate.Set();
            source.ReleaseUse();
        }
    }

    private static SharpLinkServer CreateServer()
        => (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .Build();

    private static AdmissionProgram Current(SharpLinkServer server)
        => server.CurrentAdmissionProgramForTests ??
           throw new Exception("assert failed: expected enabled admission publication");

    private static void Configure(SharpLinkAdmissionControlOptions options, int permitLimit)
        => options.UsePartition(Selector, partition =>
        {
            partition.MaxPartitions = 4;
            partition.IdleTimeout = TimeSpan.FromHours(1);
            partition.UseFixedWindow(rate =>
            {
                rate.PermitLimit = permitLimit;
                rate.Window = TimeSpan.FromHours(1);
            });
        });

    private static void ConfigureQueue(SharpLinkAdmissionControlOptions options)
    {
        options.MaxQueuedCalls = 4;
        options.MaxQueuedBytes = 1024;
        options.MaxQueueDelay = TimeSpan.FromMinutes(1);
    }

    private static async Task ConsumeAsync(AdmissionProgram program, bool allowQueue = false)
    {
        var decision = await program.Controller.AcquireAsync(
            Context(), 1, allowQueue, CancellationToken.None);
        Ensure(decision.IsAcquired, "expected partition FixedWindow permit");
        decision.Lease!.Dispose();
    }

    private static async Task EnsureRateRejectedAsync(AdmissionProgram program, string scenario)
    {
        var decision = await program.Controller.AcquireAsync(
            Context(), 1, allowQueue: false, CancellationToken.None);
        decision.Lease?.Dispose();
        Ensure(!decision.IsAcquired && decision.Reason == "rate" && decision.Scope == "partition", scenario);
    }

    private static SharpLinkAdmissionContext Context()
        => new(101, 202, RpcMethodKind.Unary, "dynamic-fixed-partition", null, null);

    private static async Task WaitUntilAsync(Func<bool> condition, string scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (!condition())
                await Task.Delay(10, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new Exception($"assert failed: {scenario}");
        }
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }
}
