using System.Text.Json;

namespace SharpLink.IntegrationTests;

public sealed class DynamicInputDrainComponentTests
{
    [Test]
    public void SingleInputCaptureShouldAllocateNoPerCallOwner()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var manager = new StreamManager();
        manager.Register(1, 1, Mailbox(context));
        for (var i = 0; i < 4096; i++)
            Ensure(!manager.CaptureInputStreamDrain(1, 1).IsEmpty, "warm capture");
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 65536; i++)
            Ensure(!manager.CaptureInputStreamDrain(1, 1).IsEmpty, "capture");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Ensure(allocated == 0, $"single-input capture allocated {allocated} bytes");
        Console.WriteLine($"DYNAMIC_INPUT_CAPTURE allocations={allocated} iterations=65536");
        manager.CompleteAll(null);
    }

    [Test]
    [Arguments(1, false)]
    [Arguments(2, false)]
    [Arguments(1, true)]
    [Arguments(2, true)]
    public async Task DrainShouldCloseAllChildrenAndJoinDisposalWithoutWaitingForPeer(int count, bool disconnect)
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var consumed = 0;
        var manager = new StreamManager(new RuntimeConcurrencyOptions(), null,
            (_, _, bytes) => Interlocked.Add(ref consumed, bytes), null);
        var children = new HeldChild[count];
        var dispatches = new Task[count];
        for (ushort id = 1; id <= count; id++)
        {
            manager.Register(7, id, Mailbox(context));
            children[id - 1] = new HeldChild();
            manager.Register(7, id, children[id - 1]);
            dispatches[id - 1] = manager.DispatchChunkAsync(7, id,
                new ReadOnlySequence<byte>(new byte[] { 1 })).AsTask();
            await children[id - 1].Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var captured = manager.CaptureInputStreamDrain(7, count);
        if (disconnect)
            manager.CompleteAll(new OperationCanceledException("session teardown"));
        var drain = captured.CompleteAsync().AsTask();
        Ensure(!drain.IsCompleted, "physical dispatch is still owned");
        if (!disconnect)
        {
            for (ushort id = 1; id <= count; id++)
                await manager.DispatchChunkAsync(7, id, new ReadOnlySequence<byte>(new byte[] { 2 }));
            Ensure(children.All(c => c.DispatchCount == 1), "all child gates close before waiting");
            Ensure(consumed == count, "late scalar discard returns credit");
        }
        foreach (var child in children)
            child.DispatchRelease.TrySetResult();
        await Task.WhenAll(dispatches).WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var child in children)
            await child.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(!drain.IsCompleted, "physical child disposal is still owned");
        foreach (var child in children)
            child.DisposeRelease.TrySetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(children.All(c => c.DisposeCount == 1), "each typed child disposes once");
        if (!disconnect)
        {
            Ensure(manager.ActiveStreamCount == count, "scalar routes survive local child exit without peer terminal");
            for (ushort id = 1; id <= count; id++)
                manager.CompletePeerStream(7, id, null);
            Ensure(consumed == count * 2, "held and late data each return exact credit");
        }
        Ensure(manager.ActiveStreamCount == 0, "peer terminal or disconnect reclaims every route");
        Console.WriteLine(JsonSerializer.Serialize(new { count, disconnect, consumed, childrenDisposed = count }));
    }

    private static PreAdmissionStreamDispatcher Mailbox(SharpLinkRuntimeContext context)
        => new(context.Buffers, _ => true, _ => { }, () => throw new InvalidOperationException("unexpected capacity"),
            retainUntilLocalCompletion: true);

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class HeldChild : IStreamConsumptionAwareDispatcher, IAsyncDisposable
    {
        public TaskCompletionSource Entered { get; } = Signal();
        public TaskCompletionSource DispatchRelease { get; } = Signal();
        public TaskCompletionSource DisposeEntered { get; } = Signal();
        public TaskCompletionSource DisposeRelease { get; } = Signal();
        public int DispatchCount;
        public int DisposeCount;
        private Action<long, ushort, int>? _consumed;
        private long _request;
        private ushort _stream;
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => DispatchAsync(payload, (int)payload.Length);
        public async ValueTask DispatchAsync(ReadOnlySequence<byte> payload, int bytes)
        {
            Interlocked.Increment(ref DispatchCount);
            Entered.TrySetResult();
            await DispatchRelease.Task;
            _consumed?.Invoke(_request, _stream, bytes);
        }
        public void Complete(bool error, string? message) { }
        public void Complete(Exception? exception) { }
        public void SetBytesConsumedCallback(Action<long, ushort, int>? callback, long request, ushort stream)
            => (_consumed, _request, _stream) = (callback, request, stream);
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCount);
            DisposeEntered.TrySetResult();
            await DisposeRelease.Task;
        }
        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

