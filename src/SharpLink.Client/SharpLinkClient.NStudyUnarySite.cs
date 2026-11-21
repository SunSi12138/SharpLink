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
        private readonly Lock _prepareGate = new();
        private NStudyPreparedUnary? _prepared;
        private PrepareOperation? _preparation;

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
        internal Func<NStudyAck, CancellationToken, ValueTask>? BeforePublishingPreparationForTests { get; set; }

        internal ValueTask<NStudyAck> PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(timeout, SharpLinkTimer.MaximumDelay);
            cancellationToken.ThrowIfCancellationRequested();
            var deadline = RpcDeadline.Create(timeout, _client._runtimeContext.TimeProvider);
            EnsureLocalReady();
            var connection = _client.RequireSingleNStudyConnection();
            if (Volatile.Read(ref _prepared) is { } current && current.IsReadyOn(connection))
                return ValueTask.FromResult(ConfirmWaiter(current, connection, deadline, cancellationToken));

            return AwaitPreparationAsync(connection, deadline, cancellationToken);
        }

        private async ValueTask<NStudyAck> AwaitPreparationAsync(ClientConnection connection, RpcDeadline deadline,
            CancellationToken cancellationToken)
        {
            _client.ReserveNStudyPrepareWaiter();
            try
            {
                PrepareOperation operation;
                var start = false;
                NStudyPreparedUnary? alreadyReady = null;
                lock (_prepareGate)
                {
                    if (_prepared is { } current && current.IsReadyOn(connection))
                    {
                        alreadyReady = current;
                        operation = null!;
                    }
                    else if (_preparation is { } pending)
                    {
                        if (!ReferenceEquals(pending.Connection, connection))
                            throw NStudyPreparedUnary.Unavailable();
                        operation = pending;
                    }
                    else
                    {
                        operation = _preparation = new PrepareOperation(connection);
                        start = true;
                    }
                }
                if (alreadyReady is not null)
                    return ConfirmWaiter(alreadyReady, connection, deadline, cancellationToken);

                if (start)
                    StartPreparation(operation);
                cancellationToken.ThrowIfCancellationRequested();
                var clock = _client._runtimeContext.TimeProvider;
                var remaining = deadline.GetRemaining(clock);
                if (remaining <= TimeSpan.Zero)
                    throw CreateDeadlineExceededException();
                NStudyPreparedUnary prepared;
                try
                {
                    prepared = await operation.Completion.Task.WaitAsync(remaining, clock, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException) { throw CreateDeadlineExceededException(); }
                return ConfirmWaiter(prepared, connection, deadline, cancellationToken);
            }
            finally { _client.ReturnNStudyPrepareWaiter(); }
        }

        private NStudyAck ConfirmWaiter(NStudyPreparedUnary prepared, ClientConnection connection, RpcDeadline deadline,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.IsExpired(_client._runtimeContext.TimeProvider))
                throw CreateDeadlineExceededException();
            EnsureLocalReady();
            if (!prepared.IsReadyOn(connection) || !connection.CanAcceptCalls ||
                !ReferenceEquals(_client.RequireSingleNStudyConnection(), connection))
                throw NStudyPreparedUnary.Unavailable();
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
                    _client.TrackFrameworkTask(operation.Completion.Task, "NStudyUnaryPrepare",
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
            NStudyPreparedUnary? ready = null;
            Exception? failure = null;
            try
            {
                var connection = operation.Connection;
                var session = connection.Session;
                var contract = _registration.Descriptor.ContractId;
                var clock = _client._runtimeContext.TimeProvider;
                var deadline = RpcDeadline.Create(RpcSession.NStudyTimeout, clock);
                using var timer = new CancellationTokenSource(RpcSession.NStudyTimeout, clock);
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(timer.Token,
                    _client._shutdownCts.Token, session.LifetimeToken);
                var ct = budget.Token;
                var bound = default(NStudyAck);
                try
                {
                    EnsureLocalReady();
                    _client.ValidateRemoteContractAssembly(_registration);
                    bound = await BindNStudyContractOnAsync(connection, contract, _hash, default, ct).ConfigureAwait(false);
                    if (BeforePublishingPreparationForTests is { } beforePublish)
                        await beforePublish(bound, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (deadline.IsExpired(clock))
                        throw CreateDeadlineExceededException();
                    EnsureLocalReady();
                    var route = session.CaptureNStudyRoute(contract);
                    if (route.Token != bound.BindingId || route.Target != bound.Target || route.Hash != _hash ||
                        !route.IsOpenOn(session) || !connection.CanAcceptCalls ||
                        !ReferenceEquals(_client.RequireSingleNStudyConnection(), connection))
                        throw NStudyPreparedUnary.Unavailable();
                    ready = new NStudyPreparedUnary(connection, route, bound);
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

        private void FinishPreparation(PrepareOperation operation, NStudyPreparedUnary? ready, Exception? error)
        {
            lock (_prepareGate)
            {
                if (ready is not null)
                    Volatile.Write(ref _prepared, ready);
                _preparation = null;
            }
            if (error is not null)
                operation.Completion.TrySetException(error);
            else
                operation.Completion.TrySetResult(ready!);
        }

        private sealed class PrepareOperation(ClientConnection connection)
        {
            internal ClientConnection Connection { get; } = connection;
            internal TaskCompletionSource<NStudyPreparedUnary> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
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
