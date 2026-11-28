namespace SharpLink.Abstractions;

/// <summary>A channel bound to one local RPC contract. Prepare it before the first call.</summary>
public interface IRpcContractChannel
{
    /// <summary>Gets the client-owned runtime context.</summary>
    IRpcRuntimeContext RuntimeContext { get; }

    /// <summary>Prepares this channel on the current Ready connection within a finite timeout.</summary>
    /// <exception cref="SharpLinkException">A full binding table returns
    /// <see cref="SharpLinkErrorCode.ResourceExhausted"/> with
    /// <see cref="SharpLinkErrorDetails.ResourceExhausted.PreparedBindingCapacity"/>.</exception>
    ValueTask PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>Invokes a unary method of this contract.</summary>
    ValueTask<TResponse> InvokeUnaryAsync<TRequest, TResponse>(RpcMethodDescriptor method, in TRequest request,
        IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec, SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default);

    /// <summary>Invokes a one-way method of this contract, optionally with typed client input.</summary>
    ValueTask InvokeOneWayAsync<TRequest, TStreams>(RpcMethodDescriptor method, in TRequest request,
        IRpcCodec<TRequest> requestCodec, in TStreams streams, SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default) where TStreams : struct, IRpcClientStreamWriter;

    /// <summary>Invokes a client-streaming method of this contract.</summary>
    ValueTask<TResponse> InvokeClientStreamingAsync<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec, in TStreams streams,
        SharpLinkMetadata? metadata, CancellationToken cancellationToken = default) where TStreams : struct, IRpcClientStreamWriter;

    /// <summary>Invokes a server-streaming method of this contract.</summary>
    IAsyncEnumerable<TResponse> InvokeServerStreamingAsync<TRequest, TResponse>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        SharpLinkMetadata? metadata, CancellationToken cancellationToken = default);

    /// <summary>Invokes a duplex method of this contract.</summary>
    IAsyncEnumerable<TResponse> InvokeDuplexStreamingAsync<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec, in TStreams streams,
        SharpLinkMetadata? metadata, CancellationToken cancellationToken = default) where TStreams : struct, IRpcClientStreamWriter;
}

/// <summary>Identifies a specific local registration of a contract on its issuing client.</summary>
public sealed class RpcLocalContract
{
    internal RpcLocalContract(Guid issuer, long generation, long contractId, RpcHash128 hash)
        => (Issuer, Generation, ContractId, Hash) = (issuer, generation, contractId, hash);

    internal Guid Issuer { get; }
    internal long Generation { get; }
    internal long ContractId { get; }
    internal RpcHash128 Hash { get; }
}
