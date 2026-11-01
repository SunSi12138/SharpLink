using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Linq;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

// Actual group, pending owner, connection reservation and Session pump. No copied state model.
public sealed class ClientStreamPublicationGroupTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task ExceptionalOperationObservationReturnsRentedOperationOnceAndPreservesBothFailures()
    {
        await using var fixture = new Fixture();
        var returns = 0;
        var operation = new RpcRequestOperation<int>(_ => Interlocked.Increment(ref returns));
        operation.Initialize(17, fixture.Codec);
        var terminal = new InvalidOperationException("delivered-terminal-marker");
        var cleanup = new InvalidOperationException("post-delivery-cleanup-marker");
        operation.SetError(terminal);
        // Exercise the actual exceptional invocation helper, not a copied observer.
        var method = typeof(SharpLink.Client.SharpLinkClient).GetMethod("ObserveFailedOperationAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(typeof(int));
        var observed = (ValueTask<int>)method.Invoke(null, new object[] { operation, cleanup })!;
        var failure = await Failure(observed.AsTask());
        Ensure(Contains(failure, terminal) && Contains(failure, cleanup),
            "terminal delivery and later cleanup errors both survive exact operation consumption");
        Ensure(returns == 1, "already-rented operation is consumed and returned exactly once");
    }

    [Test]
    public async Task IgnoredProducerTaskRetainsReservationThroughMoveNextAndDispose()
    {
        await using var fixture = new Fixture();
        var group = await fixture.CreateGroup();
        var stream = new HeldStream();
        var writer = new ManualWriter((sink, id, token) =>
        {
            _ = sink.SendClientStreamAsync(id, 1, stream, fixture.Codec, token);
            return ValueTask.CompletedTask;
        });
        var physical = group.RunWriterAsync(writer, fixture.ProducerToken);
        try
        {
            await stream.MoveEntered.Task.WaitAsync(Timeout);
            fixture.Cancel();
            Ensure(await Failure(fixture.Response) is OperationCanceledException, "logical cancellation remains independently observable");
            Ensure(!physical.IsCompleted && fixture.Connection.CallAdmissionReservationCount == 1,
                "ignored physical task keeps original reservation after logical completion");
            stream.Move.TrySetResult(false);
            await stream.DisposeEntered.Task.WaitAsync(Timeout);
            Ensure(!physical.IsCompleted && fixture.Connection.CallAdmissionReservationCount == 1,
                "actual Dispose tail is joined before the fence and refund");
            stream.Dispose.TrySetResult();
            Ensure(await Failure(physical) is not null, "ignored cancelled slot failure is retained");
            Ensure(fixture.Connection.CallAdmissionReservationCount == 0 && fixture.Session.QueuedSendBytes == 0,
                "all actual producer and frame owners settle before original reservation refund");
        }
        finally { stream.Move.TrySetResult(false); stream.Dispose.TrySetResult(); await Failure(physical); }
    }

    [Test]
    public async Task SealWaitsForRegisteredCallBeforeItsPhysicalTaskIsPublished()
    {
        await using var fixture = new Fixture();
        var group = await fixture.CreateGroup();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var stream = new BlockingConstructionStream(entered, release);
        Task? sinkCall = null;
        var writer = new ManualWriter((sink, id, token) =>
        {
            sinkCall = Task.Run(() => sink.SendClientStreamAsync(id, 1, stream, fixture.Codec, token));
            if (!entered.Wait(Timeout)) throw new TimeoutException("actual enumerator construction did not enter");
            return ValueTask.CompletedTask;
        });
        var physical = group.RunWriterAsync(writer, fixture.ProducerToken);
        try
        {
            Ensure(!physical.IsCompleted && fixture.Connection.CallAdmissionReservationCount == 1,
                "sealing waits for registration-to-task-publication race rather than a placeholder Task");
            var rejected = new CountingEmptyStream();
            Ensure(Capture(() => group.SendClientStreamAsync(group.RequestId, 1, rejected, fixture.Codec)) is InvalidOperationException,
                "post-seal calls fail synchronously");
            Ensure(rejected.Entries == 0, "post-seal rejection performs no user enumeration");
            release.Set();
            await physical.WaitAsync(Timeout);
            await sinkCall!.WaitAsync(Timeout);
            Ensure(fixture.Connection.CallAdmissionReservationCount == 0, "last publisher wakes exactly one physical join owner");
        }
        finally { release.Set(); await Failure(physical); if (sinkCall is not null) await Failure(sinkCall); }
    }

    [Test]
    public async Task WriterThrowAndIgnoredSlotFailureAreBothPreserved()
    {
        await using var fixture = new Fixture();
        var group = await fixture.CreateGroup();
        var slotFailure = new InvalidOperationException("slot-marker");
        var writerFailure = new InvalidOperationException("writer-marker");
        var writer = new ManualWriter((sink, id, token) =>
        {
            _ = sink.SendClientStreamAsync(id, 1, new ThrowingStream(slotFailure), fixture.Codec, token);
            throw writerFailure;
        });
        var failure = await Failure(group.RunWriterAsync(writer, fixture.ProducerToken));
        Ensure(Contains(failure, writerFailure) && Contains(failure, slotFailure),
            "writer and ignored physical slot failures retain original identities");
        Ensure(fixture.Connection.CallAdmissionReservationCount == 0, "failure does not skip physical fence/refund");
    }

    [Test]
    public async Task DuplicateStreamRegistrationDoesNotRunSecondUserBodyOrLoseFailure()
    {
        await using var fixture = new Fixture();
        var group = await fixture.CreateGroup();
        var first = new CountingEmptyStream();
        var second = new CountingEmptyStream();
        var writer = new ManualWriter((sink, id, token) =>
        {
            _ = sink.SendClientStreamAsync(id, 1, first, fixture.Codec, token);
            Ensure(Capture(() => sink.SendClientStreamAsync(id, 1, second, fixture.Codec, token)) is InvalidOperationException,
                "duplicate registration fails before executing user code");
            return ValueTask.CompletedTask;
        });
        Ensure(await Failure(group.RunWriterAsync(writer, fixture.ProducerToken)) is InvalidOperationException,
            "writer cannot swallow the bounded invalid-registration diagnostic");
        Ensure(first.Entries == 1 && second.Entries == 0 && fixture.Connection.CallAdmissionReservationCount == 0,
            "one exact declared slot, one physical owner, no duplicate user work");
    }

    [Test]
    public async Task DefinitelyNeverPublishedHeadWithoutProducersNeedsNoFenceOrSessionClose()
    {
        await using var fixture = new Fixture();
        var group = await fixture.CreateGroup(publishHead: false);
        await group.FinishAsync().WaitAsync(Timeout);
        Ensure(fixture.Session.IsConnected && fixture.Connection.CallAdmissionReservationCount == 0 && fixture.Session.QueuedSendBytes == 0,
            "definitely no outbound ownership is refunded locally without inventing a Pending-head fence");
    }

    [Test]
    public async Task WriterFailureCancelsCooperativeProducerBeforePhysicalJoinEvenWhenObserverThrows()
    {
        await using var fixture = new Fixture();
        var observerFailure = new InvalidOperationException("observer-marker");
        var observer = new ThrowingObserver(observerFailure);
        var group = await fixture.CreateGroup(observer: observer);
        var stream = new CooperativeStream();
        var writerFailure = new InvalidOperationException("writer-before-cooperative-join");
        var writer = new ManualWriter((sink, id, token) =>
        {
            _ = sink.SendClientStreamAsync(id, 1, stream, fixture.Codec, token);
            throw writerFailure;
        });
        var physical = group.RunWriterAsync(writer, group.ProducerToken);
        try
        {
            await stream.Cancelled.Task.WaitAsync(Timeout);
            await stream.DisposeEntered.Task.WaitAsync(Timeout);
            Ensure(!physical.IsCompleted && fixture.Connection.CallAdmissionReservationCount == 1,
                "writer failure reaches cancellation before join and preserves held physical Dispose");
            Ensure(Contains(await Failure(fixture.Response), writerFailure),
                "throwing observer cannot skip exact logical result delivery");
            stream.ReleaseDispose.TrySetResult();
            var failure = await Failure(physical);
            Ensure(Contains(failure, writerFailure) && Contains(failure, observerFailure) && observer.Calls == 1,
                "terminal cleanup failure cannot skip physical join or erase writer/observer causes");
            Ensure(fixture.Connection.CallAdmissionReservationCount == 0, "original reservation refunded after actual cleanup");
        }
        finally { stream.ReleaseDispose.TrySetResult(); await Failure(physical); }
    }

    [Test]
    public async Task IgnoredFaultedSlotCancelsCooperativeSiblingWithoutWaitingForWriterWhenAll()
    {
        await using var fixture = new Fixture();
        var group = await fixture.CreateGroup(streamCount: 2);
        var sibling = new CooperativeStream();
        var expected = new InvalidOperationException("ignored-slot-marker");
        var writer = new ManualWriter((sink, id, token) =>
        {
            _ = sink.SendClientStreamAsync(id, 1, sibling, fixture.Codec, token);
            _ = sink.SendClientStreamAsync(id, 2, new ThrowingStream(expected), fixture.Codec, token);
            return ValueTask.CompletedTask;
        });
        var physical = group.RunWriterAsync(writer, group.ProducerToken);
        try
        {
            await sibling.Cancelled.Task.WaitAsync(Timeout);
            await sibling.DisposeEntered.Task.WaitAsync(Timeout);
            Ensure(!physical.IsCompleted, "ignored failed slot cancels sibling but still joins actual Dispose");
            sibling.ReleaseDispose.TrySetResult();
            Ensure(Contains(await Failure(physical), expected), "ignored original slot failure retained");
            Ensure(fixture.Connection.CallAdmissionReservationCount == 0, "joined sibling and fault do not leak reservation");
        }
        finally { sibling.ReleaseDispose.TrySetResult(); await Failure(physical); }
    }

    private sealed class ThrowingObserver(Exception failure) : IPendingCallCompletionObserver
    {
        internal int Calls;
        public void OnResponseObserved() { }
        public void OnPendingCallCompleted(in PendingCallCompletion completion) { Calls++; throw failure; }
    }
    private sealed class CooperativeStream : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private CancellationToken _token;
        internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DisposeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseDispose = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Current => 0;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        { _token = cancellationToken; return this; }
        public async ValueTask<bool> MoveNextAsync()
        {
            try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, _token); return false; }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
        }
        public ValueTask DisposeAsync() { DisposeEntered.TrySetResult(); return new(ReleaseDispose.Task); }
    }

    private readonly struct ManualWriter(Func<IRpcClientStreamSink, long, CancellationToken, ValueTask> action) : IRpcClientStreamWriter
    {
        public ValueTask WriteAsync(IRpcClientStreamSink sink, long requestId, CancellationToken cancellationToken = default)
            => action(sink, requestId, cancellationToken);
    }
    private sealed class HeldStream : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal readonly TaskCompletionSource MoveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Move = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DisposeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Dispose = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Current => 0;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() { MoveEntered.TrySetResult(); return new(Move.Task); }
        public ValueTask DisposeAsync() { DisposeEntered.TrySetResult(); return new(Dispose.Task); }
    }
    private sealed class BlockingConstructionStream(ManualResetEventSlim entered, ManualResetEventSlim release) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        public int Current => 0;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        { entered.Set(); if (!release.Wait(Timeout)) throw new TimeoutException("construction barrier"); return this; }
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class CountingEmptyStream : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal int Entries;
        public int Current => 0;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) { Entries++; return this; }
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class ThrowingStream(Exception failure) : IAsyncEnumerable<int>
    { public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => throw failure; }

    private static Exception? Capture(Action action) { try { action(); return null; } catch (Exception error) { return error; } }
    private static async Task<Exception?> Failure(Task task)
    { try { await task.WaitAsync(Timeout); return null; } catch (Exception error) when (error is not TimeoutException) { return error; } }
    private static bool Contains(Exception? error, Exception target)
        => ReferenceEquals(error, target) || error is AggregateException aggregate && aggregate.InnerExceptions.Any(item => Contains(item, target));
    private static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        private readonly SharpLinkClient _owner;
        private readonly CancellationTokenSource _connectionCancellation = new();
        private readonly CancellationTokenSource _callCancellation = new();
        internal RpcSession Session { get; }
        internal ClientConnection Connection { get; }
        internal IRpcCodec<int> Codec { get; }
        internal Task<int> Response { get; private set; } = Task.FromResult(0);
        internal CancellationToken ProducerToken { get; private set; }
        internal Fixture()
        {
            _owner = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
            var context = (SharpLinkRuntimeContext)_owner.RuntimeContext;
            Session = RpcSessionTestFixture.CreateSessionOverTestTransport("manual-writer-physical-group", _input.Reader, _output.Writer,
                RpcSessionTestFixture.ClientOptions(context));
            Session.NotifyConnected();
            Connection = new ClientConnection(_owner, Session, _connectionCancellation, 8, context);
            Codec = context.Codecs.GetCodec<int>();
        }
        internal async Task<ClientStreamPublicationGroup> CreateGroup(bool publishHead = true, int streamCount = 1, IPendingCallCompletionObserver? observer = null)
        {
            Ensure(Connection.TryReserveExactCallAdmission(), "actual connection reservation required");
            var operation = Connection.PendingCalls.Rent(Codec, PendingCallKind.ClientStreaming, default, _callCancellation.Token,
                out var id, completionObserver: observer, hasResponsePayload: true, responseNullable: false);
            Response = operation.AsValueTask().AsTask();
            var group = new ClientStreamPublicationGroup(Connection, new TestRpcBindingPublication(Session), id, streamCount);
            ProducerToken = group.ProducerToken;
            if (publishHead)
            {
                var writer = Session.RuntimeContext.Buffers.Rent();
                using (writer.BeginPacketScope(ProtocolV2FrameType.Request, ProtocolV2FrameFlags.None, (ulong)id))
                {
                    var span = writer.GetSpan(16);
                    BinaryPrimitives.WriteUInt64LittleEndian(span, group.Binding.Token);
                    BinaryPrimitives.WriteInt64LittleEndian(span[8..], 1);
                    writer.Advance(16);
                }
                var prepared = Session.PrepareOutboundFrame(writer, CancellationToken.None);
                Ensure(Connection.PendingCalls.TryPublishRequest(id, Session, prepared, false, CancellationToken.None,
                    Connection.PendingCalls, out _, out _, group), "actual pending gate publishes exact stream head");
                Connection.PendingCalls.ReceiveRequestAccepted((ulong)id);
                await Session.FlushSendQueueAsync().AsTask().WaitAsync(Timeout);
            }
            else
            {
                // A caller finishing without attempting its head must first select its actual
                // terminal outcome; only that gate can prove no later transfer is permitted.
                Connection.PendingCalls.TryComplete(id, PendingCallCompletionReason.SendFailure,
                    new InvalidOperationException("Fixture deliberately did not publish its head."));
            }
            return group;
        }
        internal void Cancel() => _callCancellation.Cancel();
        public async ValueTask DisposeAsync()
        {
            _callCancellation.Cancel();
            await Failure(Response);
            await Connection.DisposeAsync().AsTask().WaitAsync(Timeout);
            await _input.Writer.CompleteAsync();
            await _output.Reader.CompleteAsync();
            await _owner.DisposeAsync();
            _callCancellation.Dispose();
            _connectionCancellation.Dispose();
        }
    }
}
