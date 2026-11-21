namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    // Private checkpoint: the existing ABI supplies the real generated proxy.
    // Cold selection requires every method to work on this receiver.
    internal NStudyContractSite<T> CreateNStudyContractSite<T>(SharpLinkMetadata? metadata = null) where T : IService
    {
        if (_cluster is not null)
            throw new NotSupportedException("The private contract site requires a single-endpoint client.");
        if (!Volatile.Read(ref _proxies).TryGetValue(typeof(T), out var registration))
            throw new InvalidOperationException($"Proxy for service interface {typeof(T).FullName} is not registered.");
        if (registration.Descriptor.Methods.Any(method =>
                method.Kind is not (RpcMethodKind.Unary or RpcMethodKind.OneWay or RpcMethodKind.ServerStreaming or
                    RpcMethodKind.ClientStreaming or RpcMethodKind.DuplexStreaming) ||
                method.Invocation is not { } invocation || invocation.Kind != method.Kind ||
                invocation.ContractId != registration.Descriptor.ContractId || invocation.MethodId != method.MethodId))
            throw new NotSupportedException("The private site requires matching generated method facts.");
        return new NStudyContractSite<T>(this, registration, FindOwningManifest(registration).RpcAssemblyHash, metadata);
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

        internal static SharpLinkException Unavailable()
            => new(SharpLinkErrorCode.Unavailable, "The private RPC site needs a current prepared Session.");
    }

    internal sealed class NStudyContractSite<T> : IRpcChannel where T : IService
    {
        private readonly SharpLinkClient _client;
        private readonly ClientProxyRegistration _registration;
        private readonly RpcHash128 _hash;
        private readonly SharpLinkMetadata? _metadata;
        private readonly Lock _prepareGate = new();
        private NStudyPreparedSite? _prepared;
        private PrepareOperation? _preparation;

        internal NStudyContractSite(SharpLinkClient client, ClientProxyRegistration registration,
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
                NStudyPreparedSite? alreadyReady = null;
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
                            throw NStudyPreparedSite.Unavailable();
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
                NStudyPreparedSite prepared;
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

        private NStudyAck ConfirmWaiter(NStudyPreparedSite prepared, ClientConnection connection, RpcDeadline deadline,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.IsExpired(_client._runtimeContext.TimeProvider))
                throw CreateDeadlineExceededException();
            EnsureLocalReady();
            if (!prepared.IsReadyOn(connection) || !connection.CanAcceptCalls ||
                !ReferenceEquals(_client.RequireSingleNStudyConnection(), connection))
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
            internal TaskCompletionSource<NStudyPreparedSite> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private void EnsureLocalReady()
        {
            if (_client._shutdownCts.IsCancellationRequested || _client.State == SharpLinkConnectionState.Stopped)
                throw CreateConnectionClosedException("Client is not accepting new calls.");
            if (_registration.Module is { State: not SharpLinkDynamicModuleState.Running })
                throw NStudyPreparedSite.Unavailable();
        }

        internal ManualUnary BindUnary(long methodId) => new(this, FindMethod(methodId, RpcMethodKind.Unary));
        internal ManualOneWay BindOneWay(long methodId) => new(this, FindMethod(methodId, RpcMethodKind.OneWay));
        internal ManualServerStream BindServerStream(long methodId) => new(this, FindMethod(methodId, RpcMethodKind.ServerStreaming));
        internal ManualClientStream BindClientStream(long methodId) => new(this, FindMethod(methodId, RpcMethodKind.ClientStreaming));
        internal ManualDuplex BindDuplex(long methodId) => new(this, FindMethod(methodId, RpcMethodKind.DuplexStreaming));

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

        internal sealed class ManualUnary(NStudyContractSite<T> site, RpcMethodDescriptor method)
        {
            internal ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(in TRequest request,
                IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
                SharpLinkMetadata? metadata = null, CancellationToken cancellationToken = default)
                => site.InvokeUnary(method, request, requestCodec, responseCodec, metadata, cancellationToken);
        }

        internal sealed class ManualOneWay(NStudyContractSite<T> site, RpcMethodDescriptor method)
        {
            internal ValueTask InvokeAsync<TRequest, TStreams>(in TRequest request,
                IRpcCodec<TRequest> requestCodec, in TStreams streams,
                SharpLinkMetadata? metadata = null, CancellationToken cancellationToken = default)
                where TStreams : struct, IRpcClientStreamWriter
                => site.InvokeOneWay(method, request, requestCodec, streams, metadata, cancellationToken);
        }

        internal sealed class ManualServerStream(NStudyContractSite<T> site, RpcMethodDescriptor method)
        {
            internal IAsyncEnumerable<TResponse> Invoke<TRequest, TResponse>(in TRequest request,
                IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
                SharpLinkMetadata? metadata = null, CancellationToken cancellationToken = default)
                => site.InvokeServerStream(method, request, requestCodec, responseCodec, metadata, cancellationToken);
        }

        internal sealed class ManualClientStream(NStudyContractSite<T> site, RpcMethodDescriptor method)
        {
            internal ValueTask<TResponse> InvokeAsync<TRequest, TResponse, TStreams>(in TRequest request,
                IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec, in TStreams streams,
                SharpLinkMetadata? metadata = null, CancellationToken cancellationToken = default)
                where TStreams : struct, IRpcClientStreamWriter
                => site.InvokeClientStream(method, request, requestCodec, responseCodec, streams, metadata, cancellationToken);
        }

        internal sealed class ManualDuplex(NStudyContractSite<T> site, RpcMethodDescriptor method)
        {
            internal IAsyncEnumerable<TResponse> Invoke<TRequest, TResponse, TStreams>(in TRequest request,
                IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec, in TStreams streams,
                SharpLinkMetadata? metadata = null, CancellationToken cancellationToken = default)
                where TStreams : struct, IRpcClientStreamWriter
                => site.InvokeDuplex(method, request, requestCodec, responseCodec, streams, metadata, cancellationToken);
        }

        ValueTask<TResponse> IRpcChannel.InvokeUnaryAsync<TRequest, TResponse>(RpcMethodDescriptor method,
            in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
            SharpLinkMetadata? metadata, CancellationToken cancellationToken)
            => InvokeUnary(method, request, requestCodec, responseCodec, metadata, cancellationToken);

        private ValueTask<TResponse> InvokeUnary<TRequest, TResponse>(RpcMethodDescriptor method,
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

        ValueTask IRpcChannel.InvokeOneWayAsync<TRequest, TStreams>(RpcMethodDescriptor method, in TRequest request,
            IRpcCodec<TRequest> codec, in TStreams streams, SharpLinkMetadata? metadata, CancellationToken ct)
            => InvokeOneWay(method, request, codec, streams, metadata, ct);

        private ValueTask InvokeOneWay<TRequest, TStreams>(RpcMethodDescriptor method, in TRequest request,
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
            return Volatile.Read(ref _prepared) ?? throw NStudyPreparedSite.Unavailable();
        }

        ValueTask<TResponse> IRpcChannel.InvokeClientStreamingAsync<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
            in TRequest request, IRpcCodec<TRequest> codec, IRpcCodec<TResponse> responseCodec, in TStreams streams,
            SharpLinkMetadata? metadata, CancellationToken ct)
            => InvokeClientStream(method, request, codec, responseCodec, streams, metadata, ct);

        private ValueTask<TResponse> InvokeClientStream<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
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

        IAsyncEnumerable<TResponse> IRpcChannel.InvokeServerStreamingAsync<TRequest, TResponse>(RpcMethodDescriptor method,
            in TRequest request, IRpcCodec<TRequest> codec, IRpcCodec<TResponse> responseCodec,
            SharpLinkMetadata? metadata, CancellationToken ct)
            => InvokeServerStream(method, request, codec, responseCodec, metadata, ct);

        private IAsyncEnumerable<TResponse> InvokeServerStream<TRequest, TResponse>(RpcMethodDescriptor method,
            in TRequest request, IRpcCodec<TRequest> codec, IRpcCodec<TResponse> responseCodec,
            SharpLinkMetadata? metadata, CancellationToken ct)
        {
            var prepared = RequirePrepared(method, RpcMethodKind.ServerStreaming);
            return SharpLinkModuleRpcChannel.InvokeServerStreamingDeferred(_client, _registration.Module, method, request,
                codec, responseCodec, metadata ?? _metadata, ct, prepared);
        }

        IAsyncEnumerable<TResponse> IRpcChannel.InvokeDuplexStreamingAsync<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
            in TRequest request, IRpcCodec<TRequest> codec, IRpcCodec<TResponse> responseCodec, in TStreams streams,
            SharpLinkMetadata? metadata, CancellationToken ct)
            => InvokeDuplex(method, request, codec, responseCodec, streams, metadata, ct);

        private IAsyncEnumerable<TResponse> InvokeDuplex<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
            in TRequest request, IRpcCodec<TRequest> codec, IRpcCodec<TResponse> responseCodec, in TStreams streams,
            SharpLinkMetadata? metadata, CancellationToken ct)
            where TStreams : struct, IRpcClientStreamWriter
        {
            var prepared = RequirePrepared(method, RpcMethodKind.DuplexStreaming);
            return SharpLinkModuleRpcChannel.InvokeDuplexStreamingDeferred(_client, _registration.Module, method, request,
                codec, responseCodec, streams, metadata ?? _metadata, ct, prepared);
        }
    }
}
