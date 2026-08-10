
using System.Reflection;

namespace SharpLink.Client;

internal sealed partial class SharpLinkClient :
    IRpcChannel,
    ISharpLinkClient,
    IDynamicAssemblyRegistrationInspector,
    ISharpLinkClientDrainInspector,
    ISharpLinkClientTimeProvider
{
    private readonly IClientTransportFactory transportFactory;
    private readonly IEndpointClusterRuntime? _cluster;
    // Retained for endpoint-aware diagnostics without routing fixed calls through cluster selection.
    private readonly SharpLinkEndpoint? _fixedEndpoint;
    private readonly SharpLinkRuntimeContext _runtimeContext;
    private readonly IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> _staticManifests;
    private FrozenDictionary<Type, ClientProxyRegistration> _proxies;
    private readonly Lock _registryGate = new();
    private readonly Dictionary<Assembly, SharpLinkDynamicModule> _dynamicModules =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Assembly, Task<SharpLinkAssemblyUnregisterResult>> _unregisterOperations =
        new(ReferenceEqualityComparer.Instance);
    private long _registryGeneration;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Lock _stateGate = new();
    private readonly Lock _poolGate = new();
    private readonly FrameworkTaskSupervisor _frameworkTasks;
    private readonly SemaphoreSlim _reconnectSignal = new(0, 1);
    private ClientConnection[] _readyConnections = [];
    private readonly HashSet<ClientConnection> _connections = [];
    private bool _poolStopping;
    private Task? _connectTask;
    private Task? _reconnectTask;
    private Task? _expansionTask;
    private Task? _stopTask;
    private TaskCompletionSource<bool> _readySignal = CreateReadySignal();
    private int _activeLogicalInvocations;
    private int _state = (int)SharpLinkConnectionState.Created;
    private int _reconnectDelayMilliseconds = 100;
    private long _readyTimestamp;
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _heartbeatTimeout;
    private readonly bool _hasRequestTimeout;
    private readonly TimeSpan _requestTimeoutValue;
    private readonly ISharpLinkClientAuthenticator? _authenticator;
    private readonly SharpLinkProtocolOptions _protocolOptions;
    private readonly ILogger _logger;
    private readonly RpcSessionFlushOptions? _rpcSessionFlushOptions;
    private readonly SharpLinkConnectionPoolOptions _connectionPoolOptions;
    private readonly ISharpLinkClientInterceptor[] _clientInterceptors;
    private readonly SharpLinkRetryOptions? _retryOptions;
    private readonly ISharpLinkRetryPolicy? _retryPolicy;
    private readonly ISharpLinkEndpointAdmissionPolicy? _endpointAdmissionPolicy;
    private readonly ISharpLinkReconnectJitter _reconnectJitter;

    /// <summary>
    /// Initializes a Client from the explicit composition materialized by <see cref="SharpClientBuilder"/>.
    /// It intentionally performs no catalog discovery, option clone/default, topology selection, endpoint
    /// factory call, or RuntimeContext materialization. The already-tagged topology is bound here so the
    /// Client is fully valid when construction completes.
    /// </summary>
    internal SharpLinkClient(ClientRuntimeComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        transportFactory = composition.TransportFactory;
        _runtimeContext = composition.RuntimeContext;
        _staticManifests = composition.StaticManifests;
        _proxies = composition.StaticProxies;
        _heartbeatInterval = composition.HeartbeatInterval;
        _heartbeatTimeout = composition.HeartbeatTimeout;
        _hasRequestTimeout = composition.HasRequestTimeout;
        _requestTimeoutValue = composition.RequestTimeout;
        _authenticator = composition.Authenticator;
        _protocolOptions = composition.ProtocolOptions;
        _rpcSessionFlushOptions = composition.RpcSessionFlushOptions;
        _connectionPoolOptions = composition.ConnectionPoolOptions;
        _clientInterceptors = composition.Interceptors;
        _retryOptions = composition.RetryOptions;
        _retryPolicy = composition.RetryPolicy;
        _endpointAdmissionPolicy = composition.EndpointAdmissionPolicy;
        _reconnectJitter = composition.ReconnectJitter;
        _logger = composition.Logger;
        _frameworkTasks = composition.FrameworkTasks;

        // Builder has already selected and materialized exactly one tagged topology. Creating the
        // Client-owned cluster object here does not enumerate endpoints, invoke a transport factory,
        // or reinterpret mutable Builder state.
        switch (composition.Topology)
        {
            case FixedClientRuntimeTopologyComposition fixedTopology:
                _fixedEndpoint = fixedTopology.Endpoint;
                _cluster = null;
                break;
            case StaticClientRuntimeTopologyComposition staticTopology:
                _fixedEndpoint = null;
                _cluster = new StaticClusterRuntime(this, staticTopology);
                break;
            case DynamicClientRuntimeTopologyComposition dynamicTopology:
                _fixedEndpoint = null;
                _cluster = new DynamicClusterRuntime(this, dynamicTopology);
                break;
            default:
                throw new UnreachableException();
        }
    }

    internal static FrameworkTaskSupervisor CreateFrameworkTaskSupervisor(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return new FrameworkTaskSupervisor((operation, exception) =>
            LogClientBackgroundLoopUnhandledException(logger, operation, exception));
    }

    public IRpcRuntimeContext RuntimeContext => _runtimeContext;

    TimeProvider ISharpLinkClientTimeProvider.TimeProvider
        => _runtimeContext.TimeProvider;

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
        lock (_stateGate)
            TransitionTo(SharpLinkConnectionState.Draining);
        lock (_registryGate)
            TransitionTo(SharpLinkConnectionState.Draining);
        lock (_poolGate)
        {
            _poolStopping = true;
            // Serializes Seal with connection publication, retirement, and cleanup registration.
            _frameworkTasks.Seal();
        }
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

        Assembly[] dynamicAssemblies;
        lock (_registryGate)
            dynamicAssemblies = [.. _dynamicModules.Keys];
        for (var index = 0; index < dynamicAssemblies.Length; index++)
        {
            try { await UnregisterAssemblyAsync(dynamicAssemblies[index], TimeSpan.Zero).ConfigureAwait(false); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
        }

        try { await _frameworkTasks.DrainAsync().ConfigureAwait(false); }
        catch (Exception exception) { cleanupFailures.Add(exception); }

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
        lock (_stateGate)
            TransitionTo(SharpLinkConnectionState.Draining);
        lock (_registryGate)
            TransitionTo(SharpLinkConnectionState.Draining);
        _cluster!.BeginStop();
        _frameworkTasks.Seal();
        try { await _shutdownCts.CancelAsync().ConfigureAwait(false); }
        catch (Exception exception) { cleanupFailures.Add(exception); }
        Volatile.Read(ref _readySignal).TrySetResult(true);
        try { await _cluster.StopAsync().ConfigureAwait(false); }
        catch (Exception exception) { cleanupFailures.Add(exception); }

        Assembly[] dynamicAssemblies;
        lock (_registryGate)
            dynamicAssemblies = [.. _dynamicModules.Keys];
        for (var index = 0; index < dynamicAssemblies.Length; index++)
        {
            try { await UnregisterAssemblyAsync(dynamicAssemblies[index], TimeSpan.Zero).ConfigureAwait(false); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
        }

        try { await _frameworkTasks.DrainAsync().ConfigureAwait(false); }
        catch (Exception exception) { cleanupFailures.Add(exception); }
        try { await _cluster.DisposeResourcesAsync().ConfigureAwait(false); }
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

    private static void ThrowStopCleanupFailures(List<Exception> failures)
    {
        if (failures.Count == 0)
            return;
        if (failures.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException(failures);
    }

    private static bool IsExpectedStopException(Exception exception)
        => exception is OperationCanceledException or ObjectDisposedException or IOException or SocketException or
            SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed or SharpLinkErrorCode.Unavailable };

    internal void TrackFrameworkTask(
        Task task,
        string operation,
        TaskObservationMode observationMode = TaskObservationMode.FrameworkOwned)
        => _frameworkTasks.Track(task, operation, observationMode, IsExpectedStopException);

    internal FrameworkTaskSupervisorSnapshot FrameworkTaskSnapshotForDiagnostics
        => _frameworkTasks.CaptureSnapshot();

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
