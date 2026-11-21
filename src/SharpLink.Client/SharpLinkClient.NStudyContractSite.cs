using System.Reflection;

namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    public RpcProxy<T> CreateProxy<T>(RpcProxyOptions? options = null) where T : IService
    {
        var metadata = ValidateNStudyFactory(options);
        var registration = _assemblyRegistry.ResolveNStudyLocalRegistration(typeof(T));
        var site = new NStudyContractSite<T>(this, registration, metadata, registration.NStudyPreparation.Get());
        return new(site.Service, site);
    }

    public RpcLocalContract GetLocalContract<T>() where T : IService
    {
        EnsureNStudyFactoryAvailable();
        return _assemblyRegistry.CaptureNStudyLocalContract(typeof(T));
    }

    public RpcLocalContract GetLocalContract(Assembly registeredAssembly, long contractId)
    {
        ArgumentNullException.ThrowIfNull(registeredAssembly);
        EnsureNStudyFactoryAvailable();
        return _assemblyRegistry.CaptureNStudyLocalContract(registeredAssembly, contractId);
    }

    public IRpcContractChannel CreateContractChannel(RpcLocalContract local, RpcProxyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(local);
        var metadata = ValidateNStudyFactory(options);
        var registration = _assemblyRegistry.ResolveNStudyLocalContract(local);
        return new NStudyContractSite(this, registration, metadata, registration.NStudyPreparation.Get());
    }

    private SharpLinkMetadata? ValidateNStudyFactory(RpcProxyOptions? options)
    {
        var metadata = options?.Metadata;
        if (metadata is { Count: > 0 } && ProtocolV2PayloadCodec.GetMetadataPayloadLength(metadata) > _protocolOptions.MaxMetadataBytes)
            throw new ArgumentOutOfRangeException(nameof(options), "Proxy metadata exceeds the client size limit.");
        EnsureNStudyFactoryAvailable();
        return metadata;
    }

    private void EnsureNStudyFactoryAvailable()
    {
        if (Volatile.Read(ref _stopStarted) != 0 || State is SharpLinkConnectionState.Faulted)
            throw CreateConnectionClosedException("Client is not accepting new contract sites.");
    }

    // Private checkpoint: the existing ABI supplies the real generated proxy.
    // Cold selection requires every method to work on this receiver.
    internal NStudyContractSite<T> CreateNStudyContractSite<T>(SharpLinkMetadata? metadata = null) where T : IService
    {
        EnsureNStudyFactoryAvailable();
        if (!Volatile.Read(ref _proxies).TryGetValue(typeof(T), out var registration))
            throw new InvalidOperationException($"Proxy for service interface {typeof(T).FullName} is not registered.");
        return new NStudyContractSite<T>(this, registration, metadata, new());
    }

    internal sealed class NStudyPreparedSite(ClientConnection connection, NStudyClientRoute route, NStudyAck ack)
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

        internal bool Matches(NStudyObservedTarget? target)
            => target is not { } observed ||
                Ack.Authority == observed.Authority && Ack.Target == observed.Target;

        internal static SharpLinkException Unavailable()
            => new(SharpLinkErrorCode.Unavailable, "The private RPC site needs a current prepared Session.");
    }

    internal sealed class NStudyPreparationCell
    {
        private NStudyContractPreparation? _state;
        internal NStudyContractPreparation Get() => LazyInitializer.EnsureInitialized(ref _state);
    }

    internal sealed class NStudyContractPreparation
    {
        internal readonly Lock Gate = new();
        internal NStudyPreparedSite? Ready;
        internal PrepareOperation? InFlight;
    }

    internal sealed class PrepareOperation(ClientConnection? connection, NStudyObservedTarget? observation)
    {
        internal ClientConnection? FixedConnection { get; } = connection;
        internal NStudyObservedTarget? Observation { get; } = observation;
        internal TaskCompletionSource<NStudyPreparedSite> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal class NStudyContractSite : IRpcContractChannel
    {
        private readonly SharpLinkClient _client;
        private readonly ClientProxyRegistration _registration;
        private readonly RpcHash128 _hash;
        private readonly SharpLinkMetadata? _metadata;
        private readonly NStudyContractPreparation _preparation;
        private readonly FrozenDictionary<long, RpcMethodDescriptor>? _manualMethods;

        internal NStudyContractSite(SharpLinkClient client, ClientProxyRegistration registration,
            SharpLinkMetadata? metadata, NStudyContractPreparation preparation, bool manual = true)
        {
            _client = client;
            _registration = registration;
            _hash = registration.NStudyAssemblyHash;
            _metadata = metadata;
            _preparation = preparation;
            var contract = registration.Descriptor;
            if (contract.Methods.Any(method =>
                    method.Kind is not (RpcMethodKind.Unary or RpcMethodKind.OneWay or RpcMethodKind.ServerStreaming or
                        RpcMethodKind.ClientStreaming or RpcMethodKind.DuplexStreaming) ||
                    method.Invocation is not { } invocation || invocation.Kind != method.Kind ||
                    invocation.ContractId != contract.ContractId || invocation.MethodId != method.MethodId))
                throw new NotSupportedException("The private site requires matching generated method facts.");
            if (manual)
                _manualMethods = contract.Methods.ToFrozenDictionary(method => method.MethodId, method => method.Invocation!.Value);
        }

        public IRpcRuntimeContext RuntimeContext => _client.RuntimeContext;
        internal IRpcCodec<TValue> GetCodec<TValue>() => _registration.Codecs.GetCodec<TValue>();
        internal Func<NStudyAck, CancellationToken, ValueTask>? BeforePublishingPreparationForTests { get; set; }

        internal ValueTask<NStudyAck> PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken)
            => PrepareAsync(timeout, cancellationToken, null);

        async ValueTask IRpcContractChannel.PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken)
            => _ = await PrepareAsync(timeout, cancellationToken).ConfigureAwait(false);

        internal ValueTask<NStudyAck> PrepareExactAsync(NStudyObservedTarget observed, TimeSpan timeout,
            CancellationToken cancellationToken)
            => PrepareAsync(timeout, cancellationToken, observed);

        private ValueTask<NStudyAck> PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken,
            NStudyObservedTarget? observed)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(timeout, SharpLinkTimer.MaximumDelay);
            cancellationToken.ThrowIfCancellationRequested();
            var deadline = RpcDeadline.Create(timeout, _client._runtimeContext.TimeProvider);
            EnsureLocalReady();
            // Only the shared operation leader consults a cluster endpoint selector. Reusing a
            // live Ready site must not consult it again, even if the ranking policy changes.
            var connection = _client._cluster is null || observed is not null
                ? _client.RequireSingleNStudyConnection() : null;
            if (observed is { } exact && (exact.Contract != _registration.Descriptor.ContractId ||
                exact.Hash != _hash || exact.Target == default || !connection!.Session.HasNStudyAuthority(exact.Authority)))
                throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition,
                    "The observed registration does not belong to this contract and physical Session.");
            if (Volatile.Read(ref _preparation.Ready) is { } current && CanReuse(current, connection, observed))
                return ValueTask.FromResult(ConfirmWaiter(current, deadline, cancellationToken));

            return AwaitPreparationAsync(connection, deadline, cancellationToken, observed);
        }

        private async ValueTask<NStudyAck> AwaitPreparationAsync(ClientConnection? connection, RpcDeadline deadline,
            CancellationToken cancellationToken, NStudyObservedTarget? observed)
        {
            _client.ReserveNStudyPrepareWaiter();
            try
            {
                PrepareOperation operation;
                var start = false;
                NStudyPreparedSite? alreadyReady = null;
                lock (_preparation.Gate)
                {
                    if (_preparation.Ready is { } current && CanReuse(current, connection, observed))
                    {
                        alreadyReady = current;
                        operation = null!;
                    }
                    else if (_preparation.InFlight is { } pending)
                    {
                        if (!ReferenceEquals(pending.FixedConnection, connection) || pending.Observation != observed)
                            throw NStudyPreparedSite.Unavailable();
                        operation = pending;
                    }
                    else
                    {
                        operation = _preparation.InFlight = new PrepareOperation(connection, observed);
                        start = true;
                    }
                }
                if (alreadyReady is not null)
                    return ConfirmWaiter(alreadyReady, deadline, cancellationToken);

                if (start)
                    StartPreparation(operation);
                cancellationToken.ThrowIfCancellationRequested();
                var clock = _client._runtimeContext.TimeProvider;
                var remaining = deadline.GetRemaining(clock);
                if (remaining <= TimeSpan.Zero)
                    throw CreateDeadlineExceededException();
                NStudyPreparedSite prepared;
                try
                {
                    prepared = await operation.Completion.Task.WaitAsync(remaining, clock, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException) { throw CreateDeadlineExceededException(); }
                return ConfirmWaiter(prepared, deadline, cancellationToken);
            }
            finally { _client.ReturnNStudyPrepareWaiter(); }
        }

        private static bool CanReuse(NStudyPreparedSite prepared, ClientConnection? expected, NStudyObservedTarget? observed) =>
            prepared.Matches(observed) && prepared.TryGetConnection(out var connection) &&
            (expected is null || ReferenceEquals(expected, connection)) && connection.CanAcceptCalls && prepared.IsReadyOn(connection);

        private NStudyAck ConfirmWaiter(NStudyPreparedSite prepared, RpcDeadline deadline,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.IsExpired(_client._runtimeContext.TimeProvider))
                throw CreateDeadlineExceededException();
            EnsureLocalReady();
            if (!prepared.TryGetConnection(out var connection) || !prepared.IsReadyOn(connection) ||
                !_client.IsNStudyPreparationConnectionCurrent(connection))
                throw NStudyPreparedSite.Unavailable();
            return prepared.Ack;
        }

        private void StartPreparation(PrepareOperation operation)
        {
            // Register before sending so Stop can drain the operation even after all waiters leave.
            bool registered;
            lock (_client._poolGate)
            {
                registered = !_client._poolStopping;
                if (registered)
                    _client.TrackFrameworkTask(operation.Completion.Task, "NStudySitePrepare",
                        TaskObservationMode.ExternallyObserved);
            }
            if (registered)
            {
                _ = RunPreparationAsync(operation);
                return;
            }
            FinishPreparation(operation, null, CreateConnectionClosedException("Client is not accepting Prepare."));
            _ = operation.Completion.Task.Exception;
        }

        private async Task RunPreparationAsync(PrepareOperation operation)
        {
            NStudyPreparedSite? ready = null;
            Exception? failure = null;
            try
            {
                var contract = _registration.Descriptor.ContractId;
                var clock = _client._runtimeContext.TimeProvider;
                var deadline = RpcDeadline.Create(RpcSession.NStudyTimeout, clock);
                using var timer = new CancellationTokenSource(RpcSession.NStudyTimeout, clock);
                EnsureLocalReady();
                var connection = operation.FixedConnection ?? _client._cluster!.GetReadyConnection(
                    method: null, retrySelection: null, attemptOutcome: null, reserveCall: false);
                var session = connection.Session;
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(timer.Token,
                    _client._shutdownCts.Token, session.LifetimeToken);
                var ct = budget.Token;
                var bound = default(NStudyAck);
                try
                {
                    EnsureLocalReady();
                    // Query authority and local hash were checked before starting. An exact Bind
                    // still validates current remote registration under the peer's registry gate;
                    // a lagging unsolicited manifest is not the authority for this path.
                    ct.ThrowIfCancellationRequested();
                    if (!_client.IsNStudyPreparationConnectionCurrent(connection))
                        throw NStudyPreparedSite.Unavailable();
                    if (operation.Observation is null)
                        _client.ValidateNStudyRemoteContractOn(_registration, session);
                    bound = await BindNStudyContractOnAsync(connection, contract, _hash,
                        operation.Observation?.Target ?? default, ct).ConfigureAwait(false);
                    if (BeforePublishingPreparationForTests is { } beforePublish)
                        await beforePublish(bound, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (deadline.IsExpired(clock))
                        throw CreateDeadlineExceededException();
                    EnsureLocalReady();
                    var route = session.CaptureNStudyRoute(contract);
                    if (route.Token != bound.BindingId || route.Target != bound.Target || route.Hash != _hash ||
                        !route.IsOpenOn(session) || !_client.IsNStudyPreparationConnectionCurrent(connection))
                        throw NStudyPreparedSite.Unavailable();
                    ready = new NStudyPreparedSite(connection, route, bound);
                }
                catch (Exception error)
                {
                    if (bound.BindingId != 0 && session.HasNStudyBinding(contract, bound.BindingId))
                    {
                        try { await session.ReleaseNStudyBindingAsync(contract, ct, bound.BindingId).ConfigureAwait(false); }
                        catch (Exception cleanup) when (cleanup is not OutOfMemoryException and not StackOverflowException)
                        {
                            if (session.HasNStudyBinding(contract, bound.BindingId))
                                session.NotifyDisconnected(cleanup);
                        }
                    }
                    if (error is OperationCanceledException && timer.IsCancellationRequested)
                        throw CreateDeadlineExceededException();
                    if (error is OperationCanceledException && _client._shutdownCts.IsCancellationRequested)
                        throw CreateConnectionClosedException("Client is not accepting Prepare.");
                    throw;
                }
            }
            catch (Exception error) { failure = error; }
            finally { FinishPreparation(operation, ready, failure); }
        }

        private void FinishPreparation(PrepareOperation operation, NStudyPreparedSite? ready, Exception? error)
        {
            lock (_preparation.Gate)
            {
                if (ready is not null)
                    Volatile.Write(ref _preparation.Ready, ready);
                _preparation.InFlight = null;
            }
            if (error is not null)
                operation.Completion.TrySetException(error);
            else
                operation.Completion.TrySetResult(ready!);
        }

        private void EnsureLocalReady()
        {
            if (_client._shutdownCts.IsCancellationRequested || _client.State == SharpLinkConnectionState.Stopped)
                throw CreateConnectionClosedException("Client is not accepting new calls.");
            if (_registration.Module is { State: not SharpLinkDynamicModuleState.Running })
                throw NStudyPreparedSite.Unavailable();
        }

        internal ManualUnary BindUnary(long methodId) => new(this, FindMethod(methodId, RpcMethodKind.Unary));

        private RpcMethodDescriptor FindMethod(long methodId, RpcMethodKind kind)
        {
            foreach (var generated in _registration.Descriptor.Methods)
            {
                if (generated.MethodId == methodId && generated.Invocation is { } method &&
                    method.Kind == kind)
                    return method;
            }
            throw new ArgumentOutOfRangeException(nameof(methodId), $"No such local generated {kind} method.");
        }

        internal sealed class ManualUnary(NStudyContractSite site, RpcMethodDescriptor method)
        {
            internal ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(in TRequest request,
                IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
                SharpLinkMetadata? metadata = null, CancellationToken cancellationToken = default)
                => site.InvokeUnary(method, request, requestCodec, responseCodec, metadata, cancellationToken);
        }

        private RpcMethodDescriptor ValidateManualMethod(RpcMethodDescriptor method)
        {
            if (_manualMethods is null || !_manualMethods.TryGetValue(method.MethodId, out var canonical) || canonical != method)
                throw new ArgumentException("The method does not match the local contract's generated declaration.", nameof(method));
            return canonical;
        }

        ValueTask<TResponse> IRpcContractChannel.InvokeUnaryAsync<TRequest, TResponse>(RpcMethodDescriptor method,
            in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
            SharpLinkMetadata? metadata, CancellationToken cancellationToken)
            => InvokeUnary(ValidateManualMethod(method), request, requestCodec, responseCodec, metadata, cancellationToken);

        protected ValueTask<TResponse> InvokeUnary<TRequest, TResponse>(RpcMethodDescriptor method,
            in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
            SharpLinkMetadata? metadata, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prepared = RequirePrepared(method, RpcMethodKind.Unary);
            return _registration.Module is { } module
                ? SharpLinkModuleRpcChannel.InvokeUnaryWithModule(_client, module, method, request, requestCodec,
                    responseCodec, metadata ?? _metadata, cancellationToken, prepared)
                : _client.InvokeUnary(method, request, requestCodec, responseCodec, metadata ?? _metadata,
                    cancellationToken, prepared);
        }

        ValueTask IRpcContractChannel.InvokeOneWayAsync<TRequest, TStreams>(RpcMethodDescriptor method, in TRequest request,
            IRpcCodec<TRequest> codec, in TStreams streams, SharpLinkMetadata? metadata, CancellationToken ct)
            => InvokeOneWay(ValidateManualMethod(method), request, codec, streams, metadata, ct);

        protected ValueTask InvokeOneWay<TRequest, TStreams>(RpcMethodDescriptor method, in TRequest request,
            IRpcCodec<TRequest> codec, in TStreams streams, SharpLinkMetadata? metadata, CancellationToken ct)
            where TStreams : struct, IRpcClientStreamWriter
        {
            ct.ThrowIfCancellationRequested();
            var prepared = RequirePrepared(method, RpcMethodKind.OneWay);
            return _registration.Module is { } module
                ? SharpLinkModuleRpcChannel.InvokeOneWayWithModule(_client, module, method, request, codec, streams,
                    metadata ?? _metadata, ct, prepared)
                : _client.InvokeOneWay(method, request, codec, streams, metadata ?? _metadata, ct, prepared);
        }

        private NStudyPreparedSite RequirePrepared(RpcMethodDescriptor method, RpcMethodKind kind)
        {
            if (method.ContractId != _registration.Descriptor.ContractId || method.Kind != kind)
                throw new InvalidOperationException("The method does not use this private contract receiver.");
            return Volatile.Read(ref _preparation.Ready) ?? throw NStudyPreparedSite.Unavailable();
        }

        ValueTask<TResponse> IRpcContractChannel.InvokeClientStreamingAsync<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
            in TRequest request, IRpcCodec<TRequest> codec, IRpcCodec<TResponse> responseCodec, in TStreams streams,
            SharpLinkMetadata? metadata, CancellationToken ct)
            => InvokeClientStream(ValidateManualMethod(method), request, codec, responseCodec, streams, metadata, ct);

        protected ValueTask<TResponse> InvokeClientStream<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
            in TRequest request, IRpcCodec<TRequest> codec, IRpcCodec<TResponse> responseCodec, in TStreams streams,
            SharpLinkMetadata? metadata, CancellationToken ct)
            where TStreams : struct, IRpcClientStreamWriter
        {
            ct.ThrowIfCancellationRequested();
            var prepared = RequirePrepared(method, RpcMethodKind.ClientStreaming);
            return _registration.Module is { } module
                ? SharpLinkModuleRpcChannel.InvokeClientStreamingWithModule(_client, module, method, request, codec,
                    responseCodec, streams, metadata ?? _metadata, ct, prepared)
                : _client.InvokeClientStreaming(method, request, codec, responseCodec, streams, metadata ?? _metadata,
                    ct, prepared, null);
        }

        IAsyncEnumerable<TResponse> IRpcContractChannel.InvokeServerStreamingAsync<TRequest, TResponse>(RpcMethodDescriptor method,
            in TRequest request, IRpcCodec<TRequest> codec, IRpcCodec<TResponse> responseCodec,
            SharpLinkMetadata? metadata, CancellationToken ct)
            => InvokeServerStream(ValidateManualMethod(method), request, codec, responseCodec, metadata, ct);

        protected IAsyncEnumerable<TResponse> InvokeServerStream<TRequest, TResponse>(RpcMethodDescriptor method,
            in TRequest request, IRpcCodec<TRequest> codec, IRpcCodec<TResponse> responseCodec,
            SharpLinkMetadata? metadata, CancellationToken ct)
        {
            var prepared = RequirePrepared(method, RpcMethodKind.ServerStreaming);
            return SharpLinkModuleRpcChannel.InvokeServerStreamingDeferred(_client, _registration.Module, method, request,
                codec, responseCodec, metadata ?? _metadata, ct, prepared);
        }

        IAsyncEnumerable<TResponse> IRpcContractChannel.InvokeDuplexStreamingAsync<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
            in TRequest request, IRpcCodec<TRequest> codec, IRpcCodec<TResponse> responseCodec, in TStreams streams,
            SharpLinkMetadata? metadata, CancellationToken ct)
            => InvokeDuplex(ValidateManualMethod(method), request, codec, responseCodec, streams, metadata, ct);

        protected IAsyncEnumerable<TResponse> InvokeDuplex<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
            in TRequest request, IRpcCodec<TRequest> codec, IRpcCodec<TResponse> responseCodec, in TStreams streams,
            SharpLinkMetadata? metadata, CancellationToken ct)
            where TStreams : struct, IRpcClientStreamWriter
        {
            var prepared = RequirePrepared(method, RpcMethodKind.DuplexStreaming);
            return SharpLinkModuleRpcChannel.InvokeDuplexStreamingDeferred(_client, _registration.Module, method, request,
                codec, responseCodec, streams, metadata ?? _metadata, ct, prepared);
        }
    }
    // The private generated ABI still consumes IRpcChannel until the atomic cutover.
    // Both entries use the same receiver implementation and local-generation Prepare state.
    internal sealed class NStudyContractSite<T> : NStudyContractSite, IRpcChannel where T : IService
    {
        internal NStudyContractSite(SharpLinkClient client, ClientProxyRegistration registration,
            SharpLinkMetadata? metadata, NStudyContractPreparation preparation)
            : base(client, registration, metadata, preparation, manual: false)
            => Service = (T)registration.Descriptor.ProxyFactory(this, registration.Codecs);

        internal T Service { get; }

        ValueTask<TResponse> IRpcChannel.InvokeUnaryAsync<TRequest, TResponse>(RpcMethodDescriptor method,
            in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
            SharpLinkMetadata? metadata, CancellationToken cancellationToken)
            => InvokeUnary(method, request, requestCodec, responseCodec, metadata, cancellationToken);

        ValueTask IRpcChannel.InvokeOneWayAsync<TRequest, TStreams>(RpcMethodDescriptor method, in TRequest request,
            IRpcCodec<TRequest> codec, in TStreams streams, SharpLinkMetadata? metadata, CancellationToken ct)
            => InvokeOneWay(method, request, codec, streams, metadata, ct);

        ValueTask<TResponse> IRpcChannel.InvokeClientStreamingAsync<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
            in TRequest request, IRpcCodec<TRequest> codec, IRpcCodec<TResponse> responseCodec, in TStreams streams,
            SharpLinkMetadata? metadata, CancellationToken ct)
            => InvokeClientStream(method, request, codec, responseCodec, streams, metadata, ct);

        IAsyncEnumerable<TResponse> IRpcChannel.InvokeServerStreamingAsync<TRequest, TResponse>(RpcMethodDescriptor method,
            in TRequest request, IRpcCodec<TRequest> codec, IRpcCodec<TResponse> responseCodec,
            SharpLinkMetadata? metadata, CancellationToken ct)
            => InvokeServerStream(method, request, codec, responseCodec, metadata, ct);

        IAsyncEnumerable<TResponse> IRpcChannel.InvokeDuplexStreamingAsync<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
            in TRequest request, IRpcCodec<TRequest> codec, IRpcCodec<TResponse> responseCodec, in TStreams streams,
            SharpLinkMetadata? metadata, CancellationToken ct)
            => InvokeDuplex(method, request, codec, responseCodec, streams, metadata, ct);
    }
}
