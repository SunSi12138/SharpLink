using System.Collections.Concurrent;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class StreamManagerLazyRoutingTests
{
    [Test]
    public async Task IdleAndReadOnlyMissesShouldNotMaterializeRoutingState()
    {
        var manager = new StreamManager();

        Ensure(!manager.HasMaterializedRoutingState, "new manager should not allocate stream routing state");

        await manager.DispatchChunkAsync(
            404,
            7,
            new ReadOnlySequence<byte>(new byte[] { 1 }));
        manager.Unregister(404, 7);
        manager.CompleteStream(404, 7, exception: null);
        manager.CompleteRequestStreams(404, exception: null);

        Ensure(!manager.HasMaterializedRoutingState,
            "read-only misses and removals must not materialize routing state");
        Ensure(manager.DroppedStreamFrames == 1, "unknown stream frame should still be counted");
    }

    [Test]
    public void CompleteAllBeforeFirstRegisterShouldStayUnmaterializedAndRejectLateRegister()
    {
        var manager = new StreamManager();
        var exception = new SharpLinkException(
            SharpLinkErrorCode.ConnectionClosed,
            "session closed");

        manager.CompleteAll(exception);

        Ensure(manager.IsTerminated, "termination should be published");
        Ensure(!manager.HasMaterializedRoutingState,
            "terminating an unused manager must not create stream routing state");

        var dispatcher = new RecordingDispatcher();
        manager.Register(1, 1, dispatcher);

        Ensure(dispatcher.CompleteCount == 1, "late registration should complete immediately");
        Ensure(ReferenceEquals(exception, dispatcher.LastException),
            "late registration should preserve the terminal exception");
        Ensure(manager.ActiveStreamCount == 0, "late registration must not become active");
        Ensure(!manager.HasMaterializedRoutingState,
            "late registration after termination must not resurrect routing state");
    }

    [Test]
    public void ConcurrencyOptionsShouldBeSnapshottedBeforeLazyMaterialization()
    {
        var options = new RuntimeConcurrencyOptions
        {
            StripeCount = 1,
            InitialMapCapacityPerStripe = 0
        };
        var manager = new StreamManager(options);

        options.StripeCount = 3;
        options.InitialMapCapacityPerStripe = -1;

        var dispatcher = new RecordingDispatcher();
        manager.Register(2, dispatcher);

        Ensure(manager.HasMaterializedRoutingState,
            "first successful registration should materialize routing state");
        Ensure(manager.ActiveStreamCount == 1,
            "later mutations of the caller-owned options must not affect lazy initialization");
        manager.Unregister(2);
    }

    [Test]
    public async Task ConcurrentFirstRegistersShouldPublishOneUsableRoutingState()
    {
        const int streamCount = 32;
        var manager = new StreamManager();
        var dispatchers = new ConcurrentDictionary<int, RecordingDispatcher>();
        using var start = new ManualResetEventSlim();
        var registrations = new Task[streamCount];

        for (var index = 0; index < registrations.Length; index++)
        {
            var requestId = index + 1;
            registrations[index] = Task.Run(() =>
            {
                var dispatcher = new RecordingDispatcher();
                dispatchers[requestId] = dispatcher;
                start.Wait();
                manager.Register(requestId, dispatcher);
            });
        }

        start.Set();
        await Task.WhenAll(registrations);

        Ensure(manager.HasMaterializedRoutingState, "first-use race should publish routing state");
        Ensure(manager.ActiveStreamCount == streamCount,
            "every concurrently registered stream should remain addressable");

        for (var requestId = 1; requestId <= streamCount; requestId++)
        {
            await manager.DispatchChunkAsync(
                requestId,
                new ReadOnlySequence<byte>(new byte[] { 1 }));
            Ensure(dispatchers[requestId].DispatchCount == 1,
                "every registration must route through the published map");
        }

        manager.CompleteAll(exception: null);
        Ensure(manager.ActiveStreamCount == 0, "drain should retire every registered stream");
    }

    [Test]
    public void FirstRegisterRacingCompleteAllShouldNotLeaveAnOrphanedStream()
    {
        var manager = new StreamManager();
        var dispatcher = new RecordingDispatcher();
        var exception = new SharpLinkException(
            SharpLinkErrorCode.ConnectionClosed,
            "session closed");
        using var reachedFirstMaterialization = new ManualResetEventSlim();
        using var continueFirstMaterialization = new ManualResetEventSlim();
        Exception? registerFailure = null;

        var registerThread = new Thread(() =>
        {
            StripedLongMapTestHooks.BeforeInitialize = () =>
            {
                reachedFirstMaterialization.Set();
                continueFirstMaterialization.Wait();
            };
            try
            {
                manager.Register(10_000, dispatcher);
            }
            catch (Exception failure)
            {
                registerFailure = failure;
            }
            finally
            {
                StripedLongMapTestHooks.BeforeInitialize = null;
            }
        })
        {
            IsBackground = true
        };

        registerThread.Start();
        try
        {
            Ensure(reachedFirstMaterialization.Wait(TimeSpan.FromSeconds(5)),
                "registration should reach first materialization after observing a non-terminal manager");

            manager.CompleteAll(exception);

            Ensure(manager.IsTerminated, "termination should publish while first materialization is paused");
            Ensure(!manager.HasMaterializedRoutingState,
                "CompleteAll must observe no published routing map before first materialization resumes");
        }
        finally
        {
            continueFirstMaterialization.Set();
        }

        Ensure(registerThread.Join(TimeSpan.FromSeconds(5)),
            "registration should finish after first materialization resumes");
        if (registerFailure is not null)
            throw new Exception("registration failed during deterministic first-use race", registerFailure);

        Ensure(manager.HasMaterializedRoutingState,
            "the stale pre-termination registration should still publish its first-use routing map");
        Ensure(dispatcher.CompleteCount == 1,
            "the post-registration termination check should complete the raced dispatcher exactly once");
        Ensure(ReferenceEquals(exception, dispatcher.LastException),
            "the raced dispatcher should observe the published terminal exception");
        Ensure(manager.ActiveStreamCount == 0,
            "the deterministic first-use race must not leave an active stream");
    }

    [Test]
    public async Task PreAdmissionReservationShouldMaterializeAndDrainNormally()
    {
        var manager = new StreamManager();
        var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());

        manager.ReservePreAdmissionStreams(
            77,
            1,
            buffers,
            static _ => true,
            static _ => { },
            static () => throw new InvalidOperationException("Capacity should not be exhausted."));

        Ensure(manager.HasMaterializedRoutingState,
            "pre-admission reservation is a real stream-routing first use");
        Ensure(manager.ActiveStreamCount == 1, "pre-admission stream should be active");

        await manager.DispatchChunkAsync(
            77,
            1,
            new ReadOnlySequence<byte>(new byte[] { 9 }));
        manager.CompleteRequestStreams(77, exception: null);

        Ensure(manager.ActiveStreamCount == 0, "pre-admission drain should release the entry");
    }

    [Test]
    public void UnaryOnlyConstructionShouldNeverMaterializeRoutingState()
    {
        for (var index = 0; index < 100_000; index++)
        {
            var manager = new StreamManager();
            Ensure(!manager.HasMaterializedRoutingState,
                "unary-only manager construction must retain no striped routing map");
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class RecordingDispatcher : IStreamDispatcher
    {
        internal int DispatchCount { get; private set; }
        internal int CompleteCount { get; private set; }
        internal Exception? LastException { get; private set; }

        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
        {
            _ = payload;
            DispatchCount++;
            return ValueTask.CompletedTask;
        }

        public void Complete(bool isError, string? errorMessage)
            => Complete(isError ? new Exception(errorMessage) : null);

        public void Complete(Exception? exception)
        {
            CompleteCount++;
            LastException = exception;
        }
    }
}
