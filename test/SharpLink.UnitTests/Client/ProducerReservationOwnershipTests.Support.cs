using System.Diagnostics;
using System.Linq;
using System.Reflection;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed partial class ProducerReservationOwnershipTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var elapsed = Stopwatch.StartNew();
        while (!predicate() && elapsed.Elapsed < Bound) await Task.Yield();
        Ensure(predicate(), "the observed ownership transition did not finish within its existing phase bound");
    }

    private static async Task<Exception?> CaptureFailureAsync(Task task)
    {
        try { await task.WaitAsync(Bound); return null; }
        catch (Exception exception) when (exception is not TimeoutException) { return exception; }
    }

    private static async Task Invoke(Fixture fixture, RpcMethodKind kind, ProducerGroup group,
        IRpcCodec<RpcEmptyRequest>? requestCodec = null, bool timed = false, bool idempotent = false)
    {
        var channel = (IRpcChannel)fixture.Client;
        var request = default(RpcEmptyRequest);
        var writer = new GroupWriter(group);
        var method = new RpcMethodDescriptor(1, 7220 + (long)kind, kind,
            HasResponsePayload: false, HasClientStreams: group.Streams.Length != 0,
            HasMethodTimeout: timed, MethodTimeout: timed ? Bound : null,
            ClientStreamCount: group.Streams.Length, IsIdempotent: idempotent);
        var codec = requestCodec ?? RpcEmptyRequestCodec.Instance;
        var token = fixture.Cancellation.Token;
        switch (kind)
        {
            case RpcMethodKind.OneWay:
                await channel.InvokeOneWayAsync(method, in request, codec, in writer, null, token);
                break;
            case RpcMethodKind.ClientStreaming:
                _ = await channel.InvokeClientStreamingAsync(method, in request, codec,
                    RpcEmptyRequestCodec.Instance, in writer, null, token);
                break;
            case RpcMethodKind.Unary:
                _ = await channel.InvokeUnaryAsync(method, in request, codec, RpcEmptyRequestCodec.Instance, null, token);
                break;
            case RpcMethodKind.ServerStreaming:
            case RpcMethodKind.DuplexStreaming:
                var stream = kind == RpcMethodKind.ServerStreaming
                    ? channel.InvokeServerStreamingAsync(method, in request, codec, RpcEmptyRequestCodec.Instance, null, token)
                    : channel.InvokeDuplexStreamingAsync(method, in request, codec, RpcEmptyRequestCodec.Instance, in writer, null, token);
                await foreach (var _ in stream.WithCancellation(token).ConfigureAwait(false)) { }
                break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal required SharpLinkClient Client;
        internal required TestClientTransportFactory Transport;
        internal required ClientConnection Connection;
        internal CancellationTokenSource Cancellation { get; } = new();
        private bool _unrelatedReservationOwned = true;

        internal void ReleaseUnrelatedReservation()
        {
            if (!_unrelatedReservationOwned) return;
            _unrelatedReservationOwned = false;
            Connection.ReleaseCallAdmissionReservation();
        }

        internal static async Task<Fixture> CreateAsync(TimeProvider? clock = null,
            ProtocolV2Capabilities capabilities = ProtocolV2Capabilities.None,
            Action<SharpClientBuilder>? configure = null)
        {
            var transport = new TestClientTransportFactory(capabilities);
            var client = ClientBuilderTestHelper.Build(transport, builder =>
            {
                if (clock is not null) builder.UseTimeProvider(clock);
                configure?.Invoke(builder);
            });
            await client.ConnectAsync();
            var connection = ((ClientConnection[])typeof(SharpLinkClient).GetField(
                "_readyConnections", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!).Single();
            Ensure(connection.TryReserveCallAdmission(out _), "reserve an unrelated selection before the test invocation");
            return new Fixture { Client = client, Transport = transport, Connection = connection };
        }

        internal Task CompleteReplyAsync(RpcMethodKind kind, long requestId)
            => kind is RpcMethodKind.DuplexStreaming or RpcMethodKind.ServerStreaming
                ? Transport.Connection.InjectFrameAsync(ProtocolV2FrameType.StreamComplete,
                    ProtocolV2FrameFlags.None, unchecked((ulong)requestId), new byte[2])
                : Transport.Connection.InjectPacketAsync(ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, requestId);

        internal Task JoinProducersAsync() => WaitUntilAsync(() =>
        {
            var snapshot = Client.FrameworkTaskSnapshotForDiagnostics;
            Ensure(snapshot.TruncatedOperations == 0, "supervisor diagnostics must cover every operation");
            return !snapshot.Operations.Any(static operation => operation.Operation is
                "ClientStreamingProducer" or "DuplexStreamingInvoker" or "ServerStreamingInvoker");
        });

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Cancellation.CancelAsync();
                await JoinProducersAsync();
            }
            finally
            {
                ReleaseUnrelatedReservation();
                Cancellation.Dispose();
                await Client.DisposeAsync();
            }
        }
    }

    private sealed class ProducerGroup
    {
        internal readonly string Fault;
        internal readonly ProbeStream[] Streams;
        internal Task[] Tasks = [];
        internal TaskCompletionSource Started { get; } = Signal();
        internal bool CompletedOnWriterReturn;
        internal long RequestId;
        internal int Calls;
        internal Exception Failure { get; } = new InvalidOperationException("producer reservation test failure");

        internal ProducerGroup(int count, string fault = "none", bool holdDispose = false)
        {
            Fault = fault;
            Streams = Enumerable.Range(0, count).Select(index => new ProbeStream(this,
                index == 0 ? fault : "none", holdDispose && (index != 0 || fault == "none"))).ToArray();
        }
        internal Task AllDisposing => Task.WhenAll(Started.Task,
            Task.WhenAll(Streams.Select(static stream => stream.Disposing.Task)));
        internal void ReleaseAll() { foreach (var stream in Streams) stream.Release.TrySetResult(); }
        internal void AssertCounts()
        {
            Ensure(Calls == 1, "one RPC must invoke one writer group, not one writer per stream");
            foreach (var stream in Streams)
                Ensure(stream.Enumerations == 1 && stream.Moves == 1 && stream.Disposals == 1,
                    "every real producer must be enumerated and disposed exactly once");
        }
    }

    private readonly struct GroupWriter(ProducerGroup group) : IRpcClientStreamWriter
    {
        public ValueTask WriteAsync(IRpcClientStreamSink sink, long requestId, CancellationToken cancellationToken)
        {
            group.Calls++;
            group.RequestId = requestId;
            if (group.Fault == "writer-sync") throw group.Failure;
            if (group.Fault == "writer-async") return new ValueTask(FailAsync(group.Failure));
            group.Tasks = group.Streams.Select((stream, index) => sink.SendClientStreamAsync(
                requestId, checked((ushort)(index + 1)), stream, RpcEmptyRequestCodec.Instance, cancellationToken)).ToArray();
            var all = Task.WhenAll(group.Tasks);
            group.CompletedOnWriterReturn = all.IsCompleted;
            group.Started.TrySetResult();
            return new ValueTask(all);
        }
    }

    private static async Task FailAsync(Exception failure) { await Task.Yield(); throw failure; }

    private sealed class ProbeStream(ProducerGroup group, string fault, bool holdDispose)
        : IAsyncEnumerable<RpcEmptyRequest>, IAsyncEnumerator<RpcEmptyRequest>
    {
        internal int Enumerations, Moves, Disposals;
        internal TaskCompletionSource Disposing { get; } = Signal();
        internal TaskCompletionSource Release { get; } = Signal();
        public RpcEmptyRequest Current => default;
        public IAsyncEnumerator<RpcEmptyRequest> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            Enumerations++;
            if (fault == "enumerator") throw group.Failure;
            return this;
        }
        public ValueTask<bool> MoveNextAsync()
        {
            Moves++;
            if (fault == "move-sync") throw group.Failure;
            return fault == "move-async" ? FailMoveAsync(group.Failure) : new ValueTask<bool>(false);
        }
        public ValueTask DisposeAsync()
        {
            Disposals++;
            Disposing.TrySetResult();
            if (fault == "dispose-sync") throw group.Failure;
            if (fault == "dispose-async") return new ValueTask(FailAsync(group.Failure));
            return holdDispose ? new ValueTask(Release.Task) : ValueTask.CompletedTask;
        }
        private static async ValueTask<bool> FailMoveAsync(Exception failure)
        { await Task.Yield(); throw failure; }
    }
}
