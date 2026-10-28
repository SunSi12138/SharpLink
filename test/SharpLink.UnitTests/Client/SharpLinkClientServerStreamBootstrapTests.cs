using System.Buffers;
using System.Linq;
using System.Reflection;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientServerStreamBootstrapTests
{
    private static readonly AsyncLocal<string?> Ambient = new();

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task BootstrapMustIsolateInitialCodecContextAndPreserveTerminalOutcome(int scenario)
    {
        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.CancellationReason);
        await using var client = ClientBuilderTestHelper.Build(transport);
        await client.ConnectAsync();
        var connection = GetConnection(client);
        await connection.Session.FlushSendQueueAsync();
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timed = scenario is 1 or 4;
        if (timed)
            transport.Connection.RunOnNextOutputBufferRequest(() =>
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("test failed to release emission");
            });
        var marker = scenario == 2 ? new InvalidOperationException("codec marker") : null;
        var codec = new ContextCodec(marker, scenario == 3 ? cancellation : null, scenario == 4);
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        var method = new RpcMethodDescriptor(
            ContractId: 1, MethodId: 941, Kind: RpcMethodKind.ServerStreaming,
            HasResponsePayload: true, HasClientStreams: false,
            HasMethodTimeout: timed, MethodTimeout: timed ? TimeSpan.FromSeconds(30) : null);
        var previous = SynchronizationContext.Current;
        Ambient.Value = "caller";
        try
        {
            var stream = channel.InvokeServerStreamingAsync(method, in request, codec,
                channel.RuntimeContext.Codecs.GetCodec<int>(), metadata: null, cancellation.Token);
            Ensure(codec.Calls == 1, "test must actually enter the production server-stream bootstrap");
            Ensure(Ambient.Value == "caller" && ReferenceEquals(SynchronizationContext.Current, previous) && !ExecutionContext.IsFlowSuppressed(),
                "initial codec ExecutionContext/SynchronizationContext mutations must not escape async-equivalent boundary");
            await using var enumerator = stream.GetAsyncEnumerator();
            var dispatcher = GetDispatcher(enumerator);
            if (timed)
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Ensure(client.FrameworkTaskSnapshotForDiagnostics.Operations.Any(static operation => operation.Operation == "ServerStreamingInvoker"),
                    "timed blocked send must exercise pending bootstrap supervision");
                Ensure(ProducerOperations(dispatcher) == 1, "pending send must own exactly one registration retention");
            }
            if (marker is null) cancellation.Cancel();
            Exception? failure = null;
            try { await enumerator.MoveNextAsync(); }
            catch (Exception exception) { failure = exception; }
            Ensure(marker is not null ? ReferenceEquals(marker, failure) : failure is OperationCanceledException,
                "sync codec fault or cancellation must remain the user-visible terminal result");
            for (var attempt = 0; attempt < 100 && client.FrameworkTaskSnapshotForDiagnostics.Operations.Any(
                     static operation => operation.Operation == "ServerStreamingInvoker"); attempt++)
                await Task.Delay(10);
            Ensure(!client.FrameworkTaskSnapshotForDiagnostics.Operations.Any(static operation => operation.Operation == "ServerStreamingInvoker"),
                "terminal send observation must retire before consumer disposal");
            Ensure(ProducerOperations(dispatcher) == 0, "terminal bootstrap must release its registration retention exactly once");
        }
        finally
        {
            release.Set();
            SynchronizationContext.SetSynchronizationContext(previous);
            Ambient.Value = null;
        }
        await connection.Session.FlushSendQueueAsync();
        for (var attempt = 0; attempt < 100 && client.FrameworkTaskSnapshotForDiagnostics.Operations.Any(
                 static operation => operation.Operation == "ServerStreamingInvoker"); attempt++)
            await Task.Delay(10);
        Ensure(!client.FrameworkTaskSnapshotForDiagnostics.Operations.Any(static operation => operation.Operation == "ServerStreamingInvoker"),
            "completed bootstrap must release supervision independently of stream consumer lifetime");
    }

    private sealed class ContextCodec(Exception? failure, CancellationTokenSource? cancel, bool suppressFlow) : IRpcCodec<RpcEmptyRequest>
    {
        internal int Calls;
        public void Serialize(in RpcEmptyRequest value, IBufferWriter<byte> buffer)
        {
            Calls++;
            Ambient.Value = "codec";
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            if (suppressFlow) _ = ExecutionContext.SuppressFlow();
            cancel?.Cancel();
            if (failure is not null) throw failure;
        }
        public RpcEmptyRequest Deserialize(in ReadOnlySequence<byte> buffer) => default;
    }

    private static PooledAsyncStreamDispatcher<int> GetDispatcher(IAsyncEnumerator<int> enumerator)
    {
        // Unwrap logical and optional telemetry enumerators without disabling production telemetry.
        for (var depth = 0; depth < 4; depth++)
        {
            if (enumerator is PooledAsyncStreamDispatcher<int> dispatcher)
                return dispatcher;
            var field = enumerator.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(candidate => typeof(IAsyncEnumerator<int>).IsAssignableFrom(candidate.FieldType));
            enumerator = (IAsyncEnumerator<int>)field.GetValue(enumerator)!;
        }
        throw new Exception("production stream did not expose its expected dispatcher owner");
    }

    private static int ProducerOperations(PooledAsyncStreamDispatcher<int> dispatcher)
        => (int)typeof(PooledAsyncStreamDispatcher<int>).GetField("_producerOperations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dispatcher)!;

    private static ClientConnection GetConnection(SharpLinkClient client)
        => ((ClientConnection[])typeof(SharpLinkClient).GetField("_readyConnections", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!)[0];

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
