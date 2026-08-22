from pathlib import Path


def replace_once(path: str, old: str, new: str):
    p = Path(path)
    text = p.read_text()
    count = text.count(old)
    assert count == 1, (path, count, old[:120])
    p.write_text(text.replace(old, new, 1))


# Bind one immutable metadata snapshot to a proxy without adding a business-contract parameter.
Path('src/SharpLink.Client/SharpLinkMetadataRpcChannel.cs').write_text('''namespace SharpLink.Client;

internal sealed class SharpLinkMetadataRpcChannel(
    IRpcChannel inner,
    SharpLinkMetadata metadata) : IRpcChannel
{
    public IRpcRuntimeContext RuntimeContext => inner.RuntimeContext;

    public ValueTask<TResponse> InvokeUnaryAsync<TRequest, TResponse>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        SharpLinkMetadata? callMetadata, CancellationToken cancellationToken = default)
        => inner.InvokeUnaryAsync(method, request, requestCodec, responseCodec,
            callMetadata ?? metadata, cancellationToken);

    public ValueTask InvokeOneWayAsync<TRequest, TStreams>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, in TStreams streams,
        SharpLinkMetadata? callMetadata, CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
        => inner.InvokeOneWayAsync(method, request, requestCodec, streams,
            callMetadata ?? metadata, cancellationToken);

    public ValueTask<TResponse> InvokeClientStreamingAsync<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method, in TRequest request, IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec, in TStreams streams, SharpLinkMetadata? callMetadata,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
        => inner.InvokeClientStreamingAsync(method, request, requestCodec, responseCodec, streams,
            callMetadata ?? metadata, cancellationToken);

    public IAsyncEnumerable<TResponse> InvokeServerStreamingAsync<TRequest, TResponse>(
        RpcMethodDescriptor method, in TRequest request, IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec, SharpLinkMetadata? callMetadata,
        CancellationToken cancellationToken = default)
        => inner.InvokeServerStreamingAsync(method, request, requestCodec, responseCodec,
            callMetadata ?? metadata, cancellationToken);

    public IAsyncEnumerable<TResponse> InvokeDuplexStreamingAsync<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method, in TRequest request, IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec, in TStreams streams, SharpLinkMetadata? callMetadata,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
        => inner.InvokeDuplexStreamingAsync(method, request, requestCodec, responseCodec, streams,
            callMetadata ?? metadata, cancellationToken);

    public Task SendClientStreamAsync<T>(long requestId, ushort streamId,
        IAsyncEnumerable<T> stream, CancellationToken cancellationToken = default)
        => inner.SendClientStreamAsync(requestId, streamId, stream, cancellationToken);
}
''')

# Caller-selected metadata is an optional narrow capability for third-party client implementations.
for path, interface_name in [
    ('src/SharpLink.Abstractions/ISharpLinkClient.cs', 'ISharpLinkClient'),
    ('src/SharpLink.Abstractions/ISharpLinkMultiClusterClient.cs', 'ISharpLinkMultiClusterClient'),
]:
    replace_once(
        path,
        '    TContract Get<TContract>(SharpLinkMetadata metadata) where TContract : IService;',
        f'''    TContract Get<TContract>(SharpLinkMetadata metadata) where TContract : IService
        => throw new NotSupportedException(
            "This {interface_name} implementation does not support caller-selected metadata.");''')

# Do not drop the deadline-sensitive ValueTask returned by SendRpcCall in async invokers.
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
    assert text.count(old) == 1, old[:100]
    text = text.replace(old, old.replace('SendRpcCall(', 'await SendRpcCall('), 1)
p.write_text(text)

# Prove two callers can choose different metadata concurrently on the same client and method.
replace_once(
    'test/SharpLink.IntegrationTests/IntegrationBehaviorTests.cs',
    '''    [Test]
    [NotInParallel]
    public async Task ServerStopShouldPreservePendingCallCancellationReasons()
''',
    '''    [Test]
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

    [Test]
    [NotInParallel]
    public async Task ServerStopShouldPreservePendingCallCancellationReasons()
''')

# The test fixture completes the handshake by default; do not complete it twice.
replace_once(
    'test/SharpLink.UnitTests/Runtime/SendPumpTests.cs',
    '''        RpcSessionTestFixture.CompleteHandshake(session);
        var frame = new PooledByteBufferWriter();''',
    '        var frame = new PooledByteBufferWriter();')

# Reuse the existing stable timer-arm observer rather than polling a transient active-timer count.
p = Path('test/SharpLink.UnitTests/Runtime/SendPumpTests.cs')
text = p.read_text()
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
assert text.count('        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(10), clock);') == 1
text = text.replace(
    '        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(10), clock);',
    '        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(10), provider);', 1)
old = '''            session.SendPacket(frame, deadline);
            for (var i = 0; i < 1000 && clock.ActiveTimerCount == 0; i++)
                await Task.Yield();
            Ensure(clock.ActiveTimerCount > 0, "timed batch must arm its provider timer");
            clock.Advance(maxLatency);'''
new = '''            session.SendPacket(frame, deadline);
            await provider.ExpectedTimerArmed.WaitAsync(TimeSpan.FromSeconds(2));
            clock.Advance(maxLatency);'''
assert text.count(old) == 1
p.write_text(text.replace(old, new, 1))

# Development API bumps are not cumulative. Published 1.1.1 is API 3, so 2.0 is API 4 everywhere.
replace_once(
    'src/SharpLink.Generator/RpcGenerator.ManifestEmitter.cs',
    r'), 6, 2, \"{EscapeString',
    r'), 4, 2, \"{EscapeString')
replace_once(
    'src/SharpLink.Generator/RpcGenerator.ReferencedManifestBootstrap.cs',
    '                    attribute.ConstructorArguments[1].Value is not 5 ||',
    '                    attribute.ConstructorArguments[1].Value is not 4 ||')
p = Path('test/SharpLink.Generator.Tests/RpcAnalyzerTests.cs')
text = p.read_text()
assert text.count(', 6, 2,') == 3
text = text.replace(', 6, 2,', ', 4, 2,')
text = text.replace('legacy API 3 locators must not be bootstrapped into an API 5 process',
                    'legacy API 3 locators must not be bootstrapped into an API 4 process')
p.write_text(text)
