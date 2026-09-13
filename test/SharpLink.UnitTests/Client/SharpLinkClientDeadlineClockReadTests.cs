using System.Reflection;
using System.Threading;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

/// <summary>
/// Pins how many monotonic clock samples one client invocation takes.
/// </summary>
/// <remarks>
/// The deadline path is built on samples that are allowed to fail a call: the resolve stage, the
/// pre-registration checkpoint, and the publication gate. Every one of them exists because a stale
/// timestamp would silently reopen a window an earlier boundary closed, so the read count is part
/// of the contract rather than an implementation detail. A change that removes a sample has to show
/// which boundary it removed; a change that adds one has to justify a new boundary.
/// </remarks>
public sealed class SharpLinkClientDeadlineClockReadTests
{
    [Test]
    public async Task PlainTimedUnaryShouldSampleOnlyItsAuthoritativeBoundaries()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.CancellationReason);
        await using var client = ClientBuilderTestHelper.Build(
            transport, builder => builder.UseTimeProvider(timeProvider));
        await PrepareAsync(client);

        var method = UnaryMethod(TimeSpan.FromSeconds(5));
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        var before = timeProvider.TimestampReadCount;

        var invocation = channel.InvokeUnaryAsync(
            method,
            in request,
            RpcEmptyRequestCodec.Instance,
            channel.RuntimeContext.Codecs.GetCodec<int>(),
            metadata: null).AsTask();
        var sent = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Request);
        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)sent.Header.RequestId));
        Ensure(await invocation == 0, "the timed Unary must receive its response");

        var reads = timeProvider.TimestampReadCount - before;
        // Resolution samples once, the pre-registration checkpoint samples once before the call
        // starts waiting, publication samples once, and the response terminal samples once. The
        // remainder belongs to the send pump and the session activity stamp, which are per-batch
        // infrastructure shared with untimed traffic.
        Ensure(reads == 8,
            $"a plain timed Unary must take exactly its eight boundary/infrastructure samples, but took {reads}");
    }

    private static async Task PrepareAsync(SharpLinkClient client)
    {
        await client.ConnectAsync();
        await GetOnlyReadyConnection(client).Session.FlushSendQueueAsync();
    }

    private static RpcMethodDescriptor UnaryMethod(TimeSpan? methodTimeout)
        => new(
            ContractId: 1,
            MethodId: 291,
            Kind: RpcMethodKind.Unary,
            HasResponsePayload: true,
            HasClientStreams: false,
            HasMethodTimeout: methodTimeout.HasValue,
            MethodTimeout: methodTimeout);

    private static ClientConnection GetOnlyReadyConnection(SharpLinkClient client)
    {
        var connections = (ClientConnection[])(typeof(SharpLinkClient).GetField(
                "_readyConnections",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(client) ?? throw new Exception("cannot find ready connection selection snapshot"));
        Ensure(connections.Length == 1, "expected exactly one ready connection");
        return connections[0];
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
