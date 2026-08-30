using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.UnitTests.Runtime;
using static SharpLink.UnitTests.Client.SharpLinkClientReadinessPublicationSupport;
using static SharpLink.UnitTests.Client.SharpLinkClientReadinessSharedSupport;

namespace SharpLink.UnitTests.Client;

[NotInParallel]
public sealed class SharpLinkClientReadinessPublicationTests
{
    [Test]
    public async Task ReadinessSnapshotGetterShouldAllocateZeroBytes()
    {
        await using var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        for (var index = 0; index < 100_000; index++)
            _ = client.GetReadinessSnapshot();

        const int iterations = 100_000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var checksum = 0;
        for (var index = 0; index < iterations; index++)
        {
            var snapshot = client.GetReadinessSnapshot();
            checksum += snapshot.ActiveEndpoints + snapshot.TargetReadyEndpoints;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(checksum);

        Ensure(checksum == iterations * 2,
            "every getter call must observe the fixed Created publication");
        Ensure(allocated == 0,
            $"the lock-free readiness getter allocated {allocated} bytes over {iterations} calls");
    }

    [Test]
    public async Task PublicationShouldWakeAReaderThatCapturedThePreviousGeneration()
    {
        await using var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        var previous = client.ReadinessPublicationForTesting;

        client.PublishReadinessFacts(ReadyFacts);

        Ensure(previous.Changed.Task.IsCompleted,
            "publishing a new snapshot must complete the signal paired with the previous snapshot");
        await previous.Changed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(client.ReadinessPublicationForTesting.Snapshot.ReadyConnections == 1,
            "a reader that awaits after publication must immediately observe the new generation");
    }

    [Test]
    public async Task PublicationShouldBeVisibleToReadersThatStartAfterTheChange()
    {
        await using var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());

        client.PublishReadinessFacts(ReadyFacts);
        var publication = client.ReadinessPublicationForTesting;

        Ensure(publication.Snapshot == new SharpLinkClientReadinessSnapshot(
                SharpLinkConnectionState.Created, 1, 1, 1, 1),
            "a reader starting after publication must directly read the new immutable snapshot");
        Ensure(!publication.Changed.Task.IsCompleted,
            "the current generation signal must remain pending until a later public change");
    }

    [Test]
    public async Task PublicationShouldWakeAnAlreadyAwaitingReader()
    {
        await using var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        var previous = client.ReadinessPublicationForTesting;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = AwaitNextPublicationAsync(client, previous, entered);
        await entered.Task;
        Ensure(!waiter.IsCompleted,
            "the deterministic waiter must be suspended on the previous generation signal");

        client.PublishReadinessFacts(ReadyFacts);

        var observed = await waiter.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(observed.ReadyEndpoints == 1 && observed.ReadyConnections == 1,
            "an already-awaiting reader must resume on the new publication");
    }

    [Test]
    public async Task BackToBackPublicationsShouldExposeTheLatestGenerationWithoutMissedWakeup()
    {
        await using var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        var first = client.ReadinessPublicationForTesting;

        client.PublishReadinessFacts(ReadyFacts);
        var second = client.ReadinessPublicationForTesting;
        client.PublishReadinessFacts(NotReadyFacts);
        var third = client.ReadinessPublicationForTesting;

        Ensure(first.Changed.Task.IsCompleted && second.Changed.Task.IsCompleted,
            "each replaced generation must release readers even when publishers run back-to-back");
        Ensure(!third.Changed.Task.IsCompleted,
            "the latest generation must own the next incomplete change signal");
        Ensure(ReferenceEquals(client.ReadinessPublicationForTesting, third) &&
               third.Snapshot.ReadyEndpoints == 0 && third.Snapshot.ReadyConnections == 0,
            "readers may skip intermediate generations but must converge on the latest snapshot");
    }

    [Test]
    public async Task PublishingIdenticalFactsShouldReuseTheCurrentGeneration()
    {
        await using var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        var current = client.ReadinessPublicationForTesting;

        client.PublishReadinessFacts(NotReadyFacts);

        Ensure(ReferenceEquals(current, client.ReadinessPublicationForTesting),
            "an identical public snapshot must not allocate or publish another generation");
        Ensure(!current.Changed.Task.IsCompleted,
            "an identical publication request must not wake readiness readers");
    }

    [Test]
    public async Task ReadinessPublicationShouldSurviveTenThousandConcurrentChanges()
    {
        await using var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishingComplete = new int[1];

        var observer = Task.Run(async () =>
        {
            await start.Task;
            while (true)
            {
                var publication = client.ReadinessPublicationForTesting;
                AssertStressSnapshot(publication.Snapshot);
                if (Volatile.Read(ref publishingComplete[0]) != 0 &&
                    publication.Snapshot.ReadyConnections == 0)
                {
                    return;
                }

                await publication.Changed.Task;
            }
        });
        var publisher = Task.Run(async () =>
        {
            await start.Task;
            for (var index = 0; index < 9_999; index++)
            {
                client.PublishReadinessFacts((index & 1) == 0 ? ReadyFacts : NotReadyFacts);
                if ((index & 63) == 0)
                    await Task.Yield();
            }

            Volatile.Write(ref publishingComplete[0], 1);
            client.PublishReadinessFacts(NotReadyFacts);
        });

        start.TrySetResult();
        await Task.WhenAll(observer, publisher).WaitAsync(TimeSpan.FromSeconds(10));

        var final = client.ReadinessPublicationForTesting.Snapshot;
        Ensure(final.ReadyEndpoints == 0 && final.ReadyConnections == 0,
            "the stress observer must converge on the tenth-thousand terminal publication");
    }
}
