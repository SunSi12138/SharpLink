
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
    private readonly Lock _readySignalGate = new();
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
    private int _stopStarted;
    private TaskCompletionSource<bool> _readySignal = CreateReadySignal();
    private int _activeLogicalInvocations;
    private int _state = (int)SharpLinkConnectionState.Created;
    private int _reconnectDelayMilliseconds = 100;
    private long _readyTimestamp;
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _heartbeatTimeout;
    private readonly bool _hasRequestTimeout;
    private readonly TimeSpan _requestTimeoutValue;
    private readonly ClientRequestTimeoutSource _requestTimeoutSource;
    private readonly ISharpLinkClientAuthenticator? _authenticator;
    private readonly SharpLinkProtocolOptions _protocolOptions;
    private readonly ILogger _logger;
    private readonly RpcSessionFlushOptions? _rpcSessionFlushOptions;
    private readonly SharpLinkConnectionPoolOptions _connectionPoolOptions;
    private ISharpLinkClientInterceptor[] _clientInterceptors;
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
        _maximumReadinessWaitThreshold = composition.Readiness.MaximumWaitThreshold;
        _readinessFacts = new ClientReadinessFacts(
            composition.Readiness.InitialActiveEndpoints,
            ReadyEndpoints: 0,
            ReadyConnections: 0,
            composition.Readiness.InitialTargetReadyEndpoints);
        _readinessPublication = new ClientReadinessPublication(
            CreateReadinessSnapshotLocked());
        transportFactory = composition.TransportFactory;
        _runtimeContext = composition.RuntimeContext;
        _staticManifests = composition.StaticManifests;
        _proxies = composition.StaticProxies;
        _heartbeatInterval = composition.HeartbeatInterval;
        _heartbeatTimeout = composition.HeartbeatTimeout;
        _hasRequestTimeout = composition.HasRequestTimeout;
        _requestTimeoutValue = composition.RequestTimeout;
        _requestTimeoutSource = composition.RequestTimeoutSource;
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
            if (_stopTask is null)
            {
                Volatile.Write(ref _stopStarted, 1);
                _stopTask = StopCoreAsync();
            }
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
        PulseReadySignal();

        var stoppingException = CreateConnectionClosedException("Client is stopping.");
        ClientConnection[] connections;
        lock (_poolGate)
        {
            connections = [.. _connections];
            _connections.Clear();
            PublishReadySnapshotLocked();
        }
        for (var index = 0; index < connections.Length; index++)
        {
            try { connections[index].Fail(stoppingException); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
            try { await connections[index].DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
        }

        var dynamicAssemblies = GetDynamicAssembliesForShutdown();
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
        PulseReadySignal();
        try { await _cluster.StopAsync().ConfigureAwait(false); }
        catch (Exception exception) { cleanupFailures.Add(exception); }

        var dynamicAssemblies = GetDynamicAssembliesForShutdown();
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

    private Assembly[] GetDynamicAssembliesForShutdown()
    {
        SharpLinkDynamicModule[] modules;
        lock (_registryGate)
            modules = [.. _dynamicModules.Values];

        if (modules.Length == 0)
            return [];
        if (modules.Length == 1)
            return [modules[0].Assembly];

        var identities = new string[modules.Length];
        var dependencies = new string[modules.Length][];
        for (var index = 0; index < modules.Length; index++)
        {
            var manifest = modules[index].Manifest;
            identities[index] = manifest.OwnerAssembly.FullName ??
                                manifest.OwnerAssembly.GetName().Name ??
                                string.Empty;
            dependencies[index] = EnumerateManifestDependencies(manifest).ToArray();
        }

        var order = GetShutdownDependencyOrder(identities, dependencies);
        var assemblies = new Assembly[order.Length];
        for (var index = 0; index < order.Length; index++)
            assemblies[index] = modules[order[index]].Assembly;
        return assemblies;
    }

    internal static int[] GetShutdownDependencyOrder(
        string[] identities,
        string[][] dependencies)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(dependencies);
        if (identities.Length != dependencies.Length)
            throw new ArgumentException("Dependency rows must match the module identity count.", nameof(dependencies));

        var remaining = new bool[identities.Length];
        Array.Fill(remaining, true);
        var order = new int[identities.Length];
        for (var outputIndex = 0; outputIndex < order.Length; outputIndex++)
        {
            var selected = -1;
            for (var candidate = 0; candidate < identities.Length; candidate++)
            {
                if (!remaining[candidate])
                    continue;

                var hasRemainingDependant = false;
                for (var dependant = 0; dependant < identities.Length; dependant++)
                {
                    if (dependant == candidate || !remaining[dependant])
                        continue;
                    if (dependencies[dependant].Any(dependency =>
                            string.Equals(dependency, identities[candidate], StringComparison.Ordinal)))
                    {
                        hasRemainingDependant = true;
                        break;
                    }
                }

                if (!hasRemainingDependant)
                {
                    selected = candidate;
                    break;
                }
            }

            // Registration validates dependency closure before publication, so a live cycle is not
            // expected. Keep teardown deterministic for corrupted/custom manifests; the normal
            // unregister guard will then surface the invalid graph rather than looping forever.
            if (selected < 0)
            {
                for (var candidate = identities.Length - 1; candidate >= 0; candidate--)
                {
                    if (remaining[candidate])
                    {
                        selected = candidate;
                        break;
                    }
                }
            }

            order[outputIndex] = selected;
            remaining[selected] = false;
        }

        return order;
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
    {
        TaskCompletionSource? changed;
        lock (_readinessGate)
        {
            var currentState = (SharpLinkConnectionState)Volatile.Read(ref _state);
            if (currentState == SharpLinkConnectionState.Stopped)
                return;

            var stopStarted = Volatile.Read(ref _stopStarted) != 0;
            if (stopStarted &&
                state is not SharpLinkConnectionState.Draining and not SharpLinkConnectionState.Stopped)
            {
                return;
            }

            state = NormalizeAvailabilityState(
                state,
                currentState,
                _readinessFacts.ReadyConnections,
                stopStarted);
            Interlocked.Exchange(ref _state, (int)state);
            changed = PublishReadinessLocked();
            UpdateReadySignalLevelLocked();
        }
        changed?.TrySetResult();
    }

    private static SharpLinkConnectionState NormalizeAvailabilityState(
        SharpLinkConnectionState requestedState,
        SharpLinkConnectionState currentState,
        int readyConnections,
        bool stopStarted)
    {
        // Connection and topology writers publish their immutable facts before lifecycle work
        // continues outside the pool/cluster gate. A later writer can therefore overtake a stale
        // availability-derived state request. Resolve those requests against
        // the latest serialized facts so an older continuation cannot leave the public state and
        // the routable connection snapshot in conflict.
        if (readyConnections == 0 && requestedState == SharpLinkConnectionState.Ready)
        {
            return currentState == SharpLinkConnectionState.Ready
                ? SharpLinkConnectionState.Reconnecting
                : currentState;
        }
        if (readyConnections != 0 && !stopStarted &&
            (requestedState is SharpLinkConnectionState.Connecting or
                SharpLinkConnectionState.Reconnecting or
                SharpLinkConnectionState.Faulted or
                SharpLinkConnectionState.Draining))
        {
            return SharpLinkConnectionState.Ready;
        }
        return requestedState;
    }

    private static TaskCompletionSource<bool> CreateReadySignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void PulseReadySignal()
    {
        lock (_readySignalGate)
            _readySignal.TrySetResult(true);
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
                count += connections[index].Session.StreamManager.ActiveStreamCount;
            return count;
        }
    }

    int ISharpLinkClientDrainInspector.ActiveCallCount => Volatile.Read(ref _activeLogicalInvocations);

    int ISharpLinkClientDrainInspector.ActiveStreamCount => ActiveClientStreamCount;

}
