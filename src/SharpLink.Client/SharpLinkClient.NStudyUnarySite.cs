namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    // Private checkpoint: the existing ABI supplies a real generated proxy, but
    // only a unary-only contract may be constructed for this receiver.
    internal NStudyUnarySite<T> CreateNStudyUnarySite<T>(SharpLinkMetadata? metadata = null) where T : IService
    {
        if (_cluster is not null)
            throw new NotSupportedException("The private unary site requires a single-endpoint client.");
        if (!Volatile.Read(ref _proxies).TryGetValue(typeof(T), out var registration))
            throw new InvalidOperationException($"Proxy for service interface {typeof(T).FullName} is not registered.");
        if (registration.Descriptor.Methods.Any(method =>
                method.Kind != RpcMethodKind.Unary || method.Invocation is not { Kind: RpcMethodKind.Unary }))
            throw new NotSupportedException("The private unary site requires a generated unary-only contract.");
        return new NStudyUnarySite<T>(this, registration, FindOwningManifest(registration).RpcAssemblyHash, metadata);
    }

    internal sealed class NStudyPreparedUnary(ClientConnection connection, NStudyClientRoute route, NStudyAck ack)
    {
        // A prepared site is not an owner of a retired physical Session.
        private readonly WeakReference<ClientConnection> _connection = new(connection);
        private readonly WeakReference<NStudyClientRoute> _route = new(route);
        internal NStudyAck Ack { get; } = ack;

        internal bool TryGetConnection(out ClientConnection connection) => _connection.TryGetTarget(out connection!);
        internal NStudyClientRoute RequireRoute() => _route.TryGetTarget(out var route) ? route : throw Unavailable();
        internal bool IsReadyOn(ClientConnection connection)
            => _connection.TryGetTarget(out var original) && ReferenceEquals(original, connection) &&
               _route.TryGetTarget(out var route) && route.IsOpenOn(connection.Session);

        internal static SharpLinkException Unavailable()
            => new(SharpLinkErrorCode.Unavailable, "The private RPC site needs a current prepared Session.");
    }

    internal sealed class NStudyUnarySite<T> : IRpcChannel where T : IService
    {
        private readonly SharpLinkClient _client;
        private readonly ClientProxyRegistration _registration;
        private readonly RpcHash128 _hash;
        private readonly SharpLinkMetadata? _metadata;
        private NStudyPreparedUnary? _prepared;

        internal NStudyUnarySite(SharpLinkClient client, ClientProxyRegistration registration,
            RpcHash128 hash, SharpLinkMetadata? metadata)
        {
            _client = client;
            _registration = registration;
            _hash = hash;
            _metadata = metadata;
            Service = (T)registration.Descriptor.ProxyFactory(this, registration.Codecs);
        }

        internal T Service { get; }
        public IRpcRuntimeContext RuntimeContext => _client.RuntimeContext;
        internal IRpcCodec<TValue> GetCodec<TValue>() => _registration.Codecs.GetCodec<TValue>();

        internal async ValueTask<NStudyAck> PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(timeout, SharpLinkTimer.MaximumDelay);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureLocalReady();
            var connection = _client.RequireSingleNStudyConnection();
            if (Volatile.Read(ref _prepared) is { } current && current.IsReadyOn(connection))
                return current.Ack;

            using var budget = new CancellationTokenSource(timeout, _client._runtimeContext.TimeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(budget.Token, cancellationToken);
            _client.ValidateRemoteContractAssembly(_registration);
            var contract = _registration.Descriptor.ContractId;
            var ack = await BindNStudyContractOnAsync(connection, contract, _hash, default, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            EnsureLocalReady();
            var route = connection.Session.CaptureNStudyRoute(contract);
            if (route.Token != ack.BindingId || route.Target != ack.Target || route.Hash != _hash ||
                !route.IsOpenOn(connection.Session) || !ReferenceEquals(_client.RequireSingleNStudyConnection(), connection))
                throw NStudyPreparedUnary.Unavailable();
            Volatile.Write(ref _prepared, new NStudyPreparedUnary(connection, route, ack));
            return ack;
        }

        private void EnsureLocalReady()
        {
            if (_client._shutdownCts.IsCancellationRequested || _client.State == SharpLinkConnectionState.Stopped)
                throw CreateConnectionClosedException("Client is not accepting new calls.");
            if (_registration.Module is { State: not SharpLinkDynamicModuleState.Running })
                throw NStudyPreparedUnary.Unavailable();
        }

        internal ManualUnary BindUnary(long methodId)
        {
            foreach (var generated in _registration.Descriptor.Methods)
            {
                if (generated.MethodId == methodId && generated.Invocation is { } method &&
                    method.ContractId == _registration.Descriptor.ContractId &&
                    method.MethodId == methodId && method.Kind == RpcMethodKind.Unary)
                    return new ManualUnary(this, method);
            }
            throw new ArgumentOutOfRangeException(nameof(methodId), "No such local generated unary method.");
        }

        internal sealed class ManualUnary(NStudyUnarySite<T> site, RpcMethodDescriptor method)
        {
            internal ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(in TRequest request,
                IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
                SharpLinkMetadata? metadata = null, CancellationToken cancellationToken = default)
                => site.Invoke(method, request, requestCodec, responseCodec, metadata, cancellationToken);
        }

        ValueTask<TResponse> IRpcChannel.InvokeUnaryAsync<TRequest, TResponse>(RpcMethodDescriptor method,
            in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
            SharpLinkMetadata? metadata, CancellationToken cancellationToken)
            => Invoke(method, request, requestCodec, responseCodec, metadata, cancellationToken);

        private ValueTask<TResponse> Invoke<TRequest, TResponse>(RpcMethodDescriptor method,
            in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
            SharpLinkMetadata? metadata, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (method.ContractId != _registration.Descriptor.ContractId || method.Kind != RpcMethodKind.Unary)
                throw new InvalidOperationException("The method does not use this private contract receiver.");
            var prepared = Volatile.Read(ref _prepared) ?? throw NStudyPreparedUnary.Unavailable();
            return _registration.Module is { } module
                ? SharpLinkModuleRpcChannel.InvokeUnaryWithModule(_client, module, method, request, requestCodec,
                    responseCodec, metadata ?? _metadata, cancellationToken, prepared)
                : _client.InvokeUnary(method, request, requestCodec, responseCodec, metadata ?? _metadata,
                    cancellationToken, prepared);
        }

        ValueTask IRpcChannel.InvokeOneWayAsync<TRequest, TStreams>(RpcMethodDescriptor method, in TRequest request,
            IRpcCodec<TRequest> codec, in TStreams streams, SharpLinkMetadata? metadata, CancellationToken ct)
            => throw NotUnary();

        ValueTask<TResponse> IRpcChannel.InvokeClientStreamingAsync<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
            in TRequest request, IRpcCodec<TRequest> codec, IRpcCodec<TResponse> responseCodec, in TStreams streams,
            SharpLinkMetadata? metadata, CancellationToken ct) => throw NotUnary();

        IAsyncEnumerable<TResponse> IRpcChannel.InvokeServerStreamingAsync<TRequest, TResponse>(RpcMethodDescriptor method,
            in TRequest request, IRpcCodec<TRequest> codec, IRpcCodec<TResponse> responseCodec,
            SharpLinkMetadata? metadata, CancellationToken ct) => throw NotUnary();

        IAsyncEnumerable<TResponse> IRpcChannel.InvokeDuplexStreamingAsync<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
            in TRequest request, IRpcCodec<TRequest> codec, IRpcCodec<TResponse> responseCodec, in TStreams streams,
            SharpLinkMetadata? metadata, CancellationToken ct) => throw NotUnary();

        private static NotSupportedException NotUnary()
            => new("This private generated receiver supports unary-only contracts.");
    }
}
