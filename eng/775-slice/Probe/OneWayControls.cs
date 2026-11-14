using System.Buffers;
using System.Buffers.Binary;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Server;

namespace SharpLink.BindingSlice;
internal static partial class Program
{
    private static async Task RunOneWayAsync(string transport, CancellationToken ct)
    {
        await OneWayGrammarAsync(ct);
        await OneWayGuardsAsync(transport, ct);
        await OneWayAcceptedAsync(transport, false, ct);
        await OneWayAcceptedAsync(transport, true, ct);
        await OneWayEarlyReturnAsync(transport, ct);
        await OneWayRefusalAsync(transport, false, false, ct);
        await OneWayRefusalAsync(transport, false, true, ct);
        await OneWayRefusalAsync(transport, true, false, ct);
        await OneWayRefusalAsync(transport, true, true, ct);
        await OneWayCallQuotaAsync(transport, ct);
        await OneWayRouteQuotaAsync(transport, ct);
        await OneWayOverWindowAsync(transport, ct);
        await OneWayConflictingCountAsync(transport, ct);
        await OneWayStopAsync(transport, ct);
        await LocalAlcAsync(transport, "oneway local ALC", OneWayLocalOwnerAsync, ct);
        await OneWayServerOwnerAsync(transport, ct);
    }
    private static async Task OneWayGuardsAsync(string transport, CancellationToken ct)
    {
        await using var f = new OneWayFixture(transport); var writer = new ForbiddenStreamWriter(); var starts = ForbiddenStreamWriter.Starts;
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Expect<ArgumentNullException>(() => f.Site.InvokeOneWayAsync(f.Fire with { ContractId = long.MaxValue }, default(RpcEmptyRequest), null!, writer, null, canceled.Token).AsTask(),
            "oneway input Codec null guard remains before identity and cancellation");
        await ExpectCode(() => f.Site.InvokeOneWayAsync(f.Fire with { ContractId = long.MaxValue }, default(RpcEmptyRequest), RpcEmptyRequestCodec.Instance, writer, null, canceled.Token).AsTask(),
            SharpLinkErrorCode.FailedPrecondition, "oneway input identity guard remains before cancellation");
        await Expect<OperationCanceledException>(() => f.Site.InvokeOneWayAsync(f.Fire, default(RpcEmptyRequest), RpcEmptyRequestCodec.Instance, writer, null, canceled.Token).AsTask(),
            "oneway input caller cancellation remains before ready connection selection");
        await ExpectCode(() => f.Site.InvokeOneWayAsync(f.Fire with { ClientStreamCount = 128 }, default(RpcEmptyRequest), RpcEmptyRequestCodec.Instance, writer, null, ct).AsTask(),
            SharpLinkErrorCode.FailedPrecondition, "oneway input local count above 127 fails before writer entry");
        Check(ForbiddenStreamWriter.Starts == starts && f.Client.ReadyConnectionCount == 0,
            "oneway input failed guards acquire no connection and start no typed writer");
    }
    private static async Task OneWayGrammarAsync(CancellationToken ct)
    {
        static void Parse(int count, ProtocolV2FrameFlags flags = ProtocolV2FrameFlags.OneWay, ulong id = 1, int size = 17)
        {
            var bytes = new byte[15 + size]; bytes[0] = ProtocolV2Constants.Magic;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), size); bytes[5] = (byte)StaticBindingWire.OneWayInputRequest; bytes[6] = (byte)flags;
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(7), id); if (size > 0) bytes[15] = (byte)count;
            var input = new ReadOnlySequence<byte>(bytes); if (!ProtocolV2FrameParser.TryReadFrame(ref input, new(), out _, out _)) throw new InvalidDataException("Incomplete frame.");
        }
        Parse(1); Parse(127);
        Check(true, "oneway grammar independent count accepts one and 127");
        await Expect<SharpLinkProtocolViolationException>(() => { Parse(0); return Task.CompletedTask; }, "oneway grammar rejects zero count");
        await Expect<SharpLinkProtocolViolationException>(() => { Parse(128); return Task.CompletedTask; }, "oneway grammar rejects count above 127");
        await Expect<SharpLinkProtocolViolationException>(() => { Parse(1, 0); return Task.CompletedTask; }, "oneway grammar requires OneWay flag");
        await Expect<SharpLinkProtocolViolationException>(() => { Parse(1, ProtocolV2FrameFlags.OneWay | ProtocolV2FrameFlags.HasReturn); return Task.CompletedTask; }, "oneway grammar forbids return flag");
        await Expect<SharpLinkProtocolViolationException>(() => { Parse(1, id: 0); return Task.CompletedTask; }, "oneway grammar forbids zero correlation");
        await Expect<SharpLinkProtocolViolationException>(() => { Parse(1, size: 16); return Task.CompletedTask; }, "oneway grammar requires complete routing prefix");
        await using var f = new StreamFixture("tcp"); var owner = new StreamPendingOwner();
        using var pending = new PendingRequestTable(2, f.Client.RuntimeContext.Codecs, owner, f.Clock);
        var lease = pending.RegisterOneWayClientStream(default, ct, null);
        var slots = Field<Array>(pending, "_slots");
        var slot = slots.GetValue((int)(lease.Id & 1))!; slot.GetType().GetMethod("MarkRequestPublished")!.Invoke(slot, null);
        await Expect<SharpLinkProtocolViolationException>(() => { pending.AcceptStreamRequest(lease.Id); return Task.CompletedTask; }, "oneway pending explicitly rejects per-call RequestAccepted");
        pending.TryComplete(lease.Id, PendingCallCompletionReason.LocalStreamComplete); await lease.Operation.AsValueTask();
    }
    private static async Task OneWayAcceptedAsync(string transport, bool compressed, CancellationToken ct)
    {
        var prefix = compressed ? "oneway compressed" : "oneway plain";
        await using var f = new OneWayFixture(transport, compressed); await f.ConnectAsync(ct);
        var before = OneWayValueCodec.Decodes; var acks = 0;
        f.Server.BeforeStreamAcceptanceTestHook = (_, _, _) => { Interlocked.Increment(ref acks); throw new InvalidOperationException("OneWay must never Ack."); };
        f.Service.HoldBusiness = true;
        try
        {
            await f.Rpc.Service.FireAsync(new(7), OneWayValues(1), ct);
            await f.Service.Entered.Task.WaitAsync(ct);
            Check(!f.Service.Finished.Task.IsCompleted && acks == 0, prefix + " local completion is enqueue not business completion or Ack");
            f.Service.Release.TrySetResult(); await f.Service.Finished.Task.WaitAsync(ct); await f.QuiesceAsync(ct);
            Check(f.Service.Sum == 8 && OneWayValueCodec.Decodes - before == 2, prefix + " generated typed writer decodes original head and stream exactly once");
            Check(!compressed || f.Compression.Decodes == 2 && f.ClientCompression.Compressed >= 2, prefix + " business compression excludes independent count and routing prefix");
            Check(OneWayFixture.Flow(f.Connection.Session).SendConnectionCredit == 128 && f.Peer.Session.StreamManager.ActiveStreamCount == 0,
                prefix + " returns full receive credit and all routes");
            Check(await f.Rpc.Service.BarrierAsync(ct) == 42 && f.Mapper.Failures.IsEmpty, prefix + " unrelated Unary progresses without hidden server failure");
        }
        finally { f.Service.Release.TrySetResult(); f.Server.BeforeStreamAcceptanceTestHook = null; }
    }
    private static async Task OneWayEarlyReturnAsync(string transport, CancellationToken ct)
    {
        await using var f = new OneWayFixture(transport, true); await f.ConnectAsync(ct); f.Service.ReturnEarly = true;
        var before = OneWayValueCodec.Decodes;
        var producer = new OneWayControlledProducer();
        try
        {
            var call = f.Rpc.Service.FireAsync(new(9), producer, ct).AsTask(); await producer.Entered.Task.WaitAsync(ct);
            await f.Service.Finished.Task.WaitAsync(ct);
            Check(!call.IsCompleted && f.Peer.Session.StreamManager.ActiveStreamCount == 1,
                "oneway early return retains stable discard until peer terminal");
            producer.Release.TrySetResult(); await call.WaitAsync(ct); await f.QuiesceAsync(ct);
            Check(OneWayValueCodec.Decodes - before == 1 && f.Compression.Decodes == 1,
                "oneway early return discards later compressed items without business Codec or provider");
            Check(OneWayFixture.Flow(f.Connection.Session).SendConnectionCredit == 128 && f.Mapper.Failures.IsEmpty,
                "oneway early return reclaims credit routes and typed child without hidden errors");
        }
        finally { producer.Release.TrySetResult(); }
    }
    private sealed class OneWayControlledProducer : IAsyncEnumerable<OneWayValue>
    {
        internal TaskCompletionSource Entered { get; } = Signal(); internal TaskCompletionSource Release { get; } = Signal();
        public async IAsyncEnumerator<OneWayValue> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); await Release.Task; for (var i = 0; i < 8; i++) { cancellationToken.ThrowIfCancellationRequested(); yield return new(i); } }
    }
    private static async Task OneWayRefusalAsync(string transport, bool method, bool compressed, CancellationToken ct)
    {
        var prefix = "oneway refused " + (method ? "method" : "binding") + (compressed ? " compressed" : " plain");
        await using var f = new OneWayFixture(transport, compressed); await f.ConnectAsync(ct);
        var before = OneWayValueCodec.Decodes; var target = f.Site.Snapshot.Single();
        var id = await f.HeadAsync(method ? target.Token : target.Token ^ (1UL << 48), method ? long.MaxValue : f.Fire.MethodId, 1, compressed, ct);
        await UntilAsync(() => f.Peer.Session.StreamManager.ActiveStreamCount == 1, ct);
        Check(f.Service.Calls == 0 && OneWayValueCodec.Decodes == before && f.Compression.Decodes == 0, prefix + " installs bounded route before any wrong Codec or decompression");
        for (var i = 0; i < 16; i++) await f.DataAsync(id, 32, compressed, true, ct);
        await f.EndAsync(id, 1, ct); await f.QuiesceAsync(ct);
        Check(OneWayValueCodec.Decodes == before && f.Compression.Decodes == 0 && f.Service.Calls == 0, prefix + " drains many windows with zero business execution");
        Check(OneWayFixture.Flow(f.Connection.Session).RetainedSendStreamCount == 0 && OneWayFixture.Flow(f.Connection.Session).SendConnectionCredit == 128,
            prefix + " returns all stream and connection credit");
        Check(await f.Rpc.Service.BarrierAsync(ct) == 42 && f.Mapper.Failures.IsEmpty, prefix + " releases routes and preserves unrelated Unary");
    }
    private static async Task OneWayCallQuotaAsync(string transport, CancellationToken ct)
    {
        await using var f = new OneWayFixture(transport, true, true); await f.ConnectAsync(ct); var before = OneWayValueCodec.Decodes;
        var hold = f.Rpc.Service.HoldAsync(ct).AsTask(); await f.Service.Entered.Task.WaitAsync(ct);
        try
        {
            var id = await f.HeadAsync(f.Site.Snapshot.Single().Token, f.Fire.MethodId, 1, true, ct);
            await UntilAsync(() => f.Peer.Session.StreamManager.ActiveStreamCount == 1, ct);
            Check(f.Service.Calls == 0 && OneWayValueCodec.Decodes == before && f.Compression.Decodes == 0,
                "oneway call quota refusal precedes compressed head decoding and typed invocation");
            for (var i = 0; i < 8; i++) await f.DataAsync(id, 32, true, true, ct);
            await f.EndAsync(id, 1, ct);
            f.Service.Release.TrySetResult(); await hold.WaitAsync(ct); await f.QuiesceAsync(ct);
            Check(f.Service.Calls == 0 && OneWayValueCodec.Decodes == before && f.Compression.Decodes == 0,
                "oneway call quota discard never executes invalid provider data or wrong Codec");
            Check(await f.Rpc.Service.BarrierAsync(ct) == 42 && OneWayFixture.Flow(f.Connection.Session).SendConnectionCredit == 128,
                "oneway call quota returns credit route and original call capacity");
        }
        finally { f.Service.Release.TrySetResult(); }
    }
    private static async Task OneWayRouteQuotaAsync(string transport, CancellationToken ct)
    {
        await using var f = new OneWayFixture(transport, true); await f.ConnectAsync(ct); var peer = f.Peer; var connection = f.Connection;
        var before = OneWayValueCodec.Decodes; var wrong = f.Site.Snapshot.Single().Token ^ (1UL << 48);
        await f.HeadAsync(wrong, f.Fire.MethodId, 4, true, ct);
        await UntilAsync(() => peer.Session.StreamManager.ActiveStreamCount == 4, ct);
        Check(peer.Session.StreamManager.ActiveStreamCount == 4 && OneWayValueCodec.Decodes == before && f.Compression.Decodes == 0,
            "oneway route quota admits only configured bounded discard routes");
        await f.HeadAsync(wrong, f.Fire.MethodId, 1, true, ct);
        await UntilAsync(() => !peer.Session.IsConnected, ct); await f.Client.StopAsync(); await f.Server.StopAsync(TimeSpan.FromSeconds(5));
        Check(peer.Session.StreamManager.ActiveStreamCount == 0 && connection.ActiveCallCount == 0 &&
            OneWayFixture.Flow(connection.Session).RetainedSendStreamCount == 0,
            "oneway route quota exhaustion closes offending Session and reclaims all physical routes");
        Check(OneWayValueCodec.Decodes == before && f.Compression.Decodes == 0 && f.Service.Calls == 0,
            "oneway route quota exhaustion performs zero wrong Codec or decompression");
    }
    private sealed class OneWayGateInterceptor(TaskCompletionSource entered, TaskCompletionSource release) : ISharpLinkServerInterceptor
    {
        public async ValueTask InvokeAsync(SharpLinkServerInvocationContext context, SharpLinkServerInvocationDelegate next)
        { entered.TrySetResult(); await release.Task; throw new OperationCanceledException("Rejected before typed invocation."); }
    }
    private static async Task OneWayOverWindowAsync(string transport, CancellationToken ct)
    {
        await using var f = new OneWayFixture(transport, true); await f.ConnectAsync(ct); var peer = f.Peer; var connection = f.Connection;
        var entered = Signal(); var release = Signal(); f.Server.ReplaceInterceptors([new OneWayGateInterceptor(entered, release)]);
        var before = OneWayValueCodec.Decodes;
        try
        {
            var id = await f.HeadAsync(f.Site.Snapshot.Single().Token, f.Fire.MethodId, 1, false, ct); await entered.Task.WaitAsync(ct);
            await f.DataAsync(id, 48, true, false, ct);
            await f.DataAsync(id, 48, true, false, ct);
            await UntilAsync(() => !peer.Session.IsConnected, ct);
            Check(OneWayValueCodec.Decodes == before && f.Compression.Decodes == 0 && f.Service.Calls == 0,
                "oneway over-window compressed original lengths fail before provider and typed Codec");
            release.TrySetResult(); await f.Client.StopAsync(); await f.Server.StopAsync(TimeSpan.FromSeconds(5));
            Check(peer.Session.StreamManager.ActiveStreamCount == 0 && connection.ActiveCallCount == 0,
                "oneway over-window termination reclaims buffered bytes routes and physical ownership");
        }
        finally { release.TrySetResult(); }
    }
    private static async Task OneWayConflictingCountAsync(string transport, CancellationToken ct)
    {
        await using var f = new OneWayFixture(transport, true); await f.ConnectAsync(ct); var peer = f.Peer;
        var before = OneWayValueCodec.Decodes;
        await f.HeadAsync(f.Site.Snapshot.Single().Token, f.Fire.MethodId, 2, true, ct);
        await UntilAsync(() => !peer.Session.IsConnected, ct); await f.Client.StopAsync(); await f.Server.StopAsync(TimeSpan.FromSeconds(5));
        Check(OneWayValueCodec.Decodes == before && f.Compression.Decodes == 0 && f.Service.Calls == 0,
            "oneway conflicting known count terminates before wrong Codec and provider");
        Check(peer.Session.StreamManager.ActiveStreamCount == 0,
            "oneway conflicting known count allocates no inferred routes");
    }
    private static async Task OneWayStopAsync(string transport, CancellationToken ct)
    {
        await using var f = new StreamFixture(transport); await f.ConnectAsync(ct); var connection = f.Connection;
        var producer = new ProbeProducer { HoldMove = true, HoldDispose = true }; using var caller = new CancellationTokenSource();
        try
        {
            var call = f.Rpc.Service.FireStreamAsync(producer, caller.Token).AsTask(); await producer.Entered.Task.WaitAsync(ct);
            caller.Cancel(); await UntilAsync(() => connection.PendingCalls.Count == 0, ct);
            Check(!call.IsCompleted && connection.PendingCalls.PhysicalStreamProducers == 1 && producer.Token.IsCancellationRequested,
                "oneway Stop preserves producer CTS and physical owner after logical pending cancellation");
            var stop = f.Client.StopAsync().AsTask();
            Check(!stop.IsCompleted, "oneway Stop cannot pass noncooperative producer MoveNext");
            producer.ReleaseMove.TrySetResult(); await producer.DisposeEntered.Task.WaitAsync(ct);
            Check(!stop.IsCompleted && connection.PendingCalls.PhysicalStreamProducers == 1 && producer.Token.IsCancellationRequested,
                "oneway Stop retains original CTS through asynchronous producer Dispose");
            producer.ReleaseDispose.TrySetResult(); await Expect<OperationCanceledException>(() => call, "oneway Stop preserves caller cancellation after writer physically exits");
            await stop.WaitAsync(ct);
            Check(connection.PendingCalls.PhysicalStreamProducers == 0 && connection.PendingCalls.StreamProducerDrain.IsCompleted && connection.ActiveCallCount == 0,
                "oneway Stop joins producer physical disposal and returns pending quota");
        }
        finally { producer.Release(); }
    }
}
