namespace SharpLink.Server;

internal sealed partial class SharpLinkServer(
    IServerTransportListener transportListener,
    FrozenDictionary<long, ServiceRegistration> services,
    TimeSpan heartbeatCheckInterval,
    TimeSpan heartbeatTimeout,
    ILoggerFactory loggerFactory,
    ISharpLinkServerAuthenticator? authenticator = null,
    bool authenticationRequired = false,
    SharpLinkProtocolOptions? protocolOptions = null,
    SharpLinkRuntimeContext? runtimeContext = null,
    RpcSessionFlushOptions? rpcSessionFlushOptions = null,
    ISharpLinkServerInterceptor[]? serverInterceptors = null,
    IRpcExceptionMapper? exceptionMapper = null,
    IAsyncDisposable? ownedServiceProvider = null) : ISharpLinkServer
{
    private enum ServerState
    {
        Created,
        Starting,
        Running,
        Draining,
        Stopped,
        Faulted
    }

    private readonly SharpLinkRuntimeContext _runtimeContext = runtimeContext ?? new SharpLinkRuntimeContextBuilder().Build();
    private readonly ConcurrentDictionary<string, ServerConnectionState> _connections = [];
    private readonly ILogger _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory))).CreateLogger<SharpLinkServer>();
    private readonly ISharpLinkServerAuthenticator? _authenticator = authenticator;
    private readonly bool _authenticationRequired = authenticationRequired;
    private readonly CancellationTokenSource _acceptCts = new();
    private readonly CancellationTokenSource _forceStopCts = new();
    private readonly Lock _stateGate = new();
    private readonly Lock _frameworkTasksGate = new();
    private readonly HashSet<Task> _frameworkTasks = [];
    private readonly TaskCompletionSource<bool> _callsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _runTask;
    private Task? _stopTask;
    private int _state = (int)ServerState.Created;
    private readonly SharpLinkProtocolOptions _protocolOptions =
        (protocolOptions ?? runtimeContext?.Protocol ?? new SharpLinkProtocolOptions()).CloneValidated();
    private readonly int _maxConcurrentCallsPerConnection =
        (runtimeContext?.FlowControl.MaxConcurrentCallsPerConnection ?? 1024);
    private readonly RpcSessionFlushOptions? _rpcSessionFlushOptions = rpcSessionFlushOptions;
    private readonly ISharpLinkServerInterceptor[] _serverInterceptors =
        serverInterceptors is { Length: > 0 } ? [.. serverInterceptors] : [];
    private readonly IRpcExceptionMapper _exceptionMapper =
        exceptionMapper ?? new DefaultRpcExceptionMapper(includeDetails: false);
    private readonly IAsyncDisposable? _ownedServiceProvider = ownedServiceProvider;
    private int _servicesDisposed;
    private int _globalActiveCalls;
    private long _rejectedOneWayCalls;
    private readonly int _globalMaxConcurrentCalls = (int)Math.Min(
        (long)Environment.ProcessorCount * 1024,
        65_536L);

    public SharpLinkHealthStatus HealthStatus => CurrentState switch
    {
        ServerState.Running => SharpLinkHealthStatus.Ready,
        ServerState.Draining => SharpLinkHealthStatus.Draining,
        _ => SharpLinkHealthStatus.Unhealthy
    };

    public ValueTask DisposeAsync() => StopAsync(TimeSpan.Zero);

    public ValueTask StopAsync(
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(gracefulTimeout, TimeSpan.Zero);
        Task stopTask;
        lock (_stateGate)
        {
            _stopTask ??= StopCoreAsync(gracefulTimeout);
            stopTask = _stopTask;
        }

        return cancellationToken.CanBeCanceled
            ? new ValueTask(stopTask.WaitAsync(cancellationToken))
            : new ValueTask(stopTask);
    }

    private async Task StopCoreAsync(TimeSpan gracefulTimeout)
    {
        TransitionTo(ServerState.Draining);
        _acceptCts.Cancel();
        try
        {
            await transportListener.DisposeAsync().ConfigureAwait(false);
            foreach (var connection in _connections.Values)
            {
                connection.MarkDraining();
                try
                {
                    await connection.Session.SendGoAwayAsync(
                        connection.LastAcceptedRequestId,
                        SharpLinkErrorCode.Unavailable,
                        "Server is draining.").ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SharpLinkException or System.IO.IOException or ObjectDisposedException)
                {
                }
            }

            if (Volatile.Read(ref _globalActiveCalls) == 0)
                _callsDrained.TrySetResult(true);
            else if (gracefulTimeout > TimeSpan.Zero)
            {
                try
                {
                    await _callsDrained.Task.WaitAsync(gracefulTimeout).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                }
                catch (OperationCanceledException) when (_forceStopCts.IsCancellationRequested)
                {
                }
            }

            if (_callsDrained.Task.IsCompletedSuccessfully)
            {
                foreach (var connection in _connections.Values)
                {
                    try
                    {
                        await connection.Session.FlushSendQueueAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is SharpLinkException or System.IO.IOException or ObjectDisposedException)
                    {
                    }
                }
            }

            _forceStopCts.Cancel();
            await DisposeAllSessionsAsync().ConfigureAwait(false);
            await WaitForFrameworkTasksAsync().ConfigureAwait(false);
            await DisposeServicesAsync().ConfigureAwait(false);
            _acceptCts.Dispose();
            _forceStopCts.Dispose();
            TransitionTo(ServerState.Stopped);
        }
        catch
        {
            TransitionTo(ServerState.Faulted);
            await DisposeServicesAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void TrackFrameworkTask(Task task)
    {
        lock (_frameworkTasksGate)
            _frameworkTasks.Add(task);

        task.ContinueWith(
            static (completedTask, state) =>
            {
                var server = (SharpLinkServer)state!;
                lock (server._frameworkTasksGate)
                    server._frameworkTasks.Remove(completedTask);

                if (completedTask.Exception is { } exception)
                {
                    LogServerBackgroundLoopUnhandledException(
                        server._logger,
                        "FrameworkTask",
                        exception.GetBaseException());
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task DisposeAllSessionsAsync()
    {
        foreach (var connection in _connections.Values)
            await connection.CloseAsync().ConfigureAwait(false);

        _connections.Clear();
    }

    private async Task WaitForFrameworkTasksAsync()
    {
        while (true)
        {
            Task[] tasks;
            lock (_frameworkTasksGate)
                tasks = [.. _frameworkTasks];

            if (tasks.Length == 0)
                return;

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or System.IO.IOException or SocketException)
            {
            }
        }
    }

    private SharpLinkCallContextSnapshot CreateCallContext(
        IRpcSession session,
        SharpLinkAuthenticationContext? authenticationContext,
        IRpcStub stub,
        long methodId,
        long requestId,
        DateTimeOffset? deadline,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken)
    {
        if (_serverInterceptors.Length == 0)
            return new SharpLinkCallContextSnapshot(session.Id, authenticationContext, deadline, metadata);

        return CreateServerInvocationContext(
            session,
            stub,
            methodId,
            requestId,
            authenticationContext,
            deadline,
            metadata,
            cancellationToken);
    }

    private static SharpLinkServerInvocationContext CreateServerInvocationContext(
        IRpcSession session,
        IRpcStub stub,
        long methodId,
        long requestId,
        SharpLinkAuthenticationContext? authenticationContext,
        DateTimeOffset? deadline,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken)
    {
        var method = GetMethodDescriptor(stub, methodId);
        var rpcSession = (RpcSession)session;
        return new SharpLinkServerInvocationContext(
            method,
            requestId,
            session.Id,
            rpcSession.LocalEndPoint,
            rpcSession.RemoteEndPoint,
            authenticationContext,
            deadline,
            metadata,
            cancellationToken);
    }

    private static RpcMethodDescriptor GetMethodDescriptor(IRpcStub stub, long methodId)
    {
        if (!stub.TryGetMethodDescriptor(methodId, out var method))
        {
            method = new RpcMethodDescriptor(
                stub.InterfaceHash,
                methodId,
                RpcMethodKind.Unary,
                HasResponsePayload: false,
                HasClientStreams: false,
                HasMethodTimeout: false,
                MethodTimeout: null);
        }
        return method;
    }

    private bool TryAcquireCall(ServerConnectionState connection)
    {
        if (CurrentState != ServerState.Running)
            return false;
        if (!connection.TryAcquireCall(_maxConcurrentCallsPerConnection))
            return false;

        if (CurrentState != ServerState.Running)
        {
            connection.ReleaseCall();
            return false;
        }

        if (Interlocked.Increment(ref _globalActiveCalls) <= _globalMaxConcurrentCalls)
            return true;

        Interlocked.Decrement(ref _globalActiveCalls);
        connection.ReleaseCall();
        return false;
    }

    private bool TryAcceptRequest(ServerConnectionState connection, long requestId)
    {
        if (CurrentState != ServerState.Running)
            return false;
        if (!connection.TryRecordAcceptedRequest(requestId))
            return false;
        return CurrentState == ServerState.Running &&
               connection.LifecycleState == ServerConnectionLifecycleState.Ready;
    }

    private void ReleaseCall(ServerConnectionState connection)
    {
        var active = Interlocked.Decrement(ref _globalActiveCalls);
        connection.ReleaseCall();
        if (active == 0 && CurrentState == ServerState.Draining)
            _callsDrained.TrySetResult(true);
    }

    private ServerState CurrentState => (ServerState)Volatile.Read(ref _state);

    private void TransitionTo(ServerState state)
        => Interlocked.Exchange(ref _state, (int)state);

    internal void ForceStop()
    {
        try
        {
            _forceStopCts.Cancel();
        }
        catch (ObjectDisposedException) when (CurrentState == ServerState.Stopped)
        {
        }
    }

    private async ValueTask DisposeServicesAsync()
    {
        if (Interlocked.Exchange(ref _servicesDisposed, 1) != 0)
            return;

        Exception? firstException = null;
        foreach (var registration in services.Values)
        {
            try
            {
                await registration.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                firstException ??= exception;
            }
        }

        try
        {
            if (_ownedServiceProvider is not null)
                await _ownedServiceProvider.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            firstException ??= exception;
        }

        if (firstException is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstException).Throw();
    }
}
