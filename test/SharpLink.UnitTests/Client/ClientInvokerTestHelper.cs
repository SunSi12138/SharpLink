using SharpLink.Client;
using SharpLink.Sdk;
using System.Collections.Generic;
using System.Threading;

namespace SharpLink.UnitTests.Client;

internal static class ClientInvokerTestHelper
{
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

    private static readonly RpcMethodDescriptor SOneWayMethod = new(
        1,
        2,
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
        SharpLinkCallOptions options = default,
        CancellationToken cancellationToken = default)
    {
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        return channel.InvokeUnaryAsync(
            SUnaryMethod,
            in request,
            RpcEmptyRequestCodec.Instance,
            channel.RuntimeContext.Codecs.GetCodec<int>(),
            options,
            cancellationToken);
    }

    public static ValueTask<int> InvokeIdempotentUnaryAsync(
        SharpLinkClient client,
        SharpLinkCallOptions options = default,
        CancellationToken cancellationToken = default)
    {
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        return channel.InvokeUnaryAsync(
            SIdempotentUnaryMethod,
            in request,
            RpcEmptyRequestCodec.Instance,
            channel.RuntimeContext.Codecs.GetCodec<int>(),
            options,
            cancellationToken);
    }

    public static ValueTask InvokeOneWayAsync(SharpLinkClient client)
    {
        var channel = (IRpcChannel)client;
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
        var channel = (IRpcChannel)client;
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
