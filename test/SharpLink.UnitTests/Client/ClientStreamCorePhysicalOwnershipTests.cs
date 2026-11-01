using System.IO.Pipelines;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

// Actual ClientConnection producer Task + PendingRequestTable + RpcSession controls.
// These use the actual physical group; they do not model generated-writer supervision or module ownership.
public sealed class ClientStreamCorePhysicalOwnershipTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task LogicalCancellationCannotFinishCoreBeforeHeldMoveNextAndDispose()
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        // Host-only PipeSession component scope: no remote Bind success is claimed here.
        await using var group = new DirectClientStreamTestGroup(fixture.Connection,
            new TestRpcBindingPublication(fixture.Session), fixture.Codec, cancellationToken: cancellation.Token);
        var response = group.Response;
        await group.PublishHeadAsync();
        var stream = new HeldStream(holdMoveNext: true);
        var core = group.SendAsync(stream, fixture.Codec);
        try
        {
            await stream.MoveNextEntered.Task.WaitAsync(Timeout);
            cancellation.Cancel();
            Ensure(await Failure(response) is OperationCanceledException,
                "logical pending cancellation must remain independently observable");
            Ensure(!core.IsCompleted && stream.DisposeCalls == 0,
                "the physical core must still own the actual user MoveNext Task after logical cancellation");
            stream.ReleaseMoveNext();
            await stream.DisposeEntered.Task.WaitAsync(Timeout);
            Ensure(!core.IsCompleted && stream.DisposeCalls == 1,
                "MoveNext completion transfers to awaited Dispose, not an early producer completion");
            stream.ReleaseDispose();
            Ensure(await Failure(core) is OperationCanceledException,
                "the core reports cancellation only after actual enumerator disposal finishes");
            await group.FinishAsync().WaitAsync(Timeout);
            Ensure(fixture.Session.QueuedSendBytes == 0 && stream.MoveNextCalls == 1 && stream.DisposeCalls == 1,
                "exact producer and frame owners settle once after both physical barriers");
        }
        finally
        {
            stream.ReleaseMoveNext();
            stream.ReleaseDispose();
            await Failure(core);
        }
    }

    [Test]
    public async Task CancellationDuringCleanEofDisposalDoesNotDetachPhysicalDispose()
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        // Host-only PipeSession component scope: no remote Bind success is claimed here.
        await using var group = new DirectClientStreamTestGroup(fixture.Connection,
            new TestRpcBindingPublication(fixture.Session), fixture.Codec, cancellationToken: cancellation.Token);
        var response = group.Response;
        await group.PublishHeadAsync();
        var stream = new HeldStream(holdMoveNext: false);
        var core = group.SendAsync(stream, fixture.Codec);
        try
        {
            await stream.DisposeEntered.Task.WaitAsync(Timeout);
            cancellation.Cancel();
            Ensure(await Failure(response) is OperationCanceledException && !core.IsCompleted,
                "logical completion cannot certify an already-started, physically held DisposeAsync");
            stream.ReleaseDispose();
            await core.WaitAsync(Timeout);
            // Preserve existing EOF ordering: the clean terminal was published before Dispose;
            // cancellation during successful Dispose does not invent a second terminal or recheck.
            await group.FinishAsync().WaitAsync(Timeout);
            Ensure(stream.DisposeCalls == 1 && fixture.Session.QueuedSendBytes == 0,
                "the caller may fence only after core joins the one actual Dispose operation");
        }
        finally
        {
            stream.ReleaseDispose();
            await Failure(core);
        }
    }

    private static async Task<Exception?> Failure(Task task)
    {
        try { await task.WaitAsync(Timeout); return null; }
        catch (Exception exception) when (exception is not TimeoutException) { return exception; }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class HeldStream(bool holdMoveNext) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private readonly TaskCompletionSource<bool> _move = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _dispose = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource MoveNextEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int MoveNextCalls;
        internal int DisposeCalls;
        public int Current => 0;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync()
        {
            MoveNextCalls++;
            MoveNextEntered.TrySetResult();
            return holdMoveNext ? new ValueTask<bool>(_move.Task) : ValueTask.FromResult(false);
        }
        public async ValueTask DisposeAsync()
        {
            DisposeCalls++;
            DisposeEntered.TrySetResult();
            await _dispose.Task.ConfigureAwait(false);
        }
        internal void ReleaseMoveNext() => _move.TrySetResult(false);
        internal void ReleaseDispose() => _dispose.TrySetResult();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        private readonly SharpLinkClient _owner;
        private readonly CancellationTokenSource _connectionCancellation = new();
        internal RpcSession Session { get; }
        internal ClientConnection Connection { get; }
        internal IRpcCodec<int> Codec { get; }
        internal Fixture()
        {
            _owner = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
            var context = (SharpLinkRuntimeContext)_owner.RuntimeContext;
            Session = RpcSessionTestFixture.CreateSessionOverTestTransport(
                "client-core-physical-tail", _input.Reader, _output.Writer,
                RpcSessionTestFixture.ClientOptions(context));
            Session.NotifyConnected();
            Connection = new ClientConnection(_owner, Session, _connectionCancellation, 8, context);
            Codec = context.Codecs.GetCodec<int>();

        }
        public async ValueTask DisposeAsync()
        {
            Exception? failure = null;
            // The actual group has already joined its core/fence and released its selection
            // reservation and pending producer lease before the component Session is torn down.
            try { await Connection.DisposeAsync().AsTask().WaitAsync(Timeout); }
            catch (Exception cleanup) { failure = cleanup; }
            try { await _input.Writer.CompleteAsync(); }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
            try { await _output.Reader.CompleteAsync(); }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
            try { await _owner.DisposeAsync(); }
            catch (Exception cleanup) { failure = RpcPublicationErrors.Combine(failure, cleanup); }
            _connectionCancellation.Dispose();
            if (failure is not null) throw failure;
        }
    }
}
