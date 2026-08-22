from pathlib import Path


# The round-3 diff was produced before this new file was staged, so create the narrow
# metadata-bound channel explicitly in the workbench.
Path('src/SharpLink.Client/SharpLinkMetadataRpcChannel.cs').write_text('''namespace SharpLink.Client;

/// <summary>Binds one immutable metadata snapshot to a generated proxy without changing its contract signature.</summary>
internal sealed class SharpLinkMetadataRpcChannel(
    IRpcChannel inner,
    SharpLinkMetadata metadata) : IRpcChannel
{
    public IRpcRuntimeContext RuntimeContext => inner.RuntimeContext;

    public ValueTask<TResponse> InvokeUnaryAsync<TRequest, TResponse>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        SharpLinkMetadata? callMetadata,
        CancellationToken cancellationToken = default)
        => inner.InvokeUnaryAsync(
            method, request, requestCodec, responseCodec,
            callMetadata ?? metadata, cancellationToken);

    public ValueTask InvokeOneWayAsync<TRequest, TStreams>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        in TStreams streams,
        SharpLinkMetadata? callMetadata,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
        => inner.InvokeOneWayAsync(
            method, request, requestCodec, streams,
            callMetadata ?? metadata, cancellationToken);

    public ValueTask<TResponse> InvokeClientStreamingAsync<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        in TStreams streams,
        SharpLinkMetadata? callMetadata,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
        => inner.InvokeClientStreamingAsync(
            method, request, requestCodec, responseCodec, streams,
            callMetadata ?? metadata, cancellationToken);

    public IAsyncEnumerable<TResponse> InvokeServerStreamingAsync<TRequest, TResponse>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        SharpLinkMetadata? callMetadata,
        CancellationToken cancellationToken = default)
        => inner.InvokeServerStreamingAsync(
            method, request, requestCodec, responseCodec,
            callMetadata ?? metadata, cancellationToken);

    public IAsyncEnumerable<TResponse> InvokeDuplexStreamingAsync<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        in TStreams streams,
        SharpLinkMetadata? callMetadata,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
        => inner.InvokeDuplexStreamingAsync(
            method, request, requestCodec, responseCodec, streams,
            callMetadata ?? metadata, cancellationToken);

    public Task SendClientStreamAsync<T>(
        long requestId,
        ushort streamId,
        IAsyncEnumerable<T> stream,
        CancellationToken cancellationToken = default)
        => inner.SendClientStreamAsync(requestId, streamId, stream, cancellationToken);
}
''')

# Caller-selected metadata is a narrow optional capability. Built-in SharpLink clients implement
# it, but unrelated third-party ISharpLinkClient implementations are not forced to add a dummy
# method just because 2.0 gained this envelope capability.
for name, interface_name in [
    ('src/SharpLink.Abstractions/ISharpLinkClient.cs', 'ISharpLinkClient'),
    ('src/SharpLink.Abstractions/ISharpLinkMultiClusterClient.cs', 'ISharpLinkMultiClusterClient'),
]:
    p = Path(name)
    text = p.read_text()
    old = '    TContract Get<TContract>(SharpLinkMetadata metadata) where TContract : IService;'
    new = f'''    TContract Get<TContract>(SharpLinkMetadata metadata) where TContract : IService
        => throw new NotSupportedException(
            "This {interface_name} implementation does not support caller-selected metadata.");'''
    assert text.count(old) == 1, name
    p.write_text(text.replace(old, new, 1))

# These three methods are already async. SendRpcCall normally completes synchronously, but its
# ValueTask return exists for the deadline-sensitive emission path; await it here so a future
# non-completed send cannot be dropped.
p = Path('src/SharpLink.Client/SharpLinkClient.Invokers.cs')
text = p.read_text()
for old in [
    '''                producerLease = moduleProducerLifetime?.TakeLease() ?? default;
                SendRpcCall(
                    connection.Session,''',
    '''            connection = registration.Connection;
            requestId = registration.RequestId;
            SendRpcCall(
                connection.Session,''',
    '''            producerLease = moduleProducerLifetime?.TakeLease() ?? default;
            SendRpcCall(
                connection.Session,''',
]:
    assert text.count(old) == 1, old[:80]
    text = text.replace(old, old.replace('SendRpcCall(', 'await SendRpcCall('), 1)
p.write_text(text)

# Preserve the capability the old partition test demonstrated: the caller can choose metadata
# independently for concurrent invocations on the same client and method, without a business
# parameter or call-options bag.
p = Path('test/SharpLink.IntegrationTests/IntegrationBehaviorTests.cs')
text = p.read_text()
marker = '''    [Test]
    [NotInParallel]
    public async Task ServerStopShouldPreservePendingCallCancellationReasons()
'''
test = '''    [Test]
    public async Task CallerSelectedMetadataShouldVaryPerInvocation()
    {
        await using var harness = await TestHarness.CreateAsync();
        var tenantA = harness.Client.Get<ITestService>(new SharpLinkMetadata(
            new KeyValuePair<string, string>("tenant", "a")));
        var tenantB = harness.Client.Get<ITestService>(new SharpLinkMetadata(
            new KeyValuePair<string, string>("tenant", "b")));

        var results = await Task.WhenAll(
            tenantA.DescribeCallAsync(1, CancellationToken.None).AsTask(),
            tenantB.DescribeCallAsync(2, CancellationToken.None).AsTask());

        Ensure(results[0].StartsWith("1:a:", StringComparison.Ordinal),
            "caller-selected metadata A should stay bound to its invocation");
        Ensure(results[1].StartsWith("2:b:", StringComparison.Ordinal),
            "caller-selected metadata B should stay bound to its invocation");
    }

'''
assert marker in text
p.write_text(text.replace(marker, test + marker, 1))
