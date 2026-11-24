namespace SharpLink.IntegrationTests;

// Test-only adapter for the existing raw grammar controls, never for prepared RPC coverage.
internal sealed class LegacyGrammarFixtureReceiver(IRpcChannel inner) : IRpcContractChannel
{
    internal static T Create<T>(ISharpLinkClient client) where T : IService
    {
        var assembly = typeof(T).Assembly;
        var result = SharpLinkAssemblyManifestLoader.TryLoad(assembly, out var manifest);
        if (!result.Succeeded)
            throw new InvalidOperationException($"Unable to load the fixture contract manifest: {result.Error}");

        var contract = manifest!.Contracts.Single(descriptor => descriptor.ContractType == typeof(T));
        var codecs = RpcGeneratedCodecResolver.GetProvider(client.RuntimeContext, assembly);
        return (T)contract.ProxyFactory(new LegacyGrammarFixtureReceiver((IRpcChannel)client), codecs);
    }

    public IRpcRuntimeContext RuntimeContext => inner.RuntimeContext;

    public ValueTask PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => ValueTask.FromException(new NotSupportedException("This legacy receiver cannot prepare a contract."));

    public ValueTask<TResponse> InvokeUnaryAsync<TRequest, TResponse>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        SharpLinkMetadata? callMetadata, CancellationToken cancellationToken = default)
        => inner.InvokeUnaryAsync(method, request, requestCodec, responseCodec,
            callMetadata, cancellationToken);

    public ValueTask InvokeOneWayAsync<TRequest, TStreams>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, in TStreams streams,
        SharpLinkMetadata? callMetadata, CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
        => inner.InvokeOneWayAsync(method, request, requestCodec, streams,
            callMetadata, cancellationToken);

    public ValueTask<TResponse> InvokeClientStreamingAsync<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method, in TRequest request, IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec, in TStreams streams, SharpLinkMetadata? callMetadata,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
        => inner.InvokeClientStreamingAsync(method, request, requestCodec, responseCodec, streams,
            callMetadata, cancellationToken);

    public IAsyncEnumerable<TResponse> InvokeServerStreamingAsync<TRequest, TResponse>(
        RpcMethodDescriptor method, in TRequest request, IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec, SharpLinkMetadata? callMetadata,
        CancellationToken cancellationToken = default)
        => inner.InvokeServerStreamingAsync(method, request, requestCodec, responseCodec,
            callMetadata, cancellationToken);

    public IAsyncEnumerable<TResponse> InvokeDuplexStreamingAsync<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method, in TRequest request, IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec, in TStreams streams, SharpLinkMetadata? callMetadata,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
        => inner.InvokeDuplexStreamingAsync(method, request, requestCodec, responseCodec, streams,
            callMetadata, cancellationToken);
}
