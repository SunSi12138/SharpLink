using System.Buffers;
using System.Buffers.Binary;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;

namespace SharpLink.BindingTests;

internal static partial class Program
{
    private static async Task RunStreamsAsync(string transport, CancellationToken token)
    {
        await StreamWireControlsAsync(token);
        await AcceptedShapesAsync(transport, token);
        await BeforeAcceptanceAsync(transport, token);
        await AckStartRaceAsync(transport, token);
        await ProducerPhysicalDrainAsync(transport, token);
        await ConsumerPhysicalDrainAsync(transport, token);
        await RetiredStreamGenerationAsync(transport, false, token);
        await RetiredStreamGenerationAsync(transport, true, token);
    }

    private static async Task StreamWireControlsAsync(CancellationToken token)
    {
        static void Parse(byte flags, ulong id, int payloadBytes)
        {
            var bytes = new byte[ProtocolV2Constants.HeaderBytes + payloadBytes];
            bytes[0] = ProtocolV2Constants.Magic; BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), payloadBytes);
            bytes[5] = (byte)StaticBindingWire.RequestAccepted; bytes[6] = flags; BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(7), id);
            var input = new ReadOnlySequence<byte>(bytes);
            if (!ProtocolV2FrameParser.TryReadFrame(ref input, new SharpLinkProtocolOptions(), out _, out _)) throw new InvalidOperationException("Incomplete Ack.");
        }
        Parse(0, 1, 0);
        Check(true, "stream Ack is a fixed empty per-call frame");
        await Expect<SharpLinkProtocolViolationException>(() => { Parse(0, 0, 0); return Task.CompletedTask; }, "stream Ack forbids connection-control correlation zero");
        await Expect<SharpLinkProtocolViolationException>(() => { Parse((byte)ProtocolV2FrameFlags.Error, 1, 0); return Task.CompletedTask; }, "stream Ack forbids flags");
        await Expect<SharpLinkProtocolViolationException>(() => { Parse(0, 1, 1); return Task.CompletedTask; }, "stream Ack forbids payload bytes");
        await using var f = new StreamFixture("tcp"); // Wire/table controls do not depend on the selected transport cell.
        var owner = new StreamPendingOwner();
        using var pending = new PendingRequestTable(2, f.Client.RuntimeContext.Codecs, owner, f.Clock);
        await Expect<SharpLinkProtocolViolationException>(() => { pending.AcceptStreamRequest(long.MaxValue); return Task.CompletedTask; }, "stream sparse future Ack allocates no pending slot");
        Check(!pending.SlotsMaterialized && pending.Count == 0, "stream rejected sparse Ack retains lazy bounded table");
        var operation = pending.Rent(f.Client.RuntimeContext.Codecs.GetCodec<int>(), PendingCallKind.ClientStreaming, default, token, out var id);
        var admission = pending.CaptureStreamAdmission(id, true);
        await Expect<SharpLinkProtocolViolationException>(() => { pending.AcceptStreamRequest(id); return Task.CompletedTask; }, "stream unpublished request cannot accept Ack");
        await Expect<SharpLinkProtocolViolationException>(() => { var payload = ReadOnlySequence<byte>.Empty; pending.Dispatch(id, ref payload); return Task.CompletedTask; }, "stream successful response cannot precede Ack");
        pending.TryComplete(id, PendingCallCompletionReason.UserCancellation);
        await Expect<OperationCanceledException>(() => operation.AsValueTask().AsTask(), "stream table cancellation selects original terminal");
        Check(!await admission.Acceptance && !pending.AcceptStreamRequest(id) && !pending.TryStartStreamProducer(id, admission), "stream late Ack cannot revive canceled pending generation");
        Check(admission.Token.IsCancellationRequested && pending.PhysicalStreamProducers == 1, "stream logical terminal retains physical producer CTS");
        admission.ReleaseProducer();
        Check(pending.PhysicalStreamProducers == 0 && owner.Idle != 0, "stream physical release returns bounded producer quota");
    }
    private sealed class StreamPendingOwner : IPendingCallOwner
    {
        internal int Idle;
        public void OnPendingCallRegistered() { }
        public void OnPendingCallCompleted(in PendingCallCompletion completion) { }
        public void OnProducerCancellationCallbackFailed(Exception error) => throw error;
        public void OnPendingCallCapacityIdle() => Idle++;
    }

    private static async Task AcceptedShapesAsync(string transport, CancellationToken token)
    {
        await using var f = new StreamFixture(transport); await f.ConnectAsync(token);
        var producer = new ProbeProducer(); var reached = Signal(); var release = Signal(); var id = 0L;
        f.Server.BeforeStreamAcceptanceTestHook = async (requestId, _, ct) => { id = requestId; reached.TrySetResult(); await release.Task.WaitAsync(ct); };
        try
        {
            var call = f.Rpc.Service.ConsumeAsync(producer, token).AsTask(); await reached.Task.WaitAsync(token);
            Check(producer.Starts == 0 && !call.IsCompleted, "stream held server admission starts zero client producer");
            Check(f.Peer.Session.StreamManager.ActiveStreamCount == 1, "stream input route exists before acceptance publication");
            release.TrySetResult();
            Check(await call.WaitAsync(token) == 3 && producer.Starts == 1, "stream accepted generated ClientStreaming completes on real transport");
            var observed = Signal(); f.Connection.PendingCalls.StreamAcceptanceObservedTestHook = seen => { if (seen == id) observed.TrySetResult(); };
            await f.Peer.Session.SendPacketAndFlushAsync(StaticBindingWire.CreateRequestAccepted(f.Peer.Session, id), token);
            await observed.Task.WaitAsync(token);
            Check(producer.Starts == 1 && f.Connection.PendingCalls.Count == 0, "stream duplicate late real Ack cannot restart producer");
        }
        finally { release.TrySetResult(); producer.Release(); f.Server.BeforeStreamAcceptanceTestHook = null; }
        var output = await ReadAllAsync(f.Rpc.Service.ProduceAsync(token), token);
        Check(output.SequenceEqual(new[] { 3, 4 }), "stream accepted generated ServerStreaming completes on real transport");
        var duplexProducer = new ProbeProducer();
        var duplex = await ReadAllAsync(f.Rpc.Service.DuplexAsync(duplexProducer, token), token);
        Check(duplex.SequenceEqual(new[] { 2, 4 }) && duplexProducer.Starts == 1, "stream accepted generated Duplex completes on real transport");
        Check(f.Stub.Invocations.All(m => f.Contract.Methods.Any(known => known.Facts == m)) && f.Stub.Resolutions == f.Contract.Methods.Count,
            "stream all three generated terminals retain full bound facts without requery");
        var oneWayProducer = new ProbeProducer(); var callsBeforeOneWay = f.Service.Calls;
        await f.Rpc.Service.FireStreamAsync(oneWayProducer, token);
        Check(true, "stream generated typed OneWay input completes without RequestAccepted");
        await UntilAsync(() => f.Service.Calls == callsBeforeOneWay + 2, token);
        Check(oneWayProducer.Starts == 1, "stream enabled OneWay input starts exactly one producer");
        await f.QuiesceAsync(token);
        Check(f.Connection.ActiveCallCount == 0 && f.Peer.Session.StreamManager.ActiveStreamCount == 0, "stream successful calls reclaim pending physical and route quotas");
    }

    private static async Task BeforeAcceptanceAsync(string transport, CancellationToken token)
    {
        await using var f = new StreamFixture(transport); await f.ConnectAsync(token);
        var producer = new ProbeProducer(); var reached = Signal(); var release = Signal(); var id = 0L;
        f.Server.BeforeStreamAcceptanceTestHook = async (requestId, _, _) => { id = requestId; reached.TrySetResult(); await release.Task; };
        using var caller = new CancellationTokenSource();
        try
        {
            var call = f.Rpc.Service.ConsumeAsync(producer, caller.Token).AsTask(); await reached.Task.WaitAsync(token);
            caller.Cancel(); await Expect<OperationCanceledException>(() => call, "stream caller cancellation before Ack completes logically");
            Check(producer.Starts == 0, "stream caller cancellation before Ack starts zero producer");
            release.TrySetResult(); await f.QuiesceAsync(token);
            var late = Signal(); f.Connection.PendingCalls.StreamAcceptanceObservedTestHook = seen => { if (seen == id) late.TrySetResult(); };
            await f.Peer.Session.SendPacketAndFlushAsync(StaticBindingWire.CreateRequestAccepted(f.Peer.Session, id), token); await late.Task.WaitAsync(token);
            Check(producer.Starts == 0 && f.Connection.Session.IsConnected, "stream canceled call ignores late Ack without closing unrelated route");
        }
        finally { release.TrySetResult(); f.Server.BeforeStreamAcceptanceTestHook = null; }
        // Admission queue ownership must precede per-call acceptance.
        var held = f.Rpc.Service.HoldAsync(token).AsTask(); await f.Service.Entered.Task.WaitAsync(token);
        using var queuedCaller = new CancellationTokenSource(); var queuedProducer = new ProbeProducer();
        var queued = f.Rpc.Service.ConsumeAsync(queuedProducer, queuedCaller.Token).AsTask(); await f.Queued.Task.WaitAsync(token);
        Check(queuedProducer.Starts == 0 && !queued.IsCompleted, "stream queued admission starts zero producer");
        queuedCaller.Cancel(); await Expect<OperationCanceledException>(() => queued, "stream queued cancellation retains original terminal");
        f.Service.Release.TrySetResult(); await held.WaitAsync(token); await f.QuiesceAsync(token);
        Check(queuedProducer.Starts == 0, "stream canceled queued call cannot reach producer after permit release");
        f.Server.ReplaceInterceptors([new RejectStreamInterceptor()]); var rejected = new ProbeProducer();
        await ExpectCode(() => f.Rpc.Service.ConsumeAsync(rejected, token).AsTask(), SharpLinkErrorCode.PermissionDenied, "stream interceptor rejection occurs before acceptance terminal");
        Check(rejected.Starts == 0, "stream rejected interceptor starts zero producer");
        f.Server.ReplaceInterceptors([]); f.RejectActivation = true; var activation = new ProbeProducer();
        await ExpectCode(() => f.Rpc.Service.ConsumeAsync(activation, token).AsTask(), SharpLinkErrorCode.Internal, "stream activation failure occurs before acceptance terminal");
        Check(activation.Starts == 0, "stream rejected activation starts zero producer");
        f.RejectActivation = false;
        var deadlineReached = Signal(); var deadlineRelease = Signal(); var deadlineProducer = new ProbeProducer();
        f.Server.BeforeStreamAcceptanceTestHook = async (_, _, _) => { deadlineReached.TrySetResult(); await deadlineRelease.Task; };
        try
        {
            var writer = new EmptyStreamWriter(deadlineProducer);
            var m = f.Method(nameof(IFactsRpc.ConsumeAsync)) with { HasMethodTimeout = true, MethodTimeout = TimeSpan.FromSeconds(5) };
            var deadline = f.Site.InvokeClientStreamingAsync(m, default(RpcEmptyRequest), RpcEmptyRequestCodec.Instance,
                f.Client.RuntimeContext.Codecs.GetCodec<int>(), writer, null, token).AsTask();
            await deadlineReached.Task.WaitAsync(token); f.Clock.Advance(TimeSpan.FromSeconds(5));
            await ExpectCode(() => deadline, SharpLinkErrorCode.DeadlineExceeded, "stream original deadline expires while Ack is held");
            Check(deadlineProducer.Starts == 0, "stream deadline before Ack starts zero producer");
        }
        finally { deadlineRelease.TrySetResult(); f.Server.BeforeStreamAcceptanceTestHook = null; }
        await f.QuiesceAsync(token);
        Check(await f.Rpc.Service.PlainAsync() == 42, "stream pre-Ack terminals preserve unrelated Unary progress");
    }
    private readonly struct EmptyStreamWriter(ProbeProducer producer) : IRpcClientStreamWriter
    {
        public async ValueTask WriteAsync(IRpcClientStreamSink sink, long requestId, CancellationToken cancellationToken)
        { await foreach (var _ in producer.WithCancellation(cancellationToken)) { } }
    }

    private static async Task AckStartRaceAsync(string transport, CancellationToken token)
    {
        await using var f = new StreamFixture(transport); await f.ConnectAsync(token);
        var claimed = Signal(); var release = Signal(); var producer = new ProbeProducer(); var id = 0L;
        f.Client.BeforeStreamProducerClaimTestHook = async (requestId, _) => { id = requestId; claimed.TrySetResult(); await release.Task; };
        using var caller = new CancellationTokenSource();
        try
        {
            var call = f.Rpc.Service.ConsumeAsync(producer, caller.Token).AsTask(); await claimed.Task.WaitAsync(token);
            Check(producer.Starts == 0 && f.Connection.PendingCalls.PhysicalStreamProducers == 1, "stream Ack signal alone cannot start user producer");
            caller.Cancel(); await Expect<OperationCanceledException>(() => call, "stream cancellation wins between Ack and producer claim");
            var duplicate = Signal(); f.Connection.PendingCalls.StreamAcceptanceObservedTestHook = _ => duplicate.TrySetResult();
            await f.Peer.Session.SendPacketAndFlushAsync(StaticBindingWire.CreateRequestAccepted(f.Peer.Session, id), token); await duplicate.Task.WaitAsync(token);
            Check(producer.Starts == 0 && f.Connection.ActiveCallCount > 0, "stream duplicate Ack retains canceled physical owner without resurrection");
            release.TrySetResult(); await f.QuiesceAsync(token);
            Check(producer.Starts == 0 && f.Connection.ActiveCallCount == 0, "stream lost start claim exits physically without producer entry");
        }
        finally { release.TrySetResult(); f.Client.BeforeStreamProducerClaimTestHook = null; }
    }

    private static async Task ProducerPhysicalDrainAsync(string transport, CancellationToken token)
    {
        await using var f = new StreamFixture(transport); await f.ConnectAsync(token);
        var connection = f.Connection; var producer = new ProbeProducer { HoldMove = true, HoldDispose = true };
        PendingStreamAdmission? original = null;
        f.Client.BeforeStreamProducerClaimTestHook = (id, _) => { original = connection.PendingCalls.CaptureStreamAdmission(id, false); return ValueTask.CompletedTask; };
        using var caller = new CancellationTokenSource();
        try
        {
            var call = f.Rpc.Service.ConsumeAsync(producer, caller.Token).AsTask(); await producer.Entered.Task.WaitAsync(token);
            caller.Cancel(); await Expect<OperationCanceledException>(() => call, "stream started producer cancels logically before physical exit");
            Check(connection.PendingCalls.Count == 0 && connection.PendingCalls.PhysicalStreamProducers == 1 && original!.Token.IsCancellationRequested,
                "stream original producer CTS and connection owner survive logical terminal");
            var stop = f.Client.StopAsync().AsTask();
            Check(!stop.IsCompleted, "stream Client Stop cannot pass held producer MoveNext");
            producer.ReleaseMove.TrySetResult(); await producer.DisposeEntered.Task.WaitAsync(token);
            Check(!stop.IsCompleted && !connection.PendingCalls.StreamProducerDrain.IsCompleted && original!.Token.IsCancellationRequested,
                "stream producer asynchronous Dispose retains original CTS and physical drain");
            producer.ReleaseDispose.TrySetResult(); await stop.WaitAsync(token);
            Check(producer.Exited.Task.IsCompleted && connection.PendingCalls.PhysicalStreamProducers == 0 && connection.PendingCalls.StreamProducerDrain.IsCompleted,
                "stream Client Stop joins producer physical disposal and returns quota");
        }
        finally { producer.Release(); }
    }

    private static async Task ConsumerPhysicalDrainAsync(string transport, CancellationToken token)
    {
        await using var f = new StreamFixture(transport); await f.ConnectAsync(token);
        var entered = Signal(); using var release = new ManualResetEventSlim(); var connection = f.Connection;
        var codec = new HeldIntCodec(f.Client.RuntimeContext.Codecs.GetCodec<int>(), entered, release);
        using var caller = new CancellationTokenSource();
        var stream = f.Site.InvokeServerStreamingAsync(f.Method(nameof(IFactsRpc.ProduceAsync)), default(RpcEmptyRequest), RpcEmptyRequestCodec.Instance, codec, null, caller.Token);
        var reading = ReadAllAsync(stream, caller.Token);
        try
        {
            await entered.Task.WaitAsync(token); caller.Cancel();
            var stop = f.Client.StopAsync().AsTask();
            Check(!stop.IsCompleted, "stream Client Stop joins in-flight original response Codec");
            release.Set();
            await Expect<OperationCanceledException>(() => reading, "stream consumer cancellation retains selected terminal after Codec exits");
            await stop.WaitAsync(token);
            Check(connection.Session.StreamManager.ActiveStreamCount == 0 && connection.ActiveCallCount == 0,
                "stream consumer physical exit reclaims original route and connection owner");
        }
        finally { release.Set(); }
    }
}
