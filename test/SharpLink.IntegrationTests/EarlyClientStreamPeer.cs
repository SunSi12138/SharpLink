using System.Reflection;

namespace SharpLink.IntegrationTests;

/// <summary>
/// An intentionally nonconforming peer for server pre-admission resource tests. It writes the
/// real Request and generated StreamData frames over an existing TCP connection, but starts the
/// producer without waiting for RequestAccepted. Normal client proxies always wait for that ACK.
/// </summary>
internal sealed class EarlyClientStreamPeer : IRpcChannel
{
    private static readonly FieldInfo ReadyConnectionsField = typeof(SharpLinkClient).GetField(
        "_readyConnections", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(nameof(SharpLinkClient), "_readyConnections");

    private readonly SharpLinkClient _client;
    private readonly ClientConnection _connection;
    private readonly SharpLinkRuntimeContext _runtime;
    private readonly RpcHash128 _assemblyHash;

    private EarlyClientStreamPeer(SharpLinkClient client, RpcHash128 assemblyHash)
    {
        _client = client;
        _runtime = (SharpLinkRuntimeContext)client.RuntimeContext;
        _assemblyHash = assemblyHash;
        var connections = (ClientConnection[])ReadyConnectionsField.GetValue(client)!;
        if (connections.Length != 1 || !connections[0].CanAcceptCalls)
            throw new InvalidOperationException("The early-stream peer requires one established test connection.");
        _connection = connections[0];
    }

    public IRpcRuntimeContext RuntimeContext => _runtime;

    internal static TContract CreateProxy<TContract>(ISharpLinkClient client) where TContract : IService
    {
        var manifest = GlobalCatalogManifestSource.Instance.CreateSnapshot().Single(candidate =>
            candidate.Contracts.Any(contract => contract.ContractType == typeof(TContract)));
        var contract = manifest.Contracts.Single(candidate => candidate.ContractType == typeof(TContract));
        var channel = new EarlyClientStreamPeer((SharpLinkClient)client, manifest.RpcAssemblyHash);
        return (TContract)contract.ProxyFactory(channel,
            channel._runtime.GetManifestCodecProvider(manifest.OwnerAssembly));
    }

    public ValueTask<TResponse> InvokeClientStreamingAsync<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        in TStreams streams,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
        => InvokeEarlyAsync(method, request, requestCodec, responseCodec, streams, metadata, cancellationToken);

    private async ValueTask<TResponse> InvokeEarlyAsync<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        TStreams streams,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken)
        where TStreams : struct, IRpcClientStreamWriter
    {
        if (method.Kind != RpcMethodKind.ClientStreaming || metadata is { Count: > 0 })
            throw new InvalidOperationException("The early-stream peer supports metadata-free client streams only.");
        if (!_connection.TryReserveSelectedCallAdmission())
            throw new InvalidOperationException("The test peer's selected connection is no longer ready.");

        long requestId;
        RpcRequestOperation<TResponse> operation;
        try
        {
            operation = _connection.PendingCalls.Rent(responseCodec, PendingCallKind.ClientStreaming,
                default, cancellationToken, out requestId,
                hasResponsePayload: method.HasResponsePayload, responseNullable: method.ResponseNullable);
        }
        catch
        {
            _connection.ReleaseCallAdmissionReservation();
            throw;
        }

        try
        {
            var producerToken = _connection.PendingCalls.GetProducerCancellationToken(requestId);
            await SendRequestAsync(method, request, requestCodec, requestId, producerToken).ConfigureAwait(false);
            // Deliberately omit WaitForRequestAdmissionAsync. This is the adversarial behavior
            // under test; frame encoding, send credit, cancellation and response arbitration stay real.
            _client.TrackFrameworkTask(ProduceEarlyAsync(streams, requestId, producerToken),
                "EarlyClientStreamTestPeer");
        }
        catch (Exception exception)
        {
            _connection.PendingCalls.TryComplete(requestId, PendingCallCompletionReason.SendFailure, exception);
        }
        return await operation.AsValueTask().ConfigureAwait(false);
    }

    private ValueTask SendRequestAsync<TRequest>(RpcMethodDescriptor method, TRequest request,
        IRpcCodec<TRequest> requestCodec, long requestId, CancellationToken cancellationToken)
    {
        var session = _connection.Session;
        var writer = session.RentFrameWriter();
        var ownsWriter = true;
        try
        {
            var flags = ProtocolV2FrameFlags.Cancellable |
                (method.HasResponsePayload ? ProtocolV2FrameFlags.HasReturn : ProtocolV2FrameFlags.None);
            using (writer.BeginPacketScope(ProtocolV2FrameType.Request, flags, unchecked((ulong)requestId)))
            {
                var identity = new ProtocolV2RequestIdentity(method.ContractId, method.MethodId,
                    _assemblyHash, method.Kind, checked((ushort)method.ClientStreamCount));
                ProtocolV2RequestIdentityCodec.Write(writer, in identity);
                requestCodec.Serialize(request, writer);
            }

            ownsWriter = false;
            var prepared = session.PrepareOutboundFrame(writer, cancellationToken);
            if (_connection.PendingCalls.TryPublishRequest(requestId, session, prepared,
                    observeEmission: false, cancellationToken, failureObserver: null,
                    out var emission, out var rejection))
                return emission;
            if (rejection is not null)
                throw rejection;
            _runtime.Buffers.Return(prepared);
            return ValueTask.CompletedTask;
        }
        finally
        {
            if (ownsWriter)
                _runtime.Buffers.Return(writer);
        }
    }

    private async Task ProduceEarlyAsync<TStreams>(TStreams streams, long requestId,
        CancellationToken cancellationToken) where TStreams : struct, IRpcClientStreamWriter
    {
        try
        {
            await streams.WriteAsync(_connection, requestId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _connection.PendingCalls.TryComplete(requestId, PendingCallCompletionReason.SendFailure, exception);
        }
    }

    public ValueTask<TResponse> InvokeUnaryAsync<TRequest, TResponse>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        SharpLinkMetadata? metadata, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Use the normal client for non-adversarial calls.");

    public ValueTask InvokeOneWayAsync<TRequest, TStreams>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, in TStreams streams,
        SharpLinkMetadata? metadata, CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter
        => throw new NotSupportedException("Normal OneWay producers already run without an ACK.");

    public IAsyncEnumerable<TResponse> InvokeServerStreamingAsync<TRequest, TResponse>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        SharpLinkMetadata? metadata, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Use the normal client for non-adversarial calls.");

    public IAsyncEnumerable<TResponse> InvokeDuplexStreamingAsync<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method, in TRequest request, IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec, in TStreams streams, SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default) where TStreams : struct, IRpcClientStreamWriter
        => throw new NotSupportedException("This test peer sends client-streaming requests only.");
}
