using System.Collections.Frozen;
using System.Reflection;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkMultiClusterLifecycleTests : SharpLinkMultiClusterClientTestBase
{
    [Test]
    public async Task StopDuringInitialConnectShouldRemainStoppedAfterSharedConnectFaults()
    {
        var blocked = new BlockingTransportFactory();
        await using var client = CreateStaticBuilder()
            .AddCluster("orders", child => child.UseTransport(blocked))
            .Build();

        var connecting = client.ConnectAsync().AsTask();
        await blocked.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await client.StopAsync();
        await EnsureThrows<OperationCanceledException>(async () => await connecting);

        Ensure(client.State == SharpLinkMultiClusterState.Stopped,
            "shutdown must own the terminal state when it races the initial shared connect");
        await client.StopAsync();
    }

    [Test]
    public async Task ReadyStateReadsShouldNotAllocate()
    {
        SharpLinkClusterKey cluster = "ready";
        var child = new CoordinatedUnregisterClient(SharpLinkConnectionState.Ready);
        var slot = new SharpLinkClusterSlot(cluster, child, AllowDynamicContracts: true);
        await using var client = new SharpLinkMultiClusterClient(
            new SharpLinkMultiClusterOptions(),
            new Dictionary<SharpLinkClusterKey, SharpLinkClusterSlot> { [cluster] = slot }
                .ToFrozenDictionary(),
            FrozenDictionary<Type, SharpLinkClusterRouteRegistration>.Empty,
            []);
        typeof(SharpLinkMultiClusterClient)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, (int)SharpLinkMultiClusterState.Ready);
        // Let tiered PGO finish its instrumented warm-up before measuring the steady-state path.
        for (var index = 0; index < 100_000; index++)
            _ = client.State;

        var before = GC.GetAllocatedBytesForCurrentThread();
        var readyReads = 0;
        for (var index = 0; index < 100_000; index++)
            readyReads += client.State == SharpLinkMultiClusterState.Ready ? 1 : 0;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Ensure(readyReads == 100_000, "every state read should preserve Ready semantics");
        Ensure(allocated == 0, $"ready state reads allocated {allocated} bytes");
    }

    [Test]
    public async Task CancellationAfterRemovePublicationShouldNotRestoreTheRetiredSlot()
    {
        SharpLinkClusterKey cluster = "retiring";
        var child = new BlockingRetiredClient();
        var slot = new SharpLinkClusterSlot(cluster, child, AllowDynamicContracts: true);
        await using var client = new SharpLinkMultiClusterClient(
            new SharpLinkMultiClusterOptions(),
            new Dictionary<SharpLinkClusterKey, SharpLinkClusterSlot> { [cluster] = slot }
                .ToFrozenDictionary(),
            FrozenDictionary<Type, SharpLinkClusterRouteRegistration>.Empty,
            []);
        using var cancellation = new CancellationTokenSource();

        var removal = client.RemoveClusterAsync(
            cluster,
            TimeSpan.FromSeconds(5),
            cancellation.Token).AsTask();
        await EnsureThrows<ArgumentException>(() =>
        {
            _ = client.GetClusterState(cluster);
            return Task.CompletedTask;
        });
        cancellation.Cancel();

        await EnsureThrows<OperationCanceledException>(async () => await removal);
        await EnsureThrows<ArgumentException>(() =>
        {
            _ = client.GetClusterState(cluster);
            return Task.CompletedTask;
        });

        var coordinatorStop = client.StopAsync().AsTask();
        await child.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(!coordinatorStop.IsCompleted,
            "caller cancellation must leave retired cleanup owned by coordinator shutdown");
        child.ReleaseStop();
        await coordinatorStop.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task TimerRangeExceedingRemoveTimeoutShouldRemainPendingUntilCleanupCompletes()
    {
        SharpLinkClusterKey cluster = "retiring";
        var child = new BlockingRetiredClient();
        var slot = new SharpLinkClusterSlot(cluster, child, AllowDynamicContracts: true);
        await using var client = new SharpLinkMultiClusterClient(
            new SharpLinkMultiClusterOptions(),
            new Dictionary<SharpLinkClusterKey, SharpLinkClusterSlot> { [cluster] = slot }
                .ToFrozenDictionary(),
            FrozenDictionary<Type, SharpLinkClusterRouteRegistration>.Empty,
            []);

        var removal = client.RemoveClusterAsync(cluster, TimeSpan.MaxValue).AsTask();
        await Task.Delay(50);
        Ensure(!removal.IsCompleted,
            "a timer-range-exceeding graceful timeout must remain pending while calls are active");

        child.ReleaseCalls();
        await child.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        child.ReleaseStop();
        var result = await removal.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(result is { Succeeded: true, ReferencesReleased: true, ForcedStop: false },
            "huge graceful timeout must complete normally after the retired child drains");
    }

    [Test]
    public async Task RetiredActiveCallsShouldForceStopAtTheOwningProviderBoundaryAndCleanUp()
    {
        var ownerProvider = new ManualTimeProvider();
        var unrelatedProvider = new ManualTimeProvider();
        SharpLinkClusterKey cluster = "provider-retiring";
        var child = new BlockingRetiredClient(ownerProvider);
        var slot = new SharpLinkClusterSlot(cluster, child, AllowDynamicContracts: true);
        var client = new SharpLinkMultiClusterClient(
            new SharpLinkMultiClusterOptions(),
            new Dictionary<SharpLinkClusterKey, SharpLinkClusterSlot> { [cluster] = slot }
                .ToFrozenDictionary(),
            FrozenDictionary<Type, SharpLinkClusterRouteRegistration>.Empty,
            []);
        try
        {
            var removal = client.RemoveClusterAsync(cluster, TimeSpan.FromSeconds(5)).AsTask();
            unrelatedProvider.Advance(TimeSpan.FromDays(1));
            ownerProvider.Advance(TimeSpan.FromSeconds(5).Subtract(TimeSpan.FromTicks(1)));
            await Task.Yield();

            Ensure(!removal.IsCompleted && child.StopCount == 0,
                "an unrelated clock and the owner tick before retirement expiry must keep active calls draining");
            Ensure(unrelatedProvider.ActiveTimerCount == 0 && ownerProvider.ActiveTimerCount > 0,
                "retired-call drain timers must be owned only by the child RuntimeContext provider");

            ownerProvider.Advance(TimeSpan.FromTicks(1));
            var result = await removal;
            await child.StopStarted.Task;
            Ensure(child.StopCount == 1,
                "retired cleanup must force one child stop at exact owner-provider equality");
            Ensure(result is { Succeeded: true, ReferencesReleased: false, ForcedStop: true },
                "the equality boundary must report forced cleanup while the child stop is still retained");

            child.ReleaseStop();
            await client.StopAsync();
            Ensure(client.FrameworkTaskSnapshotForDiagnostics.ActiveTasks == 0,
                "coordinator shutdown must join its completed retired cleanup task");
            Ensure(ownerProvider.ActiveTimerCount == 0 && child.StopCount == 1,
                "completed retirement must disarm provider timers and stop the child exactly once");
            Ensure((int)typeof(SharpLinkMultiClusterClient)
                .GetField("_transitionConnectionBudget", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(client)! == 0,
                "completed retirement must return its transition connection budget");
        }
        finally
        {
            child.ReleaseStop();
            await client.StopAsync();
        }
    }

    [Test]
    public async Task CoordinatorStopRacingRetiredDrainDueShouldOwnOneCleanupAndOneChildStop()
    {
        var ownerProvider = new ManualTimeProvider();
        SharpLinkClusterKey cluster = "provider-race";
        var child = new BlockingRetiredClient(ownerProvider);
        var slot = new SharpLinkClusterSlot(cluster, child, AllowDynamicContracts: true);
        var client = new SharpLinkMultiClusterClient(
            new SharpLinkMultiClusterOptions(),
            new Dictionary<SharpLinkClusterKey, SharpLinkClusterSlot> { [cluster] = slot }
                .ToFrozenDictionary(),
            FrozenDictionary<Type, SharpLinkClusterRouteRegistration>.Empty,
            []);
        try
        {
            var removal = client.RemoveClusterAsync(cluster, TimeSpan.FromSeconds(5)).AsTask();
            ownerProvider.Advance(TimeSpan.FromSeconds(5).Subtract(TimeSpan.FromTicks(1)));
            var coordinatorStop = client.StopAsync().AsTask();
            ownerProvider.Advance(TimeSpan.FromTicks(1));

            await removal;
            await child.StopStarted.Task;
            Ensure(child.StopCount == 1,
                "the due/Stop race must converge on one retired-child cleanup");
            Ensure(!coordinatorStop.IsCompleted,
                "coordinator Stop must retain ownership until the single retired child stop completes");

            child.ReleaseStop();
            await Task.WhenAll(removal, coordinatorStop);
            Ensure(child.StopCount == 1 && ownerProvider.ActiveTimerCount == 0,
                "the due/Stop race must neither duplicate Stop nor leak the drain timer");
            var snapshot = client.FrameworkTaskSnapshotForDiagnostics;
            Ensure(snapshot is { IsSealed: true, IsDrained: true, ActiveTasks: 0 },
                "coordinator shutdown must fully drain the one retired cleanup registration");
        }
        finally
        {
            child.ReleaseStop();
            await client.StopAsync();
        }
    }

    [Test]
    public async Task ForcedRemoveShouldUnpublishImmediatelyAndCoordinatorStopShouldTrackCleanup()
    {
        SharpLinkClusterKey cluster = "retiring";
        var child = new BlockingRetiredClient();
        var slot = new SharpLinkClusterSlot(cluster, child, AllowDynamicContracts: true);
        await using var client = new SharpLinkMultiClusterClient(
            new SharpLinkMultiClusterOptions(),
            new Dictionary<SharpLinkClusterKey, SharpLinkClusterSlot> { [cluster] = slot }
                .ToFrozenDictionary(),
            FrozenDictionary<Type, SharpLinkClusterRouteRegistration>.Empty,
            []);

        var removal = await client.RemoveClusterAsync(cluster, TimeSpan.Zero);
        Ensure(removal is { Succeeded: true, ReferencesReleased: false, ForcedStop: true },
            "a zero-timeout remove must report forced cleanup without rolling back publication");
        await child.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var coordinatorStop = client.StopAsync().AsTask();
        await Task.Delay(50);
        Ensure(!coordinatorStop.IsCompleted,
            "coordinator StopAsync must keep ownership of a retired child cleanup still in progress");

        child.ReleaseStop();
        await coordinatorStop.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(child.State == SharpLinkConnectionState.Stopped,
            "retired child cleanup must finish before coordinator StopAsync completes");
    }

    [Test]
    public async Task FaultedRetiredCleanupShouldBeReportedByCoordinatorStop()
    {
        SharpLinkClusterKey cluster = "retiring";
        var child = new FaultingRetiredClient();
        var slot = new SharpLinkClusterSlot(cluster, child, AllowDynamicContracts: true);
        var client = new SharpLinkMultiClusterClient(
            new SharpLinkMultiClusterOptions(),
            new Dictionary<SharpLinkClusterKey, SharpLinkClusterSlot> { [cluster] = slot }
                .ToFrozenDictionary(),
            FrozenDictionary<Type, SharpLinkClusterRouteRegistration>.Empty,
            []);

        var removal = await client.RemoveClusterAsync(cluster, TimeSpan.Zero);
        Ensure(removal is { Succeeded: true, ReferencesReleased: false, ForcedStop: true },
            "zero-timeout removal must leave the retired cleanup under coordinator ownership");
        await child.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        child.FailStop();

        var retiredFailure = await CaptureExceptionAsync(
            child.StopOperation.WaitAsync(TimeSpan.FromSeconds(2)));
        Ensure(retiredFailure is InvalidOperationException exception &&
               exception.Message.Contains("retired cleanup failed", StringComparison.Ordinal),
            "the retired child must expose the controlled cleanup failure");
        await WaitForConditionAsync(
            () => client.FrameworkTaskSnapshotForDiagnostics.RetainedFailures != 0,
            "the coordinator must retain the faulted cleanup until shutdown consumes it");

        var shutdownFailure = await CaptureExceptionAsync(client.StopAsync().AsTask());
        Ensure(shutdownFailure is InvalidOperationException shutdownException &&
               shutdownException.Message.Contains("retired cleanup failed", StringComparison.Ordinal),
            "coordinator shutdown must report a previously faulted retired cleanup");
    }
}
