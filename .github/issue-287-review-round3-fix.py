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

# CreateSessionOverTestTransport completes the handshake by default. The emission test only needs
# a ready session, so do not attempt to complete the same handshake twice.
p = Path('test/SharpLink.UnitTests/Runtime/SendPumpTests.cs')
text = p.read_text()
old = '''        RpcSessionTestFixture.CompleteHandshake(session);
        var frame = new PooledByteBufferWriter();'''
assert text.count(old) == 1
text = text.replace(old, '        var frame = new PooledByteBufferWriter();', 1)

# Observe the explicit MaxLatency arm through the same stable hook used by the existing timed-batch
# tests. ActiveTimerCount can legitimately miss a short create/dispose handoff.
old = '''        var clock = new ManualTimeProvider();
        var input = new Pipe();
        var output = new Pipe();
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(clock)
            .Build(includeGeneratedAssemblyCatalog: false);
        var maxLatency = TimeSpan.FromSeconds(5);'''
new = '''        var clock = new ManualTimeProvider();
        var maxLatency = TimeSpan.FromSeconds(5);
        var provider = new TimerArmObservingTimeProvider(clock, maxLatency);
        var input = new Pipe();
        var output = new Pipe();
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(provider)
            .Build(includeGeneratedAssemblyCatalog: false);'''
assert text.count(old) == 1
text = text.replace(old, new, 1)
text = text.replace(
    '        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(10), clock);',
    '        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(10), provider);',
    1)
old = '''            session.SendPacket(frame, deadline);
            for (var i = 0; i < 1000 && clock.ActiveTimerCount == 0; i++)
                await Task.Yield();
            Ensure(clock.ActiveTimerCount > 0, "timed batch must arm its provider timer");
            clock.Advance(maxLatency);'''
new = '''            session.SendPacket(frame, deadline);
            await provider.ExpectedTimerArmed.WaitAsync(TimeSpan.FromSeconds(2));
            clock.Advance(maxLatency);'''
assert text.count(old) == 1
text = text.replace(old, new, 1)
p.write_text(text)

# Development API bumps are not cumulative. The published 1.1.1 baseline is API 3, so all 2.0
# generator stamps and current-manifest fixtures use API 4 consistently. Pre-release 5/6 stamps
# are not compatibility boundaries.
p = Path('src/SharpLink.Generator/RpcGenerator.ManifestEmitter.cs')
text = p.read_text()
old = ', 6, 2, \\"{EscapeString(ExecutingGeneratorVersion)}\\")]'
new = ', 4, 2, \\"{EscapeString(ExecutingGeneratorVersion)}\\")]'
assert text.count(old) == 1
p.write_text(text.replace(old, new, 1))

p = Path('test/SharpLink.Generator.Tests/RpcAnalyzerTests.cs')
text = p.read_text()
assert text.count(', 6, 2,') == 3
p.write_text(text.replace(', 6, 2,', ', 4, 2,'))
