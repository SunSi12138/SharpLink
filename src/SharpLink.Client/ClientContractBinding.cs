namespace SharpLink.Client;

/// <summary>The immutable local identity and explicit server constraint frozen by a proxy.</summary>
internal sealed class ClientContractBinding(
    long contractId,
    RpcHash128 rpcAssemblyHash,
    ContractRegistrationReceipt? receipt = null,
    RpcRegistrationRequirement requirement = RpcRegistrationRequirement.Compatible)
{
    internal long ContractId { get; } = contractId;
    internal RpcHash128 RpcAssemblyHash { get; } = rpcAssemblyHash;
    internal bool HasReceipt { get; } = receipt.HasValue;
    internal ContractRegistrationReceipt? Receipt { get; } = receipt;
    internal RpcRegistrationRequirement Requirement { get; } = requirement;

    internal ClientContractBinding WithOptions(RpcProxyOptions? options)
    {
        if (options is null)
            return this;
        if (!Enum.IsDefined(options.Requirement))
            throw new ArgumentOutOfRangeException(nameof(options), "The registration requirement is not supported.");
        if (options.Receipt is not { } receipt)
        {
            if (options.Requirement != RpcRegistrationRequirement.Compatible)
                throw new ArgumentException("A version requirement requires a registration receipt.", nameof(options));
            return this;
        }
        if (receipt.ServerInstanceId == Guid.Empty || receipt.RegistryEpoch == Guid.Empty ||
            receipt.RegistrationId == Guid.Empty ||
            receipt.ContractId != ContractId || receipt.RpcAssemblyHash != RpcAssemblyHash)
        {
            throw new ArgumentException("The receipt must identify this local contract and a complete server registration.", nameof(options));
        }
        return new ClientContractBinding(ContractId, RpcAssemblyHash, receipt, options.Requirement);
    }
}

/// <summary>Passes a frozen binding explicitly into the existing logical-call lifetime.</summary>
internal sealed class SharpLinkContractRpcChannel(SharpLinkClient inner, ClientContractBinding binding) : IRpcChannel
{
    public IRpcRuntimeContext RuntimeContext => inner.RuntimeContext;

    public ValueTask<TResponse> InvokeUnaryAsync<TRequest, TResponse>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        SharpLinkMetadata? metadata, CancellationToken cancellationToken = default)
        => inner.InvokeUnaryBoundAsync(method, request, requestCodec, responseCodec, metadata, cancellationToken, binding);

    public ValueTask InvokeOneWayAsync<TRequest, TStreams>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, in TStreams streams,
        SharpLinkMetadata? metadata, CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
        => inner.InvokeOneWayBoundAsync(method, request, requestCodec, streams, metadata, cancellationToken, binding);

    public ValueTask<TResponse> InvokeClientStreamingAsync<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        in TStreams streams, SharpLinkMetadata? metadata, CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
        => inner.InvokeClientStreamingBoundAsync(method, request, requestCodec, responseCodec, streams, metadata, cancellationToken, binding);

    public IAsyncEnumerable<TResponse> InvokeServerStreamingAsync<TRequest, TResponse>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        SharpLinkMetadata? metadata, CancellationToken cancellationToken = default)
        => inner.InvokeServerStreamingBoundAsync(method, request, requestCodec, responseCodec, metadata, cancellationToken, binding);

    public IAsyncEnumerable<TResponse> InvokeDuplexStreamingAsync<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        in TStreams streams, SharpLinkMetadata? metadata, CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
        => inner.InvokeDuplexStreamingBoundAsync(method, request, requestCodec, responseCodec, streams, metadata, cancellationToken, binding);
}
