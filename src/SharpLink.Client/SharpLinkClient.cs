
using System.Reflection;

namespace SharpLink.Client;

internal sealed partial class SharpLinkClient(IClientTransportFactory transportFactory) : IRpcChannel, ISharpLinkClient
{
    private readonly SharpLinkRuntimeContext _runtimeContext = new SharpLinkRuntimeContextBuilder().Build();
    private readonly IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> _staticManifests =
        SharpLinkGeneratedAssemblyCatalog.CreateSnapshot();
    private FrozenDictionary<Type, ClientProxyRegistration> _proxies =
        FrozenDictionary<Type, ClientProxyRegistration>.Empty;
    private readonly Lock _registryGate = new();
    private readonly Dictionary<Assembly, SharpLinkDynamicModule> _dynamicModules =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Assembly, Task<SharpLinkAssemblyUnregisterResult>> _unregisterOperations =
        new(ReferenceEqualityComparer.Instance);
    private long _registryGeneration;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Lock _stateGate = new();
    private readonly Lock _poolGate = new();
    private readonly Lock _backgroundTasksGate = new();
    private readonly SemaphoreSlim _reconnectSignal = new(0, 1);
    private readonly HashSet<Task> _backgroundTasks = [];
    private ClientConnection[] _readyConnections = [];
    private readonly HashSet<ClientConnection> _connections = [];
    private Task? _connectTask;
    private Task? _reconnectTask;
    private Task? _expansionTask;
    private Task? _stopTask;
    private TaskCompletionSource<bool> _readySignal = CreateReadySignal();
    private int _state = (int)SharpLinkConnectionState.Created;
    private int _reconnectDelayMilliseconds = 100;
    private long _readyTimestamp;
    private readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(10);
    private readonly TimeSpan _heartbeatTimeout = TimeSpan.FromSeconds(30);
    private readonly bool _hasRequestTimeout;
    private readonly TimeSpan _requestTimeoutValue;
    private readonly ISharpLinkClientAuthenticator? _authenticator;
    private readonly SharpLinkProtocolOptions _protocolOptions = new();
    private readonly ILogger _logger = NullLogger<SharpLinkClient>.Instance;
    private readonly RpcSessionFlushOptions? _rpcSessionFlushOptions;
    private readonly SharpLinkConnectionPoolOptions _connectionPoolOptions = new();
    private readonly ISharpLinkClientInterceptor[] _clientInterceptors = [];

    public SharpLinkClient(
        IClientTransportFactory transportFactory,
        TimeSpan heartbeatInterval,
        TimeSpan heartbeatTimeout,
        TimeSpan? requestTimeout = null,
        ISharpLinkClientAuthenticator? authenticator = null,
        SharpLinkProtocolOptions? protocolOptions = null,
        SharpLinkRuntimeContext? runtimeContext = null,
        RpcSessionFlushOptions? rpcSessionFlushOptions = null,
        SharpLinkConnectionPoolOptions? connectionPoolOptions = null,
        ISharpLinkClientInterceptor[]? clientInterceptors = null)
        : this(transportFactory)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heartbeatInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heartbeatTimeout, TimeSpan.Zero);
        if (heartbeatTimeout <= heartbeatInterval)
            throw new ArgumentException("Heartbeat timeout must be greater than interval.");
        if (requestTimeout is { } timeout)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
            _hasRequestTimeout = true;
            _requestTimeoutValue = timeout;
        }

        _heartbeatInterval = heartbeatInterval;
        _heartbeatTimeout = heartbeatTimeout;
        _authenticator = authenticator;
        _runtimeContext = runtimeContext ?? new SharpLinkRuntimeContextBuilder().Build();
        _protocolOptions = (protocolOptions ?? _runtimeContext.Protocol).CloneValidated();
        _rpcSessionFlushOptions = rpcSessionFlushOptions;
        _connectionPoolOptions = (connectionPoolOptions ?? new SharpLinkConnectionPoolOptions()).CloneValidated();
        _clientInterceptors = clientInterceptors is { Length: > 0 } ? [.. clientInterceptors] : [];
        _proxies = BuildStaticProxySnapshot(_staticManifests);
    }

    public SharpLinkClient(
        IClientTransportFactory transportFactory,
        TimeSpan heartbeatInterval,
        TimeSpan heartbeatTimeout,
        ILoggerFactory loggerFactory,
        TimeSpan? requestTimeout = null,
        ISharpLinkClientAuthenticator? authenticator = null,
        SharpLinkProtocolOptions? protocolOptions = null,
        SharpLinkRuntimeContext? runtimeContext = null,
        RpcSessionFlushOptions? rpcSessionFlushOptions = null,
        SharpLinkConnectionPoolOptions? connectionPoolOptions = null,
        ISharpLinkClientInterceptor[]? clientInterceptors = null)
        : this(transportFactory, heartbeatInterval, heartbeatTimeout, requestTimeout, authenticator, protocolOptions,
            runtimeContext, rpcSessionFlushOptions, connectionPoolOptions, clientInterceptors)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger<SharpLinkClient>();
    }

    public IRpcRuntimeContext RuntimeContext => _runtimeContext;

    public SharpLinkConnectionState State
        => (SharpLinkConnectionState)Volatile.Read(ref _state);

    public ValueTask DisposeAsync() => StopAsync();

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        Task stopTask;
        lock (_stateGate)
        {
            _stopTask ??= StopCoreAsync();
            stopTask = _stopTask;
        }

        return cancellationToken.CanBeCanceled
            ? new ValueTask(stopTask.WaitAsync(cancellationToken))
            : new ValueTask(stopTask);
    }

    private async Task StopCoreAsync()
    {
        lock (_registryGate)
            TransitionTo(SharpLinkConnectionState.Draining);
        _shutdownCts.Cancel();
        Volatile.Read(ref _readySignal).TrySetResult(true);

        var stoppingException = CreateConnectionClosedException("Client is stopping.");
        ClientConnection[] connections;
        lock (_poolGate)
        {
            connections = [.. _connections];
            _connections.Clear();
            Volatile.Write(ref _readyConnections, []);
        }
        for (var index = 0; index < connections.Length; index++)
        {
            connections[index].Fail(stoppingException);
            await connections[index].DisposeAsync().ConfigureAwait(false);
        }

        Task? connectTask;
        Task? reconnectTask;
        Task? expansionTask;
        lock (_stateGate)
        {
            connectTask = _connectTask;
            reconnectTask = _reconnectTask;
            expansionTask = _expansionTask;
        }
        await IgnoreExpectedStopExceptionAsync(connectTask).ConfigureAwait(false);
        if (!ReferenceEquals(reconnectTask, connectTask))
            await IgnoreExpectedStopExceptionAsync(reconnectTask).ConfigureAwait(false);
        if (!ReferenceEquals(expansionTask, connectTask) && !ReferenceEquals(expansionTask, reconnectTask))
            await IgnoreExpectedStopExceptionAsync(expansionTask).ConfigureAwait(false);
        await WaitForBackgroundTasksAsync().ConfigureAwait(false);

        Assembly[] dynamicAssemblies;
        lock (_registryGate)
            dynamicAssemblies = [.. _dynamicModules.Keys];
        for (var index = 0; index < dynamicAssemblies.Length; index++)
            await UnregisterAssemblyAsync(dynamicAssemblies[index], TimeSpan.Zero).ConfigureAwait(false);

        await transportFactory.DisposeAsync().ConfigureAwait(false);
        _reconnectSignal.Dispose();
        _shutdownCts.Dispose();
        TransitionTo(SharpLinkConnectionState.Stopped);
    }

    private async Task IgnoreExpectedStopExceptionAsync(Task? task)
    {
        if (task is null)
            return;
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception) when (_shutdownCts.IsCancellationRequested)
        {
        }
    }

    internal void TrackBackgroundTask(Task task)
    {
        lock (_backgroundTasksGate)
            _backgroundTasks.Add(task);

        task.ContinueWith(
            static (completedTask, state) =>
            {
                var client = (SharpLinkClient)state!;
                lock (client._backgroundTasksGate)
                    client._backgroundTasks.Remove(completedTask);
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
            {
            }
        }
    }

    private static SharpLinkException CreateAuthenticationRejectedException(string message)
        => new(SharpLinkErrorCode.AuthenticationRejected, message);

    private static SharpLinkException CreateConnectionClosedException(string message, Exception? innerException = null)
        => new(SharpLinkErrorCode.ConnectionClosed, message, innerException);

    private static SharpLinkException CreateHeartbeatTimeoutException(string message)
        => new(SharpLinkErrorCode.HeartbeatTimeout, message);

    private static SharpLinkException CreateProtocolViolationException(string message)
        => new(SharpLinkErrorCode.ProtocolViolation, message);

    private void TransitionTo(SharpLinkConnectionState state)
        => Interlocked.Exchange(ref _state, (int)state);

    private static TaskCompletionSource<bool> CreateReadySignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void ResetReadySignal()
    {
        lock (_stateGate)
        {
            if (_readySignal.Task.IsCompleted)
                _readySignal = CreateReadySignal();
        }
    }

    internal int ReadyConnectionCount => Volatile.Read(ref _readyConnections).Length;

    internal int PendingCallCount
    {
        get
        {
            var connections = Volatile.Read(ref _readyConnections);
            var count = 0;
            for (var index = 0; index < connections.Length; index++)
                count += connections[index].PendingCalls.Count;
            return count;
        }
    }

    internal int ActiveClientCallCount
    {
        get
        {
            var connections = Volatile.Read(ref _readyConnections);
            var count = 0;
            for (var index = 0; index < connections.Length; index++)
                count += connections[index].ActiveCallCount;
            return count;
        }
    }

    internal int ActiveClientStreamCount
    {
        get
        {
            var connections = Volatile.Read(ref _readyConnections);
            var count = 0;
            for (var index = 0; index < connections.Length; index++)
                count += ((StreamManager)connections[index].Session.StreamManager).ActiveStreamCount;
            return count;
        }
    }

}
