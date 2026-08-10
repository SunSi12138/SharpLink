
using System.Reflection;

namespace SharpLink.Client;

internal sealed partial class SharpLinkClient :
    IRpcChannel,
    ISharpLinkClient,
    IDynamicAssemblyRegistrationInspector,
    ISharpLinkClientDrainInspector
{
    private readonly IClientTransportFactory transportFactory;
    private readonly IEndpointClusterRuntime? _cluster;
    // Retained for endpoint-aware diagnostics without routing fixed calls through cluster selection.
    private readonly SharpLinkEndpoint? _fixedEndpoint;
    private readonly SharpLinkRuntimeContext _runtimeContext;
    private readonly IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> _staticManifests;
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
    private int _activeLogicalInvocations;
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
    private readonly SharpLinkRetryOptions? _retryOptions;
    private readonly ISharpLinkRetryPolicy? _retryPolicy;
    private readonly ISharpLinkEndpointAdmissionPolicy? _endpointAdmissionPolicy;

    private SharpLinkClient(
        IClientTransportFactory transportFactory,
        SharpLinkRuntimeContext runtimeContext,
        StaticEndpointConfiguration[]? staticEndpoints = null,
        SharpLinkClusterOptions? clusterOptions = null,
        SharpLinkLoadBalancingStrategy loadBalancingStrategy = SharpLinkLoadBalancingStrategy.PowerOfTwoChoices,
        ISharpLinkEndpointSelector? endpointSelector = null,
        SharpLinkEndpoint? fixedEndpoint = null,
        ISharpLinkEndpointResolver? dynamicResolver = null,
        SharpLinkEndpointTransportFactory? dynamicTransportFactory = null,
        SharpLinkRetryOptions? retryOptions = null,
        ISharpLinkRetryPolicy? retryPolicy = null,
        ISharpLinkEndpointAdmissionPolicy? endpointAdmissionPolicy = null,
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest>? staticManifests = null)
    {
        this.transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _runtimeContext = runtimeContext ?? throw new ArgumentNullException(nameof(runtimeContext));
        _staticManifests = staticManifests ?? SharpLinkGeneratedAssemblyCatalog.CreateSnapshot();
        _fixedEndpoint = fixedEndpoint;
        _retryOptions = retryOptions;
        _retryPolicy = retryPolicy;
        _endpointAdmissionPolicy = endpointAdmissionPolicy;
        if (staticEndpoints is not null && dynamicResolver is not null)
            throw new ArgumentException("Static endpoints and an endpoint resolver cannot both be configured.");
        if (staticEndpoints is not null)
        {
            _cluster = new StaticClusterRuntime(
                this,
                staticEndpoints,
                clusterOptions ?? throw new ArgumentNullException(nameof(clusterOptions)),
                loadBalancingStrategy,
                endpointSelector);
        }
        else if (dynamicResolver is not null)
        {
            _cluster = new DynamicClusterRuntime(
                this,
                dynamicResolver,
                dynamicTransportFactory ?? throw new ArgumentNullException(nameof(dynamicTransportFactory)),
                clusterOptions ?? throw new ArgumentNullException(nameof(clusterOptions)),
                loadBalancingStrategy,
                endpointSelector);
        }
    }

    public SharpLinkClient(
        IClientTransportFactory transportFactory,
        TimeSpan heartbeatInterval,
        TimeSpan heartbeatTimeout,
        SharpLinkRuntimeContext runtimeContext,
        TimeSpan? requestTimeout = null,
        ISharpLinkClientAuthenticator? authenticator = null,
        SharpLinkProtocolOptions? protocolOptions = null,
        RpcSessionFlushOptions? rpcSessionFlushOptions = null,
        SharpLinkConnectionPoolOptions? connectionPoolOptions = null,
        ISharpLinkClientInterceptor[]? clientInterceptors = null,
        StaticEndpointConfiguration[]? staticEndpoints = null,
        SharpLinkClusterOptions? clusterOptions = null,
        SharpLinkLoadBalancingStrategy loadBalancingStrategy = SharpLinkLoadBalancingStrategy.PowerOfTwoChoices,
        ISharpLinkEndpointSelector? endpointSelector = null,
        SharpLinkEndpoint? fixedEndpoint = null,
        ISharpLinkEndpointResolver? dynamicResolver = null,
        SharpLinkEndpointTransportFactory? dynamicTransportFactory = null,
        SharpLinkRetryOptions? retryOptions = null,
        ISharpLinkRetryPolicy? retryPolicy = null,
        ISharpLinkEndpointAdmissionPolicy? endpointAdmissionPolicy = null,
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest>? staticManifests = null)
        : this(transportFactory, runtimeContext, staticEndpoints, clusterOptions, loadBalancingStrategy, endpointSelector, fixedEndpoint,
            dynamicResolver, dynamicTransportFactory, retryOptions, retryPolicy, endpointAdmissionPolicy, staticManifests)
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
        SharpLinkRuntimeContext runtimeContext,
        TimeSpan? requestTimeout = null,
        ISharpLinkClientAuthenticator? authenticator = null,
        SharpLinkProtocolOptions? protocolOptions = null,
        RpcSessionFlushOptions? rpcSessionFlushOptions = null,
        SharpLinkConnectionPoolOptions? connectionPoolOptions = null,
        ISharpLinkClientInterceptor[]? clientInterceptors = null,
        StaticEndpointConfiguration[]? staticEndpoints = null,
        SharpLinkClusterOptions? clusterOptions = null,
        SharpLinkLoadBalancingStrategy loadBalancingStrategy = SharpLinkLoadBalancingStrategy.PowerOfTwoChoices,
        ISharpLinkEndpointSelector? endpointSelector = null,
        SharpLinkEndpoint? fixedEndpoint = null,
        ISharpLinkEndpointResolver? dynamicResolver = null,
        SharpLinkEndpointTransportFactory? dynamicTransportFactory = null,
        SharpLinkRetryOptions? retryOptions = null,
        ISharpLinkRetryPolicy? retryPolicy = null,
        ISharpLinkEndpointAdmissionPolicy? endpointAdmissionPolicy = null,
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest>? staticManifests = null)
        : this(transportFactory, heartbeatInterval, heartbeatTimeout, runtimeContext, requestTimeout, authenticator,
            protocolOptions, rpcSessionFlushOptions, connectionPoolOptions, clientInterceptors, staticEndpoints,
            clusterOptions, loadBalancingStrategy, endpointSelector, fixedEndpoint, dynamicResolver, dynamicTransportFactory,
            retryOptions, retryPolicy, endpointAdmissionPolicy, staticManifests)
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
        if (_cluster is not null)
        {
            await StopStaticClusterCoreAsync().ConfigureAwait(false);
            return;
        }

        var cleanupFailures = new List<Exception>();
        lock (_registryGate)
            TransitionTo(SharpLinkConnectionState.Draining);
        try { await _shutdownCts.CancelAsync().ConfigureAwait(false); }
        catch (Exception exception) { cleanupFailures.Add(exception); }
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
            try { connections[index].Fail(stoppingException); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
            try { await connections[index].DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
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
        // ConnectAsync exposes the initial attempt directly to its caller. Do not report that
        // same already-observable failure a second time from DisposeAsync/StopAsync.
        try { await IgnoreExpectedStopExceptionAsync(connectTask, ignoreUnexpected: true).ConfigureAwait(false); }
        catch (Exception exception) { cleanupFailures.Add(exception); }
        if (!ReferenceEquals(reconnectTask, connectTask))
        {
            try { await IgnoreExpectedStopExceptionAsync(reconnectTask).ConfigureAwait(false); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
        }
        if (!ReferenceEquals(expansionTask, connectTask) && !ReferenceEquals(expansionTask, reconnectTask))
        {
            try { await IgnoreExpectedStopExceptionAsync(expansionTask).ConfigureAwait(false); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
        }
        try { await WaitForBackgroundTasksAsync().ConfigureAwait(false); }
        catch (Exception exception) { cleanupFailures.Add(exception); }

        Assembly[] dynamicAssemblies;
        lock (_registryGate)
            dynamicAssemblies = [.. _dynamicModules.Keys];
        for (var index = 0; index < dynamicAssemblies.Length; index++)
        {
            try { await UnregisterAssemblyAsync(dynamicAssemblies[index], TimeSpan.Zero).ConfigureAwait(false); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
        }

        try { await transportFactory.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { cleanupFailures.Add(exception); }
        try { _reconnectSignal.Dispose(); }
        catch (Exception exception) { cleanupFailures.Add(exception); }
        try { _shutdownCts.Dispose(); }
        catch (Exception exception) { cleanupFailures.Add(exception); }
        try { _runtimeContext.Dispose(); }
        catch (Exception exception) { cleanupFailures.Add(exception); }
        TransitionTo(SharpLinkConnectionState.Stopped);
        ThrowStopCleanupFailures(cleanupFailures);
    }

    private async Task StopStaticClusterCoreAsync()
    {
        var cleanupFailures = new List<Exception>();
        lock (_registryGate)
            TransitionTo(SharpLinkConnectionState.Draining);
        try { await _shutdownCts.CancelAsync().ConfigureAwait(false); }
        catch (Exception exception) { cleanupFailures.Add(exception); }
        Volatile.Read(ref _readySignal).TrySetResult(true);
        try { await _cluster!.StopAsync().ConfigureAwait(false); }
        catch (Exception exception) { cleanupFailures.Add(exception); }
        try { await WaitForBackgroundTasksAsync().ConfigureAwait(false); }
        catch (Exception exception) { cleanupFailures.Add(exception); }

        Assembly[] dynamicAssemblies;
        lock (_registryGate)
            dynamicAssemblies = [.. _dynamicModules.Keys];
        for (var index = 0; index < dynamicAssemblies.Length; index++)
        {
            try { await UnregisterAssemblyAsync(dynamicAssemblies[index], TimeSpan.Zero).ConfigureAwait(false); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
        }

        try { _reconnectSignal.Dispose(); }
        catch (Exception exception) { cleanupFailures.Add(exception); }
        try { _shutdownCts.Dispose(); }
        catch (Exception exception) { cleanupFailures.Add(exception); }
        try { _runtimeContext.Dispose(); }
        catch (Exception exception) { cleanupFailures.Add(exception); }
        TransitionTo(SharpLinkConnectionState.Stopped);
        ThrowStopCleanupFailures(cleanupFailures);
    }

    private static void ThrowStopCleanupFailures(List<Exception> failures)
    {
        if (failures.Count == 0)
            return;
        if (failures.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException(failures);
    }

    private async Task IgnoreExpectedStopExceptionAsync(
        Task? task,
        bool ignoreUnexpected = false)
    {
        if (task is null)
            return;
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception) when (_shutdownCts.IsCancellationRequested)
        {
            if (ignoreUnexpected)
                return;
            var failures = exception is AggregateException aggregate
                ? aggregate.Flatten().InnerExceptions
                : [exception];
            List<Exception>? unexpected = null;
            for (var index = 0; index < failures.Count; index++)
            {
                var failure = failures[index];
                if (IsExpectedStopException(failure))
                    continue;
                (unexpected ??= []).Add(failure);
            }

            if (unexpected is { Count: 1 })
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(unexpected[0]).Throw();
            if (unexpected is not null)
                throw new AggregateException(unexpected);
        }
    }

    private static bool IsExpectedStopException(Exception exception)
        => exception is OperationCanceledException or ObjectDisposedException or IOException or SocketException or
            SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed or SharpLinkErrorCode.Unavailable };

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

                if (completedTask.Exception is { } exception)
                {
                    LogClientBackgroundLoopUnhandledException(
                        client._logger,
                        "BackgroundTask",
                        exception.GetBaseException());
                }
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
            catch
            {
                List<Exception>? unexpected = null;
                for (var taskIndex = 0; taskIndex < tasks.Length; taskIndex++)
                {
                    if (tasks[taskIndex].Exception is not { } aggregate)
                        continue;
                    foreach (var exception in aggregate.Flatten().InnerExceptions)
                    {
                        if (IsExpectedStopException(exception))
                            continue;
                        (unexpected ??= []).Add(exception);
                    }
                }

                if (unexpected is { Count: 1 })
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(unexpected[0]).Throw();
                if (unexpected is not null)
                    throw new AggregateException(unexpected);
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

    internal int ReadyConnectionCount => _cluster?.ReadyConnectionCount ?? Volatile.Read(ref _readyConnections).Length;

    internal int PendingCallCount
    {
        get
        {
            if (_cluster is not null)
                return _cluster.PendingCallCount;
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
            if (_cluster is not null)
                return _cluster.ActiveCallCount;
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
            if (_cluster is not null)
                return _cluster.ActiveStreamCount;
            var connections = Volatile.Read(ref _readyConnections);
            var count = 0;
            for (var index = 0; index < connections.Length; index++)
                count += ((StreamManager)connections[index].Session.StreamManager).ActiveStreamCount;
            return count;
        }
    }

    int ISharpLinkClientDrainInspector.ActiveCallCount => Volatile.Read(ref _activeLogicalInvocations);

    int ISharpLinkClientDrainInspector.ActiveStreamCount => ActiveClientStreamCount;

}
