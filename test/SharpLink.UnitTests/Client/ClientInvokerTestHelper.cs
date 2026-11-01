using SharpLink.Client;
using SharpLink.Sdk;
using System.Collections.Generic;
using System.Threading;
using System.Reflection;
using System.Linq;
using System.Diagnostics;

namespace SharpLink.UnitTests.Client;

internal static class ClientInvokerTestHelper
{
    internal static TestClientTransportFactory CreateTransport()
        => new(contractManifest: [new(ManualClientContractFixture.ContractId, ManualClientContractFixture.AssemblyHash)],
            acceptManualContractBindings: true);

    // Cold creation performs no readiness wait, explicit Prepare, or RPC. Production may start
    // background Bind demand on Ready owners; negative controls do not implicitly await readiness.
    internal static IRpcContractChannel CreateChannel(SharpLinkClient client)
        => client.CreateContractChannel(client.GetLocalContract<ManualClientContractFixture.IContract>());

    internal static async Task<IRpcContractChannel> CreatePreparedChannelAsync(SharpLinkClient client)
    {
        var channel = CreateChannel(client);
        var timeout = TimeSpan.FromSeconds(5);
        var started = Stopwatch.GetTimestamp();
        // A frozen client TimeProvider must not make fixture setup unbounded. This wall-clock
        // cancellation covers the same original setup budget, never a business RPC deadline.
        using var setupCancellation = new CancellationTokenSource(timeout);
        await channel.PrepareAsync(timeout, setupCancellation.Token);
        var site = (SharpLinkClient.ContractCallSite)channel;
        var siteType = typeof(SharpLinkClient.ContractCallSite);
        var bindingGate = (Lock)siteType.GetField("_bindingGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(site)!;
        var targetsField = siteType.GetField("_boundTargets", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var clientType = typeof(SharpLinkClient);
        var cluster = clientType.GetField("_cluster", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client);
        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            // Capture topology BEFORE the site gate: never invert topology -> binding lock order.
            var ready = cluster is null
                ? (ClientConnection[])clientType.GetField("_readyConnections", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!
                : (ClientConnection[])cluster.GetType().GetMethod("CaptureReadyConnections", BindingFlags.Public | BindingFlags.Instance)!.Invoke(cluster, null)!;
            lock (bindingGate)
            {
                var targets = (SharpLinkClient.ContractCallSite.BoundTarget[])targetsField.GetValue(site)!;
                if (ready.Length != 0 && ready.All(connection => targets.Any(target =>
                    ReferenceEquals(target.Connection, connection) && target.IsEligible)))
                    return channel;
            }
            await Task.Delay(1, setupCancellation.Token);
        }
        throw new TimeoutException("The exact manual contract did not bind every current Ready owner within the original preparation budget.");
    }

    private static readonly RpcMethodDescriptor SUnaryMethod = new(
        1,
        2,
        RpcMethodKind.Unary,
        HasResponsePayload: true,
        HasClientStreams: false,
        HasMethodTimeout: false,
        MethodTimeout: null);

    private static readonly RpcMethodDescriptor SIdempotentUnaryMethod = new(
        1,
        4,
        RpcMethodKind.Unary,
        HasResponsePayload: true,
        HasClientStreams: false,
        HasMethodTimeout: false,
        MethodTimeout: null,
        IsIdempotent: true);

    // The former root-channel fixture reused Unary's ID 2 across shapes. A genuine contract
    // has one declared shape per method identity: the manual OneWay method is explicitly ID 1.
    private static readonly RpcMethodDescriptor SOneWayMethod = new(
        1,
        1,
        RpcMethodKind.OneWay,
        HasResponsePayload: false,
        HasClientStreams: false,
        HasMethodTimeout: false,
        MethodTimeout: null);

    private static readonly RpcMethodDescriptor SServerStreamingMethod = new(
        1,
        3,
        RpcMethodKind.ServerStreaming,
        HasResponsePayload: true,
        HasClientStreams: false,
        HasMethodTimeout: false,
        MethodTimeout: null);

    public static ValueTask<int> InvokeUnaryAsync(
        SharpLinkClient client,
        SharpLinkMetadata? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var channel = CreateChannel(client);
        var request = default(RpcEmptyRequest);
        return channel.InvokeUnaryAsync(
            SUnaryMethod,
            in request,
            RpcEmptyRequestCodec.Instance,
            channel.RuntimeContext.Codecs.GetCodec<int>(),
            metadata,
            cancellationToken);
    }

    public static ValueTask<int> InvokeIdempotentUnaryAsync(
        SharpLinkClient client,
        SharpLinkMetadata? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var channel = CreateChannel(client);
        var request = default(RpcEmptyRequest);
        return channel.InvokeUnaryAsync(
            SIdempotentUnaryMethod,
            in request,
            RpcEmptyRequestCodec.Instance,
            channel.RuntimeContext.Codecs.GetCodec<int>(),
            metadata,
            cancellationToken);
    }

    public static ValueTask InvokeOneWayAsync(SharpLinkClient client)
    {
        var channel = CreateChannel(client);
        var request = default(RpcEmptyRequest);
        var streams = default(RpcNoClientStreams);
        return channel.InvokeOneWayAsync(
            SOneWayMethod,
            in request,
            RpcEmptyRequestCodec.Instance,
            in streams,
            default);
    }

    public static IAsyncEnumerable<int> InvokeServerStreaming(
        SharpLinkClient client,
        CancellationToken cancellationToken = default)
    {
        var channel = CreateChannel(client);
        var request = default(RpcEmptyRequest);
        return channel.InvokeServerStreamingAsync(
            SServerStreamingMethod,
            in request,
            RpcEmptyRequestCodec.Instance,
            channel.RuntimeContext.Codecs.GetCodec<int>(),
            default,
            cancellationToken);
    }
}
