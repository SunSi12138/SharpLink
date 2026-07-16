namespace SharpLink.Server;

internal sealed partial class SharpLinkServer(
    IServerTransportListener transportListener,
    FrozenDictionary<long, (IRpcStub stub,object service)> services,
    TimeSpan heartbeatCheckInterval,
    TimeSpan heartbeatTimeout,
    ILoggerFactory loggerFactory,
    ISharpLinkServerAuthenticator? authenticator = null,
    bool authenticationRequired = false,
    SharpLinkProtocolOptions? protocolOptions = null,
    SharpLinkRuntimeContext? runtimeContext = null,
    RpcSessionFlushOptions? rpcSessionFlushOptions = null,
    ISharpLinkServerInterceptor[]? serverInterceptors = null,
    IRpcExceptionMapper? exceptionMapper = null) : ISharpLinkServer
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
    private readonly ConcurrentDictionary<string, IRpcSession> _sessions = [];
    private readonly ConcurrentDictionary<string, SharpLinkAuthenticationContext?> _sessionAuthContexts = [];
    private readonly ConcurrentDictionary<string, long> _lastAcceptedRequestIds = [];
    private readonly ILogger _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory))).CreateLogger<SharpLinkServer>();
    private readonly ISharpLinkServerAuthenticator? _authenticator = authenticator;
    private readonly bool _authenticationRequired = authenticationRequired;
    private readonly CancellationTokenSource _acceptCts = new();
    private readonly CancellationTokenSource _forceStopCts = new();
    private readonly Lock _stateGate = new();
    private readonly Lock _backgroundTasksGate = new();
    private readonly HashSet<Task> _backgroundTasks = [];
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
    private int _globalActiveCalls;
    private long _rejectedOneWayCalls;
    private readonly int _globalMaxConcurrentCalls = (int)Math.Min(
        (long)Environment.ProcessorCount * 1024,
        65_536L);

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
            foreach (var session in _sessions.Values)
            {
                var lastAccepted = _lastAcceptedRequestIds.TryGetValue(session.Id, out var requestId)
                    ? requestId
                    : 0;
                try
                {
                    await session.SendGoAwayAsync(
                        lastAccepted,
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
            }

            if (_callsDrained.Task.IsCompletedSuccessfully)
            {
                foreach (var session in _sessions.Values)
                {
                    if (session is not RpcSession rpcSession)
                        continue;
                    try
                    {
                        await rpcSession.FlushSendQueueAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is SharpLinkException or System.IO.IOException or ObjectDisposedException)
                    {
                    }
                }
            }

            _forceStopCts.Cancel();
            await DisposeAllSessionsAsync().ConfigureAwait(false);
            await WaitForBackgroundTasksAsync().ConfigureAwait(false);
            _acceptCts.Dispose();
            _forceStopCts.Dispose();
            TransitionTo(ServerState.Stopped);
        }
        catch
        {
            TransitionTo(ServerState.Faulted);
            throw;
        }
    }

    private void TrackBackgroundTask(Task task)
    {
        lock (_backgroundTasksGate)
            _backgroundTasks.Add(task);

        task.ContinueWith(
            static (completedTask, state) =>
            {
                var server = (SharpLinkServer)state!;
                lock (server._backgroundTasksGate)
                    server._backgroundTasks.Remove(completedTask);
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task DisposeAllSessionsAsync()
    {
        foreach (var session in _sessions.Values)
            await session.DisposeAsync().ConfigureAwait(false);

        _sessions.Clear();
        _sessionAuthContexts.Clear();
        _lastAcceptedRequestIds.Clear();
    }

    private async Task WaitForBackgroundTasksAsync()
    {
        while (true)
        {
            Task[] tasks;
            lock (_backgroundTasksGate)
                tasks = [.. _backgroundTasks];

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
        IRpcStub stub,
        long methodId,
        long requestId,
        DateTimeOffset? deadline,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken)
    {
        _sessionAuthContexts.TryGetValue(session.Id, out var authenticationContext);
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

    private bool TryAcquireCall(SessionCallAdmission sessionAdmission)
    {
        if (CurrentState != ServerState.Running)
            return false;
        if (Interlocked.Increment(ref sessionAdmission.ActiveCalls) > _maxConcurrentCallsPerConnection)
        {
            Interlocked.Decrement(ref sessionAdmission.ActiveCalls);
            return false;
        }

        if (Interlocked.Increment(ref _globalActiveCalls) <= _globalMaxConcurrentCalls)
            return true;

        Interlocked.Decrement(ref _globalActiveCalls);
        Interlocked.Decrement(ref sessionAdmission.ActiveCalls);
        return false;
    }

    private void ReleaseCall(SessionCallAdmission sessionAdmission)
    {
        var active = Interlocked.Decrement(ref _globalActiveCalls);
        Interlocked.Decrement(ref sessionAdmission.ActiveCalls);
        if (active == 0 && CurrentState == ServerState.Draining)
            _callsDrained.TrySetResult(true);
    }

    private ServerState CurrentState => (ServerState)Volatile.Read(ref _state);

    private void TransitionTo(ServerState state)
        => Interlocked.Exchange(ref _state, (int)state);

    private sealed class SessionCallAdmission
    {
        public int ActiveCalls;
    }
}
