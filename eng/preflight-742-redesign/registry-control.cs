namespace SharpLink.UnitTests.Runtime;

public partial class StreamManagerTests
{
    [Test]
    public async Task RegistrationMustNotPublishIntoAnUnlinkedRequestContainer()
    {
        const long requestId = 9601;
        var manager = new StreamManager();
        manager.Register(requestId, 1, new RecordingDispatcher());
        using var configured = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var dispatcher = new RegistrationBarrierDispatcher(() =>
        {
            configured.Set();
            if (!release.Wait(RaceCoordinationTimeout))
                throw new TimeoutException("Registration control was not released.");
        });
        var registration = Task.Run(() => manager.Register(requestId, 2, dispatcher));
        try
        {
            Ensure(configured.Wait(RaceCoordinationTimeout), "registration must reach configuration");
            manager.Unregister(requestId, 1);
        }
        finally
        {
            release.Set();
        }
        await registration.WaitAsync(RaceCoordinationTimeout);
        await manager.DispatchChunkAsync(requestId, 2, new ReadOnlySequence<byte>(new byte[] { 1 }));
        Ensure(dispatcher.DispatchCount == 1,
            "successful registration must stay reachable after the previous last route retires");
        manager.CompleteAll(exception: null);
        Ensure(manager.ActiveStreamCount == 0 && dispatcher.CompleteCount == 1,
            "the successfully registered route must remain reachable by connection cleanup");
    }

    [Test]
    [Arguments((ushort)0)]
    [Arguments((ushort)7)]
    public async Task PeerTerminalMustNotRetireAReplacementEntry(ushort streamId)
    {
        const long requestId = 9602;
        var manager = new StreamManager();
        var original = new RecordingDispatcher();
        var replacement = new RecordingDispatcher();
        manager.Register(requestId, streamId, original);
        // Keep the same request container mapped while the target route is replaced.
        manager.Register(requestId, 99, new RecordingDispatcher());
        StreamManagerTestHooks.AfterPeerTerminalMarked = (markedRequest, markedStream) =>
        {
            Ensure(markedRequest == requestId && markedStream == streamId,
                "the barrier must identify the captured route");
            manager.Unregister(requestId, streamId);
            manager.Register(requestId, streamId, replacement);
        };
        try
        {
            manager.CompletePeerStream(requestId, streamId, exception: null);
        }
        finally
        {
            StreamManagerTestHooks.AfterPeerTerminalMarked = null;
        }
        await manager.DispatchChunkAsync(requestId, streamId,
            new ReadOnlySequence<byte>(new byte[] { 1 }));
        Ensure(replacement.DispatchCount == 1 && replacement.CompleteCount == 0,
            "a terminal captured for the old entry must not complete or remove its replacement");
        manager.CompleteAll(exception: null);
    }

    private sealed class RegistrationBarrierDispatcher(Action beforeConfiguration)
        : IStreamConsumptionAwareDispatcher
    {
        public int DispatchCount { get; private set; }
        public int CompleteCount { get; private set; }
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
        {
            DispatchCount++;
            return ValueTask.CompletedTask;
        }
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload, int encodedByteCount)
            => DispatchAsync(payload);
        public void Complete(bool isError, string? errorMessage) => Complete(exception: null);
        public void Complete(Exception? exception) => CompleteCount++;
        public void SetBytesConsumedCallback(Action<long, ushort, int>? callback,
            long requestId, ushort streamId)
        {
            if (requestId != 0)
                beforeConfiguration();
        }
    }
}
