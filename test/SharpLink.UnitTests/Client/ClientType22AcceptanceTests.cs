using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

// Source controls for actual PendingRequestTable, group, connection reservation and Session pump.
// This is not the separate generated TCP integration oracle, and has not been executed here.
public sealed partial class ClientType22AcceptanceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task Type22RejectsZeroFutureWrongKindAndDuplicateButIgnoresRetiredFullId()
    {
        using var table = PendingRequestTableTestFixture.Create(1);
        Ensure(IsProtocol(Capture(() => table.ReceiveRequestAccepted(0))), "zero ID is invalid");
        Ensure(IsProtocol(Capture(() => table.ReceiveRequestAccepted(1))), "unallocated future ID is invalid");
        var codec = PendingRequestTableTestFixture.Codecs.GetCodec<int>();
        var first = table.Rent(codec, PendingCallKind.Unary, default, default, out var oldId);
        Ensure(IsProtocol(Capture(() => table.ReceiveRequestAccepted((ulong)oldId))), "live Unary has no acceptance ACK");
        table.TryComplete(oldId, PendingCallCompletionReason.ConnectionClosed);
        await Failure(first.AsValueTask().AsTask());
        var next = table.Rent(codec, PendingCallKind.ClientStreaming, default, default, out var id);
        table.ReceiveRequestAccepted((ulong)oldId); // Same ring slot, different exact full ID.
        Ensure(table.Contains(id), "retired ACK cannot remove or accept a replacement");
        table.ReceiveRequestAccepted((ulong)id);
        Ensure(IsProtocol(Capture(() => table.ReceiveRequestAccepted((ulong)id))), "duplicate live ACK is invalid");
        table.TryComplete(id, PendingCallCompletionReason.ConnectionClosed);
        await Failure(next.AsValueTask().AsTask());
        table.ReceiveRequestAccepted((ulong)id); // No tombstone and no allocation/start.
        Ensure(table.ActiveCount == 0, "logical entries without physical groups refund normally");
    }

    [Test]
    public async Task EarlyAckCannotClaimStartBeforeExactHeadAndLaterStartIsOnce()
    {
        await using var f = new Fixture();
        var group = f.CreateGroup();
        f.Connection.PendingCalls.ReceiveRequestAccepted((ulong)group.RequestId);
        Ensure(!await f.Connection.PendingCalls.WaitForProducerStartAsync(group.RequestId, group), "ACK alone cannot start before head");
        await f.PublishHead(group);
        Ensure(await f.Connection.PendingCalls.WaitForProducerStartAsync(group.RequestId, group), "same exact call starts after head and ACK");
        Ensure(!await f.Connection.PendingCalls.WaitForProducerStartAsync(group.RequestId, group), "start is claimed once");
        f.Cancel();
        await group.FinishAsync().WaitAsync(Timeout);
        Ensure(f.Session.IsConnected && f.Connection.PendingCalls.ActiveCount == 0, "bounded unstarted cleanup retains healthy Session");
    }

    [Test]
    public async Task AwaitingAckDoesNotEnterWriterAndRetainsOriginalCapacity()
    {
        await using var f = new Fixture();
        var group = f.CreateGroup();
        await f.PublishHead(group);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new Writer((sink, id, token) =>
        {
            entered.TrySetResult();
            return new ValueTask(sink.SendClientStreamAsync(id, 1, new EmptyStream(), f.Codec, token));
        });
        var physical = group.RunWriterAsync(writer, group.ProducerToken);
        Ensure(!entered.Task.IsCompleted && !physical.IsCompleted && f.Connection.PendingCalls.ActiveCount == 1,
            "awaiting ACK retains original pending owner without executing user code");
        f.Connection.PendingCalls.ReceiveRequestAccepted((ulong)group.RequestId);
        await entered.Task.WaitAsync(Timeout);
        await physical.WaitAsync(Timeout);
        f.Cancel();
        await Failure(f.Response);
        Ensure(f.Connection.CallAdmissionReservationCount == 0 && f.Connection.PendingCalls.ActiveCount == 0,
            "actual EOF/final fence and logical result settle once");
    }

    [Test]
    public async Task CancellationAfterAckBeforeStartInvokesNoWriterAndPublishesErrorTerminals()
    {
        await using var f = new Fixture();
        var group = f.CreateGroup(streamCount: 2);
        await f.PublishHead(group);
        f.Connection.PendingCalls.ReceiveRequestAccepted((ulong)group.RequestId);
        f.Cancel();
        Ensure(await Failure(f.Response) is OperationCanceledException, "logical cancellation remains prompt");
        var entries = 0;
        await group.RunWriterAsync(new Writer((_, _, _) => { entries++; return ValueTask.CompletedTask; }), group.ProducerToken).WaitAsync(Timeout);
        var ids = await f.ReadTerminalIds(2);
        Ensure(entries == 0 && ids.SetEquals(new ushort[] { 1, 2 }), "every declared unstarted route receives real Error terminal");
        Ensure(f.Session.IsConnected && f.Connection.PendingCalls.ActiveCount == 0, "ordinary bounded cleanup does not default-close Session");
    }

    [Test]
    public async Task OriginalDeadlineWinsAckBeforeSchedulerCallbackAndPreventsWriterEntry()
    {
        var clock = new ManualTimeProvider();
        await using var f = new Fixture(clock);
        var group = f.CreateGroup(deadline: RpcDeadline.Create(TimeSpan.FromSeconds(1), clock));
        await f.PublishHead(group);
        clock.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(1));
        f.Connection.PendingCalls.ReceiveRequestAccepted((ulong)group.RequestId);
        Ensure(await Failure(f.Response) is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded }, "monotonic fact beats late timer callback");
        var entries = 0;
        await group.RunWriterAsync(new Writer((_, _, _) => { entries++; return ValueTask.CompletedTask; }), group.ProducerToken).WaitAsync(Timeout);
        Ensure(entries == 0 && f.Connection.PendingCalls.ActiveCount == 0, "expired accepted call never starts producer");
    }

    [Test]
    public async Task StreamedOneWayStartsAfterLocalHeadWithoutAckAndRejectsAnAck()
    {
        await using var f = new Fixture();
        var group = f.CreateGroup(oneWay: true);
        await f.PublishHead(group, oneWay: true);
        Ensure(IsProtocol(Capture(() => f.Connection.PendingCalls.ReceiveRequestAccepted((ulong)group.RequestId))), "type23 has no per-call ACK");
        var entered = 0;
        await group.RunWriterAsync(new Writer((sink, id, token) =>
        {
            entered++;
            return new ValueTask(sink.SendClientStreamAsync(id, 1, new EmptyStream(), f.Codec, token));
        }), group.ProducerToken).WaitAsync(Timeout);
        f.Connection.PendingCalls.TryComplete(group.RequestId, PendingCallCompletionReason.LocalStreamComplete);
        await f.Response.WaitAsync(Timeout);
        Ensure(entered == 1 && f.Connection.PendingCalls.ActiveCount == 0, "local type23 production completes without remote success claim");
    }

    [Test]
    public async Task OmittedStreamCleansPeerBeforeHeldMoveNextAndOrderedDisposeButKeepsOriginalOwners()
    {
        await using var f = new Fixture();
        var group = f.CreateGroup(streamCount: 2);
        await f.PublishHead(group);
        f.Connection.PendingCalls.ReceiveRequestAccepted((ulong)group.RequestId);
        var held = new HeldStream();
        var physical = group.RunWriterAsync(new Writer((sink, id, token) =>
        {
            _ = sink.SendClientStreamAsync(id, 1, held, f.Codec, token);
            return ValueTask.CompletedTask; // Deliberately omit declared stream2.
        }), group.ProducerToken);
        try
        {
            await held.MoveEntered.Task.WaitAsync(Timeout);
            Ensure(await Failure(f.Response) is InvalidOperationException, "omission is a writer failure, never clean EOF");
            var ids = await f.ReadTerminalIds(2);
            Ensure(ids.SetEquals(new ushort[] { 1, 2 }) && !physical.IsCompleted &&
                f.Connection.PendingCalls.ActiveCount == 1 && f.Connection.CallAdmissionReservationCount == 1,
                "real cleanup bytes precede held physical tail without refunding original owners");
            Ensure(!held.DisposeEntered.Task.IsCompleted, "Dispose cannot overlap a held MoveNext");
            held.Move.TrySetResult(false);
            await held.DisposeEntered.Task.WaitAsync(Timeout);
            Ensure(!physical.IsCompleted && f.Connection.PendingCalls.ActiveCount == 1, "ordered Dispose retains original CTS/capacity");
            held.Dispose.TrySetResult();
            Ensure(await Failure(physical) is not null, "writer and actual canceled producer failure remain observed");
            Ensure(f.Connection.PendingCalls.ActiveCount == 0 && f.Connection.CallAdmissionReservationCount == 0,
                "both branches and final fence join before last refund");
        }
        finally { held.Move.TrySetResult(false); held.Dispose.TrySetResult(); await Failure(physical); }
    }

    [Test]
    public async Task FinalFenceCannotOvertakeHeldCleanupFlushEvenWithNoProducerTasks()
    {
        await using var f = new Fixture();
        var group = f.CreateGroup();
        await f.PublishHead(group);
        f.Connection.PendingCalls.ReceiveRequestAccepted((ulong)group.RequestId);
        f.Output.Arm();
        f.Connection.PendingCalls.DispatchError(group.RequestId, new SharpLinkException(SharpLinkErrorCode.Internal, "peer terminal before producer start"));
        var physical = group.FinishAsync();
        try
        {
            await f.Output.Entered.Task.WaitAsync(Timeout);
            Ensure(!physical.IsCompleted && f.Connection.PendingCalls.ActiveCount == 1 &&
                f.Connection.CallAdmissionReservationCount == 1, "held cleanup output cannot refund original owners");
            Ensure(Capture(() => group.ValidateProducer(f.Session, group.RequestId)) is null,
                "early cleanup fence must not call final BeginFence while cleanup branch is held");
            Ensure(group.ProducerToken.WaitHandle is not null, "original producer CTS remains usable through physical cleanup");
            f.Output.Release();
            await physical.WaitAsync(Timeout);
            Ensure(Capture(() => group.ValidateProducer(f.Session, group.RequestId)) is InvalidOperationException &&
                f.Connection.PendingCalls.ActiveCount == 0, "final fence follows cleanup join before final pending refund");
        }
        finally { f.Output.Release(); await Failure(physical); }
    }

    [Test]
    public async Task WriterWithNoSinkCallsFailsAndCleansEveryDeclaredRoute()
    {
        await using var f = new Fixture();
        var group = f.CreateGroup(streamCount: 2);
        await f.PublishHead(group);
        f.Connection.PendingCalls.ReceiveRequestAccepted((ulong)group.RequestId);
        var entries = 0;
        var failure = await Failure(group.RunWriterAsync(new Writer((_, _, _) =>
        { entries++; return ValueTask.CompletedTask; }), group.ProducerToken));
        Ensure(failure is InvalidOperationException && await Failure(f.Response) is InvalidOperationException,
            "zero sink calls is actual writer failure, never fabricated EOF or success");
        Ensure(entries == 1 && (await f.ReadTerminalIds(2)).SetEquals(new ushort[] { 1, 2 }) && f.Session.IsConnected,
            "bounded Error terminal publication preserves healthy Session");
    }

    [Test]
    public async Task ExactHeadIsAcceptedBeforeHeldDeferredWakeAndWakeFailureCannotRefundIt()
    {
        var f = new Fixture();
        var expected = new InvalidOperationException("deferred-wake-after-accepted-head");
        Exception? failure = null;
        void Keep(Exception error) => failure = failure is null ? error : new AggregateException(failure, error);
        try
        {
            var group = f.CreateGroup();
            f.Output.Arm();
            var ping = f.HoldPing();
            await f.Output.Entered.Task.WaitAsync(Timeout); // Actual consumer holds physical output; wake arm is idle.
            var pump = typeof(RpcSession).GetField("_pump", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(f.Session)!;
            var wake = (WakeupSignal)pump.GetType().GetField("_wakeup", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(pump)!;
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var first = 0;
            wake.BeforeLatchWrite = () =>
            {
                if (Interlocked.Exchange(ref first, 1) != 0) return;
                entered.Set();
                if (!release.Wait(Timeout)) throw new TimeoutException("deferred wake barrier");
                throw expected;
            };
            var publication = Task.Run(() => f.TransferHead(group));
            Task? physical = null;
            try
            {
                Ensure(entered.Wait(Timeout), "Request queue transfer reached its deferred wake");
                Ensure(group.HeadPublicationConfirmed && !publication.IsCompleted,
                    "same CompletionGate already recorded Accepted while deferred callback still owns the publication driver");
                f.Connection.PendingCalls.ReceiveRequestAccepted((ulong)group.RequestId);
                f.Cancel();
                Ensure(await Failure(f.Response) is OperationCanceledException && f.Connection.PendingCalls.ActiveCount == 1,
                    "terminal observes accepted head and retains original physical owner");
                release.Set();
                Ensure(await publication.WaitAsync(Timeout) && group.HeadPublicationConfirmed,
                    "deferred wake failure cannot downgrade actual queue transfer to neveraccepted");
                physical = group.FinishAsync();
                Ensure(!physical.IsCompleted && f.Connection.PendingCalls.ActiveCount == 1,
                    "accepted frame and callback failure remain owned behind actual held output");
                f.Output.Release();
                await Failure(ping);
                Ensure(await Failure(physical) is not null && f.Connection.PendingCalls.ActiveCount == 0,
                    "real drain completes before final refund and preserves publication failure");
            }
            catch (Exception error) { failure = error; }
            finally
            {
                // Teardown must not replace the original body assertion with the injected
                // Session disposal fault, nor skip the other already-owned resources.
                try { release.Set(); } catch (Exception error) { Keep(error); }
                wake.BeforeLatchWrite = null;
                try { f.Output.Release(); } catch (Exception error) { Keep(error); }
                try { await Failure(publication); } catch (Exception error) { Keep(error); }
                try { await Failure(ping); } catch (Exception error) { Keep(error); }
                try { f.Cancel(); } catch (Exception error) { Keep(error); }
                try { physical ??= group.FinishAsync(); } catch (Exception error) { Keep(error); }
                if (physical is not null)
                    try { await Failure(physical); } catch (Exception error) { Keep(error); }
            }
        }
        catch (Exception error) { Keep(error); }
        try { await f.DisposeAfterExpectedWakeFailureAsync(expected); }
        catch (Exception error) { Keep(error); }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private readonly struct Writer(Func<IRpcClientStreamSink, long, CancellationToken, ValueTask> action) : IRpcClientStreamWriter
    { public ValueTask WriteAsync(IRpcClientStreamSink sink, long requestId, CancellationToken cancellationToken = default) => action(sink, requestId, cancellationToken); }
    private sealed class EmptyStream : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        public int Current => 0;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
    private static Exception? Capture(Action action) { try { action(); return null; } catch (Exception error) { return error; } }
    private static bool IsProtocol(Exception? error) => error is SharpLinkException { Code: SharpLinkErrorCode.ProtocolViolation };
    private static async Task<Exception?> Failure(Task task)
    { try { await task.WaitAsync(Timeout); return null; } catch (Exception error) when (error is not TimeoutException) { return error; } }
    private static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        private readonly SharpLinkClient _client;
        private readonly CancellationTokenSource _cancel = new();
        internal readonly RpcSession Session;
        internal readonly HeldOutput Output;
        internal readonly ClientConnection Connection;
        internal readonly IRpcCodec<int> Codec;
        internal Task Response = Task.CompletedTask;
        internal Fixture(TimeProvider? clock = null)
        {
            _client = ClientBuilderTestHelper.Build(new TestClientTransportFactory(), b => { if (clock is not null) b.UseTimeProvider(clock); });
            var context = (SharpLinkRuntimeContext)_client.RuntimeContext;
            Output = new HeldOutput(_output.Writer);
            Session = RpcSessionTestFixture.CreateSessionOverTestTransport("type22-client-control", _input.Reader, Output,
                RpcSessionTestFixture.ClientOptions(context));
            Session.NotifyConnected();
            Connection = new ClientConnection(_client, Session, new CancellationTokenSource(), 8, context);
            Codec = context.Codecs.GetCodec<int>();
        }
        internal ClientStreamPublicationGroup CreateGroup(int streamCount = 1, bool oneWay = false, RpcDeadline deadline = default)
        {
            Ensure(Connection.TryReserveExactCallAdmission(), "exact connection reservation");
            long id;
            if (oneWay)
            {
                var pending = Connection.PendingCalls.RegisterOneWayClientStream(deadline, _cancel.Token);
                id = pending.Id; Response = pending.Operation.AsValueTask().AsTask();
            }
            else Response = Connection.PendingCalls.Rent(Codec, PendingCallKind.ClientStreaming, deadline, _cancel.Token, out id).AsValueTask().AsTask();
            return new ClientStreamPublicationGroup(Connection, new TestRpcBindingPublication(Session), id, streamCount);
        }
        internal async Task PublishHead(ClientStreamPublicationGroup group, bool oneWay = false)
        {
            Ensure(TransferHead(group, oneWay), "exact gated head transfer");
            await Session.FlushSendQueueAsync().AsTask().WaitAsync(Timeout);
        }
        internal bool TransferHead(ClientStreamPublicationGroup group, bool oneWay = false)
        {
            var writer = Session.RentFrameWriter();
            using (writer.BeginPacketScope(oneWay ? ProtocolV2FrameType.OneWayStreamRequest : ProtocolV2FrameType.Request,
                oneWay ? ProtocolV2FrameFlags.OneWay : ProtocolV2FrameFlags.Cancellable, (ulong)group.RequestId))
            {
                var count = oneWay ? 18 : 16;
                var span = writer.GetSpan(count);
                BinaryPrimitives.WriteUInt64LittleEndian(span, group.Binding.Token);
                BinaryPrimitives.WriteInt64LittleEndian(span[8..], 1);
                if (oneWay) BinaryPrimitives.WriteUInt16LittleEndian(span[16..], checked((ushort)group.StreamCount));
                writer.Advance(count);
            }
            var prepared = Session.PrepareOutboundFrame(writer, group.ProducerToken);
            return Connection.PendingCalls.TryPublishRequest(group.RequestId, Session, prepared, false, group.ProducerToken,
                Connection.PendingCalls, out _, out _, group);
        }
        internal Task HoldPing()
        {
            var writer = Session.RentFrameWriter();
            using (writer.BeginPacketScope(ProtocolV2FrameType.Ping, ProtocolV2FrameFlags.None, 0))
            {
                writer.GetSpan(sizeof(long))[..sizeof(long)].Clear();
                writer.Advance(sizeof(long));
            }
            return Session.SendPacketAndFlushAsync(writer).AsTask();
        }
        internal void Cancel() => _cancel.Cancel();
        internal async Task<HashSet<ushort>> ReadTerminalIds(int count)
        {
            var ids = new HashSet<ushort>();
            while (ids.Count < count)
            {
                var read = await _output.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
                var remaining = read.Buffer;
                while (ProtocolV2FrameParser.TryReadFrame(ref remaining, Session.RuntimeContext.Protocol, out var header, out var payload))
                {
                    if (header.Type != ProtocolV2FrameType.StreamComplete) continue;
                    Ensure((header.Flags & ProtocolV2FrameFlags.Error) != 0, "host cleanup cannot fabricate clean completion");
                    var id = BinaryPrimitives.ReadUInt16LittleEndian(payload.ToArray());
                    Ensure(ids.Add(id), "single consumer admits one terminal per exact stream");
                }
                _output.Reader.AdvanceTo(remaining.Start, remaining.End);
            }
            return ids;
        }
        // Used by the single deliberate deferred-wake fault case only. Normal fixture
        // disposal below retains its original behavior for every other test.
        internal async Task DisposeAfterExpectedWakeFailureAsync(Exception expected)
        {
            Exception? failure = null;
            void Keep(Exception error) => failure = failure is null ? error : new AggregateException(failure, error);
            try { Output.Release(); } catch (Exception error) { Keep(error); }
            try { _cancel.Cancel(); } catch (Exception error) { Keep(error); }
            try { await Failure(Response); } catch (Exception error) { Keep(error); }
            Exception? connectionFailure = null;
            try { await Connection.DisposeAsync().AsTask().WaitAsync(Timeout); }
            catch (Exception error) { connectionFailure = error; }
            // Not message/type matching and not an Aggregate/InnerException search:
            // only the exact injected instance is the intended physical-stop outcome.
            if (!ReferenceEquals(connectionFailure, expected))
                Keep(new InvalidOperationException(
                    $"Expected exact deferred-wake Session disposal failure; actual={connectionFailure?.GetType().FullName ?? "<null>"}; same={ReferenceEquals(connectionFailure, expected)}",
                    connectionFailure));
            try { await _client.DisposeAsync(); } catch (Exception error) { Keep(error); }
            try { await _input.Writer.CompleteAsync(); } catch (Exception error) { Keep(error); }
            try { await _output.Reader.CompleteAsync(); } catch (Exception error) { Keep(error); }
            try { _cancel.Dispose(); } catch (Exception error) { Keep(error); }
            if (failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        public async ValueTask DisposeAsync()
        {
            Output.Release();
            _cancel.Cancel();
            await Failure(Response);
            await Connection.DisposeAsync().AsTask().WaitAsync(Timeout);
            await _client.DisposeAsync();
            await _input.Writer.CompleteAsync();
            await _output.Reader.CompleteAsync();
            _cancel.Dispose();
        }
    }
    private sealed class HeldOutput(PipeWriter inner) : PipeWriter
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Arm() => Volatile.Write(ref _armed, 1);
        internal void Release() => _release.TrySetResult();
        public override void Advance(int bytes) => inner.Advance(bytes);
        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
        public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) != 0)
            {
                Entered.TrySetResult();
                await _release.Task.ConfigureAwait(false); // Physical barrier deliberately ignores logical cancellation.
            }
            return await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

}
