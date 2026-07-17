using SharpLink.Client;
using SharpLink.Sdk;
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

    private static readonly RpcMethodDescriptor SOneWayMethod = new(
        1,
        2,
        RpcMethodKind.OneWay,
        HasResponsePayload: false,
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
}
