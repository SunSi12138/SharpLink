using System.Buffers;
using System.IO.Pipelines;

namespace SharpLink.UnitTests.Runtime;

// Characterization only: these assertions expose why logical completion is not a
// physical user-code quiescence certificate. They do not add a production barrier.
public class ColdQuiescenceServerOwnershipAuditTests
{
    [Test]
    public async Task GeneratedDeadlineCanCompleteWhileMoveNextAndDisposeRemainActive()
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "cold-generated-deadline-ownership", input.Reader, output.Writer,
            RpcSessionTestFixture.ServerOptions());
        var clock = new ManualTimeProvider();
        var stream = new HeldStream();
        try
        {
            Task pump;
            using (SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(
                       session.Id, authentication: null,
                       RpcDeadline.Create(TimeSpan.FromSeconds(1), clock), clock)))
            {
                pump = new RpcSessionGeneratedServerBridge(session).PumpOutboundStreamAsync(
                    91, 0, stream, new IntCodec(), payloadNullable: false,
                    contractId: 101, methodId: 202, CancellationToken.None).AsTask();
            }

            await stream.MoveNextStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(1));
            Exception? failure = null;
            try { await pump.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception exception) { failure = exception; }

            Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
                "the generated pump must select its deadline terminal");
            Ensure(stream.DisposeStarted.Task.IsCompleted,
                "the pump must already have invoked user disposal before returning");
            Ensure(!stream.MoveNextFinished.Task.IsCompleted && !stream.DisposeFinished.Task.IsCompleted,
                "deadline completion must not be mistaken for completion of abandoned user work");
        }
        finally
        {
            stream.Release();
            await stream.MoveNextFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await stream.DisposeFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    [Test]
    public async Task GeneratedNormalCompletionKeepsOwnershipThroughAsyncDispose()
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "cold-generated-normal-ownership", input.Reader, output.Writer,
            RpcSessionTestFixture.ServerOptions());
        var stream = new HeldStream();
        var pump = new RpcSessionGeneratedServerBridge(session).PumpOutboundStreamAsync(
            92, 0, stream, new IntCodec(), payloadNullable: false,
            contractId: 101, methodId: 202, CancellationToken.None).AsTask();
        try
        {
            await stream.MoveNextStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(!pump.IsCompleted, "normal MoveNext must retain pump ownership");
            stream.ReleaseMoveNext.TrySetResult();
            await stream.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(!pump.IsCompleted, "normal async Dispose must retain pump ownership");
            stream.ReleaseDispose.TrySetResult();
            await pump.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(stream.DisposeFinished.Task.IsCompleted, "normal completion includes disposal");
        }
        finally
        {
            stream.Release();
            await pump.WaitAsync(TimeSpan.FromSeconds(5));
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    [Test]
    // Custom internal child only; built-in cooperative inbound disposal has separate positive controls.
    public async Task CustomInboundMailboxCanDetachBeforeAbandonedAsyncDisposalCompletes()
    {
        using var buffers = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions());
        var mailbox = new PreAdmissionStreamDispatcher(
            buffers, static _ => true, static _ => { }, static () => { });
        var child = new HeldDispatcher();
        Ensure(mailbox.TryBeginAttach(child, out _), "child must attach");
        mailbox.FinishAttach(child);
        var state = (IStreamDispatchState)mailbox;
        try
        {
            mailbox.Abandon(out _);
            await state.WaitForDetachedAsync(CancellationToken.None);
            Ensure(state.IsDetached && !state.HasActiveDispatches,
                "mailbox routing/dispatch ownership must be detached");
            Ensure(child.DispatchesDrained && child.DisposeStarted,
                "child ownership notification follows disposal initiation");
            Ensure(!child.DisposalFinished.Task.IsCompleted,
                "detachment and drained notification do not wait for asynchronous disposal");
        }
        finally
        {
            child.ReleaseDispose.TrySetResult();
            await child.DisposalFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class HeldStream : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal readonly TaskCompletionSource MoveNextStarted = NewCompletion();
        internal readonly TaskCompletionSource ReleaseMoveNext = NewCompletion();
        internal readonly TaskCompletionSource MoveNextFinished = NewCompletion();
        internal readonly TaskCompletionSource DisposeStarted = NewCompletion();
        internal readonly TaskCompletionSource ReleaseDispose = NewCompletion();
        internal readonly TaskCompletionSource DisposeFinished = NewCompletion();
        public int Current => 0;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public async ValueTask<bool> MoveNextAsync()
        {
            MoveNextStarted.TrySetResult();
            await ReleaseMoveNext.Task.ConfigureAwait(false);
            MoveNextFinished.TrySetResult();
            return false;
        }
        public async ValueTask DisposeAsync()
        {
            DisposeStarted.TrySetResult();
            await ReleaseDispose.Task.ConfigureAwait(false);
            DisposeFinished.TrySetResult();
        }
        internal void Release()
        {
            ReleaseMoveNext.TrySetResult();
            ReleaseDispose.TrySetResult();
        }
    }

    private sealed class HeldDispatcher : IStreamDispatcher, IStreamDispatchLease, IAsyncDisposable
    {
        internal readonly TaskCompletionSource ReleaseDispose = NewCompletion();
        internal readonly TaskCompletionSource DisposalFinished = NewCompletion();
        internal bool DisposeStarted;
        internal bool DispatchesDrained;
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => ValueTask.CompletedTask;
        public void Complete(bool isError, string? errorMessage) { }
        public void Complete(Exception? exception) { }
        public void BindDispatchState(IStreamDispatchState state) { }
        public ValueTask DispatchAcquiredAsync(ReadOnlySequence<byte> payload, int encodedByteCount)
            => ValueTask.CompletedTask;
        public void OnDispatchesDrained() => DispatchesDrained = true;
        public async ValueTask DisposeAsync()
        {
            DisposeStarted = true;
            await ReleaseDispose.Task.ConfigureAwait(false);
            DisposalFinished.TrySetResult();
        }
    }

    private sealed class IntCodec : IRpcCodec<int>
    {
        public void Serialize(in int value, IBufferWriter<byte> buffer) => throw new NotSupportedException();
        public int Deserialize(in ReadOnlySequence<byte> buffer) => throw new NotSupportedException();
    }

    private static TaskCompletionSource NewCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
