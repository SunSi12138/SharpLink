using SharpLink.Client;
using SharpLink.Sdk;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace SharpLink.UnitTests.Client;

internal static class ClientInvokerTestHelper
{
    private const long FixtureContractId = 1;

    private static readonly RpcMethodDescriptor SUnaryMethod = new(
        FixtureContractId,
        2,
        RpcMethodKind.Unary,
        HasResponsePayload: true,
        HasClientStreams: false,
        HasMethodTimeout: false,
        MethodTimeout: null);

    private static readonly RpcMethodDescriptor SIdempotentUnaryMethod = new(
        FixtureContractId,
        4,
        RpcMethodKind.Unary,
        HasResponsePayload: true,
        HasClientStreams: false,
        HasMethodTimeout: false,
        MethodTimeout: null,
        IsIdempotent: true);

    private static readonly RpcMethodDescriptor SOneWayMethod = new(
        FixtureContractId,
        2,
        RpcMethodKind.OneWay,
        HasResponsePayload: false,
        HasClientStreams: false,
        HasMethodTimeout: false,
        MethodTimeout: null);

    private static readonly RpcMethodDescriptor SServerStreamingMethod = new(
        FixtureContractId,
        3,
        RpcMethodKind.ServerStreaming,
        HasResponsePayload: true,
        HasClientStreams: false,
        HasMethodTimeout: false,
        MethodTimeout: null);

    internal static Task<SharpLinkClient.NStudyPreparedSite> PrepareAsync(
        SharpLinkClient client, params TestClientTransportFactory[] transports)
        => PrepareConnectionsAsync(client, Array.ConvertAll(transports, factory => factory.Connection));

    internal static Task<SharpLinkClient.NStudyPreparedSite> PrepareAsync(
        SharpLinkClient client, TestTransportConnection transport)
        => PrepareConnectionsAsync(client, transport);

    internal static async Task<SharpLinkClient.NStudyPreparedSite> PreparePublishedAsync(
        SharpLinkClient client, TestTransportConnection transport, TimeSpan timeout)
    {
        // SessionRefresh's fixed-pool cut hook runs before the Ready snapshot is published.
        using var wait = new CancellationTokenSource(timeout);
        var connection = FindTransport(CaptureReady(client), transport);
        while (connection is null)
        {
            await Task.Delay(10, wait.Token);
            connection = FindTransport(CaptureReady(client), transport);
        }
        return new(await transport.BindInvocationRouteAsync(connection, FixtureContractId));
    }

    private static async Task<SharpLinkClient.NStudyPreparedSite> PrepareConnectionsAsync(
        SharpLinkClient client, params TestTransportConnection[] transports)
    {
        if (transports.Length == 0)
            throw new ArgumentException("Specify the connected test transport(s) to prepare.", nameof(transports));
        var ready = CaptureReady(client);
        var routes = new SharpLinkClient.NStudyPreparedRoute[transports.Length];
        for (var i = 0; i < transports.Length; i++)
        {
            var transport = transports[i];
            var connection = FindTransport(ready, transport)
                ?? throw new InvalidOperationException("The test transport does not own a Ready session.");
            routes[i] = await transport.BindInvocationRouteAsync(connection, FixtureContractId, bindingSlot: i);
        }
        return transports.Length == 1 ? new(routes[0]) : new(routes);
    }

    private static readonly MethodInfo SCaptureReady = typeof(SharpLinkClient).GetMethod(
        "CaptureNStudyReadyConnections", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("The client ready-connection snapshot is unavailable.");

    private static ClientConnection[] CaptureReady(SharpLinkClient client)
        => (ClientConnection[])SCaptureReady.Invoke(client, null)!;

    private static ClientConnection? FindTransport(ClientConnection[] ready, TestTransportConnection transport)
        => Array.Find(ready, peer => peer.CanAcceptCalls && ReferenceEquals(peer.Session.Input, transport.Input));

    public static ValueTask<int> InvokeUnaryAsync(
        SharpLinkClient client,
        SharpLinkMetadata? metadata = null,
        CancellationToken cancellationToken = default,
        SharpLinkClient.NStudyPreparedSite? prepared = null)
    {
        var request = default(RpcEmptyRequest);
        return client.InvokeUnary(
            SUnaryMethod,
            in request,
            RpcEmptyRequestCodec.Instance,
            client.RuntimeContext.Codecs.GetCodec<int>(),
            metadata,
            cancellationToken,
            prepared);
    }

    public static ValueTask<int> InvokeIdempotentUnaryAsync(
        SharpLinkClient client,
        SharpLinkMetadata? metadata = null,
        CancellationToken cancellationToken = default,
        SharpLinkClient.NStudyPreparedSite? prepared = null)
    {
        var request = default(RpcEmptyRequest);
        return client.InvokeUnary(
            SIdempotentUnaryMethod,
            in request,
            RpcEmptyRequestCodec.Instance,
            client.RuntimeContext.Codecs.GetCodec<int>(),
            metadata,
            cancellationToken,
            prepared);
    }

    public static ValueTask InvokeOneWayAsync(
        SharpLinkClient client, SharpLinkClient.NStudyPreparedSite? prepared = null)
    {
        var request = default(RpcEmptyRequest);
        var streams = default(RpcNoClientStreams);
        return client.InvokeOneWay(
            SOneWayMethod,
            in request,
            RpcEmptyRequestCodec.Instance,
            in streams,
            metadata: null,
            cancellationToken: default,
            prepared);
    }

    public static IAsyncEnumerable<int> InvokeServerStreaming(
        SharpLinkClient client,
        CancellationToken cancellationToken = default,
        SharpLinkClient.NStudyPreparedSite? prepared = null)
    {
        var request = default(RpcEmptyRequest);
        var responseCodec = client.RuntimeContext.Codecs.GetCodec<int>();
        return prepared is null
            ? client.InvokeServerStreamingAsync(
                SServerStreamingMethod, in request, RpcEmptyRequestCodec.Instance, responseCodec, null, cancellationToken)
            : SharpLinkModuleRpcChannel.InvokeServerStreamingDeferred(
                client, null, SServerStreamingMethod, in request, RpcEmptyRequestCodec.Instance,
                responseCodec, null, cancellationToken, prepared);
    }
}
