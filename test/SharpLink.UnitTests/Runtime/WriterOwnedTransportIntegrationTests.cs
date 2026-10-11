using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpLink.Client;
using SharpLink.Server;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Runtime;

// Real generated proxy -> transport -> server dispatch -> generated stream bridge.
// Both peers keep the original wire format. Mixed-mode cells prohibit a hidden requirement
// that the remote endpoint must have the writer-owned scheduler as well.
public sealed class WriterOwnedTransportIntegrationTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    [Test]
    [Arguments(false, false, true)]
    [Arguments(false, true, false)]
    [Arguments(false, true, true)]
    [Arguments(true, false, true)]
    [Arguments(true, true, false)]
    [Arguments(true, true, true)]
    public async Task FullRpcShapesMustInteroperateOverRealTransports(
        bool sharedMemory, bool clientWriter, bool serverWriter)
    {
        await RunPair(sharedMemory, clientWriter, serverWriter, false, async (service, token) =>
        {
            Ensure(await service.Ping(9, token) == 9, "unary control failed");
            var expected = Enumerable.Range(0, 64).ToArray();
            Ensure((await Collect(service.Download(64, token), token)).SequenceEqual(expected), "server streaming values/order");
            Ensure(await service.Upload(Values(64, token), token) == expected.Sum(), "client streaming result");
            Ensure((await Collect(service.Duplex(Values(64, token), token), token))
                .SequenceEqual(expected.Select(value => value + 1)), "duplex values/order");
            Ensure(await service.Merge(Values(32, token), Values(32, token), token) == 2 * Enumerable.Range(0, 32).Sum(),
                "named request streams did not share their request lifetime correctly");
            Ensure((await Collect(service.Download(0, token), token)).Count == 0, "empty server stream");
            Ensure(await service.Upload(Values(0, token), token) == 0, "empty client stream");
            Ensure(await service.Ping(10, token) == 10, "ordinary RPC failed after stream cleanup");
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EightConcurrentDuplexCallsMustProgressWithinBoundedWindows(bool sharedMemory)
    {
        await RunPair(sharedMemory, true, true, false, async (service, token) =>
        {
            var calls = Enumerable.Range(0, 8).Select(async _ =>
            {
                var values = await Collect(service.Duplex(Values(256, token), token), token);
                Ensure(values.SequenceEqual(Enumerable.Range(1, 256)), "concurrent duplex data loss/reordering");
            });
            await Task.WhenAll(calls).WaitAsync(Limit);
            Ensure(await service.Ping(11, token) == 11, "connection did not survive concurrent streams");
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CompressedDuplexMustPreservePayloadAndCreditOverRealTransport(bool sharedMemory)
    {
        await RunPair(sharedMemory, true, true, true, async (service, token) =>
        {
            var count = 0;
            await foreach (var item in service.ByteDuplex(ByteValues(token), token).WithCancellation(token))
            {
                Ensure(item.Length == 2048 && item.All(value => value == (byte)(count + 1)), "compressed duplex payload corruption");
                count++;
            }
            Ensure(count == 12, "compressed duplex item count");
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EarlyConsumerDisposalMustNotPoisonFollowingRpc(bool sharedMemory)
    {
        await RunPair(sharedMemory, true, true, false, async (service, token) =>
        {
            await using (var enumerator = service.Download(10000, token).GetAsyncEnumerator(token))
            {
                Ensure(await enumerator.MoveNextAsync(), "server stream did not start");
                Ensure(enumerator.Current == 0, "first item changed");
            }
            Ensure(await service.Ping(12, token) == 12, "consumer-abandoned stream killed the connection");
            Ensure(await service.Upload(Values(16, token), token) == 120, "new client stream was damaged by old cleanup");
        });
    }

    [Test]
    public void SenderSelectionMustBeFrozenAndInstanceScoped()
    {
        SharpLinkRuntimeOptions? mutable = null;
        using var selected = new SharpLinkRuntimeContextBuilder().Configure(options =>
        {
            mutable = options;
            options.FlowControl.WriterOwnedStreams = true;
        }).Build(includeGeneratedAssemblyCatalog: false);
        mutable!.FlowControl.WriterOwnedStreams = false;
        selected.Options.FlowControl.WriterOwnedStreams = false;
        Ensure(new RpcSessionCreationOptions(RpcSessionRole.Server, selected).WriterOwnedStreams,
            "builder or Options-clone mutation changed the frozen sender selection");
        Ensure(!new RpcSessionCreationOptions(RpcSessionRole.Client, selected, writerOwnedStreams: false).WriterOwnedStreams,
            "explicit legacy fixture override was ignored");
        using var legacy = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        Ensure(!new RpcSessionCreationOptions(RpcSessionRole.Server, legacy).WriterOwnedStreams,
            "one instance enabled a different instance's sender");
    }

    private static async Task RunPair(bool sharedMemory, bool clientWriter, bool serverWriter,
        bool compression, Func<IWriterOwnedTransportContract, CancellationToken, Task> verify)
    {
        using var timeout = new CancellationTokenSource(Limit);
        var serverBuilder = SharpLinkServerBuilder.Create();
        var clientBuilder = SharpClientBuilder.Create().UseRequestTimeout();
        if (sharedMemory)
        {
            var name = "writer-owned-" + Guid.NewGuid().ToString("N");
            serverBuilder.UseSharedMemory(name);
            clientBuilder.UseSharedMemory(name);
        }
        else
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            serverBuilder.UseTcp(port, IPAddress.Loopback.ToString());
            clientBuilder.UseTcp(IPAddress.Loopback.ToString(), port);
        }
        static void Configure(SharpLinkRuntimeOptions options, bool writer, bool compress)
        {
            options.FlowControl.WriterOwnedStreams = writer;
            // Explicit correctness-stress windows, not modified product defaults. Int32
            // streams exceed the 64-byte stream window and must return real wire credit.
            options.FlowControl.StreamReceiveWindowBytes = 64;
            options.FlowControl.ConnectionReceiveWindowBytes = 512;
            if (compress) options.Compression.Providers.Add(new TestCompressionProvider());
        }
        serverBuilder.UseRuntime(options => Configure(options, serverWriter, compression));
        clientBuilder.UseRuntime(options => Configure(options, clientWriter, compression));
        await using var server = serverBuilder.Build();
        await using var client = clientBuilder.Build();
        await server.StartAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        await verify(client.Get<IWriterOwnedTransportContract>(), timeout.Token).WaitAsync(Limit);

        var connections = (ClientConnection[])typeof(SharpLinkClient).GetField("_readyConnections",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        Ensure(connections.Length != 0 && connections.All(connection => connection.Session.UsesWriterOwnedStreams == clientWriter),
            "real client connection silently selected another sender");
        if (clientWriter)
            Ensure(connections.Any(connection => connection.Session.WriterOwnedStreams is not null),
                "client streaming never instantiated B3");
        var registry = (ServerConnectionRegistry)typeof(SharpLinkServer).GetField("_connectionRegistry",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        var sessions = registry.SnapshotActive().Select(connection => connection.Session).ToArray();
        Ensure(sessions.Length != 0 && sessions.All(session => session.UsesWriterOwnedStreams == serverWriter),
            "real server connection silently selected another sender");
        if (serverWriter)
            Ensure(sessions.Any(session => session.WriterOwnedStreams is not null), "server streaming never instantiated B3");
        if (compression)
            Ensure(sessions.All(session => (session.NegotiatedCapabilities & ProtocolV2Capabilities.Compression) != 0),
                "compression was not actually negotiated");
    }

    private static async Task<List<int>> Collect(IAsyncEnumerable<int> values, CancellationToken token)
    {
        var result = new List<int>();
        await foreach (var value in values.WithCancellation(token)) result.Add(value);
        return result;
    }

    private static async IAsyncEnumerable<int> Values(int count, [EnumeratorCancellation] CancellationToken token)
    {
        for (var index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            yield return index;
            if ((index & 15) == 15) await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<byte[]> ByteValues([EnumeratorCancellation] CancellationToken token)
    {
        for (var index = 1; index <= 12; index++)
        {
            token.ThrowIfCancellationRequested();
            yield return Enumerable.Repeat((byte)index, 2048).ToArray();
            await Task.Yield();
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}

[RpcContract]
public interface IWriterOwnedTransportContract : IService
{
    ValueTask<int> Ping(int value, CancellationToken cancellationToken);
    IAsyncEnumerable<int> Download(int count, CancellationToken cancellationToken);
    ValueTask<int> Upload(IAsyncEnumerable<int> values, CancellationToken cancellationToken);
    IAsyncEnumerable<int> Duplex(IAsyncEnumerable<int> values, CancellationToken cancellationToken);
    ValueTask<int> Merge(IAsyncEnumerable<int> left, IAsyncEnumerable<int> right, CancellationToken cancellationToken);
    IAsyncEnumerable<byte[]> ByteDuplex(IAsyncEnumerable<byte[]> values, CancellationToken cancellationToken);
}

[RpcService]
public sealed class WriterOwnedTransportService : IWriterOwnedTransportContract
{
    public ValueTask<int> Ping(int value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(value);
    }

    public async IAsyncEnumerable<int> Download(int count, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return index;
            if ((index & 15) == 15) await Task.Yield();
        }
    }

    public async ValueTask<int> Upload(IAsyncEnumerable<int> values, CancellationToken cancellationToken)
    {
        var sum = 0;
        await foreach (var value in values.WithCancellation(cancellationToken)) sum = checked(sum + value);
        return sum;
    }

    public async IAsyncEnumerable<int> Duplex(IAsyncEnumerable<int> values,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var value in values.WithCancellation(cancellationToken)) yield return value + 1;
    }

    public async ValueTask<int> Merge(IAsyncEnumerable<int> left, IAsyncEnumerable<int> right,
        CancellationToken cancellationToken)
        => await Upload(left, cancellationToken) + await Upload(right, cancellationToken);

    public async IAsyncEnumerable<byte[]> ByteDuplex(IAsyncEnumerable<byte[]> values,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var value in values.WithCancellation(cancellationToken)) yield return value;
    }
}
