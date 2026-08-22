from pathlib import Path


def replace_once(path: str, old: str, new: str):
    p = Path(path)
    text = p.read_text()
    count = text.count(old)
    assert count == 1, (path, count, old[:80])
    p.write_text(text.replace(old, new, 1))


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

# These three methods are already async. SendRpcCall normally completes synchronously, but its
# ValueTask return exists for the deadline-sensitive emission path; await it here so the compiler
# and future changes cannot silently drop a non-completed send.
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
# All three selected calls end with control.Metadata and can safely await their ValueTask.
# Replace only the first three non-observeEmission call endings after the inserted awaits.
parts = text.split('await SendRpcCall(')
assert len(parts) == 4, len(parts)
for index in range(1, 4):
    assert 'control.Metadata);' in parts[index]
    parts[index] = parts[index].replace(
        'control.Metadata);',
        'control.Metadata).ConfigureAwait(false);',
        1)
p.write_text('await SendRpcCall('.join(parts))

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
