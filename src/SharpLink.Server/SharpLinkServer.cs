using System.Reflection;

namespace SharpLink.Server;

internal sealed partial class SharpLinkServer : ISharpLinkServer
{
    internal enum ServerState
    {
        Created,
        Starting,
        Running,
        Draining,
        Stopped,
        Faulted
    }

    private readonly IServerTransportListener _transportListener;
    private readonly TimeSpan _heartbeatCheckInterval;
    private readonly TimeSpan _heartbeatTimeout;
    private readonly SharpLinkRuntimeContext _runtimeContext;
    private readonly ServerServiceModuleRegistry _serviceModuleRegistry;
    private readonly ServerConnectionRegistry _connectionRegistry = new();
    private readonly IServiceProvider _serviceProvider;
    private readonly IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> _staticManifests;
    private readonly ILogger _logger;
    private readonly ServerAuthenticationCoordinator _authentication;
    private readonly FrameworkTaskSupervisor _frameworkTasks;
    private int _state = (int)ServerState.Created;
    private readonly SharpLinkProtocolOptions _protocolOptions;
    private readonly int _maxConcurrentCallsPerConnection;
    private readonly int _maxConcurrentCallsPerServer;
    private readonly RpcSessionFlushOptions? _rpcSessionFlushOptions;
    private ServerInterceptorGeneration _serverInterceptorGeneration;
    private readonly IRpcExceptionMapper _exceptionMapper;
    private readonly ServerServiceCleanup _serviceCleanup;
    private readonly SharpLinkAdmissionController? _admissionController;
    private readonly ServerConnectionAdmission _connectionAdmission;
    private readonly ServerCallAdmission _callAdmission;
    private readonly ServerShutdownPlan _shutdownPlan;
    private readonly ServerLifecycleCoordinator _lifecycle;
    private int _deferredConnectionCleanups;
    private long _rejectedOneWayCalls;
    private FixedWindowLogThrottle _connectionAdmissionLogThrottle;
    private FixedWindowLogThrottle _oneWayAdmissionLogThrottle;
    private FixedWindowLogThrottle _protocolViolationLogThrottle;

    // Keep the established partial-file transaction shape while the mutable registry state itself
    // is owned by the focused collaborator. These are non-owning aliases, not Server fields.
    private ref FrozenDictionary<long, ServiceRegistration> _services
        => ref _serviceModuleRegistry.ServicesStorage;
    private Lock _registryGate => _serviceModuleRegistry.Gate;
    private ServerServiceModuleRegistry.DynamicModuleTable _dynamicModules
        => _serviceModuleRegistry.DynamicModules;
    private ServerServiceModuleRegistry.UnregisterOperationTable _unregisterOperations
        => _serviceModuleRegistry.UnregisterOperations;
    private ServerServiceModuleRegistry.DetachedModuleServiceTable _detachedModuleServices
        => _serviceModuleRegistry.DetachedModuleServices;
    private ref long _registryGeneration => ref _serviceModuleRegistry.GenerationStorage;

    // Existing call/session code consumes the force-stop source through this non-owning alias.
    private CancellationTokenSource _forceStopCts => _lifecycle.ForceStopSource;

    /// <summary>
    /// Initializes a Server from the explicit composition materialized by
    /// <see cref="SharpLinkServerBuilder"/>. It performs no mutable-option fallback, clone, catalog
    /// lookup, listener/service-provider/FrameworkTaskSupervisor creation, or RuntimeContext materialization.
    /// </summary>
    internal SharpLinkServer(ServerRuntimeComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        _transportListener = composition.TransportListener;
        _serviceModuleRegistry = new ServerServiceModuleRegistry(composition.Services);
        _heartbeatCheckInterval = composition.HeartbeatCheckInterval;
        _heartbeatTimeout = composition.HeartbeatTimeout;
        _logger = composition.Logger;
        _runtimeContext = composition.RuntimeContext;
        _responseCompressionPolicy = CompressionSendPolicyState.CreateInitial(composition.ResponseCompressionPolicy);
        _authentication = composition.Authentication;
        _protocolOptions = composition.ProtocolOptions;
        _rpcSessionFlushOptions = composition.RpcSessionFlushOptions;
        _serverInterceptorGeneration = ServerInterceptorGeneration.Create(composition.Interceptors);
        _exceptionMapper = composition.ExceptionMapper;
        _serviceProvider = composition.ServiceProvider;
        _staticManifests = composition.StaticManifests;
        _admissionController = composition.AdmissionController;
        _connectionAdmission = composition.ConnectionAdmission;
        _shutdownPlan = composition.ShutdownPlan;
        _maxConcurrentCallsPerConnection = _runtimeContext.FlowControl.MaxConcurrentCallsPerConnection;
        _maxConcurrentCallsPerServer = _runtimeContext.FlowControl.MaxConcurrentCallsPerServer;
        _callAdmission = new ServerCallAdmission(
            this,
            _maxConcurrentCallsPerConnection,
            _maxConcurrentCallsPerServer);
        _serviceCleanup = composition.ServiceCleanup;
        _frameworkTasks = composition.FrameworkTasks;
        var logWindow = TimeSpan.FromSeconds(5);
        var timestampFrequency = _runtimeContext.TimeProvider.TimestampFrequency;
        _connectionAdmissionLogThrottle = new FixedWindowLogThrottle(logWindow, timestampFrequency);
        _oneWayAdmissionLogThrottle = new FixedWindowLogThrottle(logWindow, timestampFrequency);
        _protocolViolationLogThrottle = new FixedWindowLogThrottle(logWindow, timestampFrequency);
        _lifecycle = new ServerLifecycleCoordinator(this);
    }

    public SharpLinkHealthStatus HealthStatus => _lifecycle.HealthStatus;

    public ValueTask DisposeAsync() => StopAsync(TimeSpan.Zero);

    public ValueTask StopAsync(
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default)
        => _lifecycle.StopAsync(gracefulTimeout, cancellationToken);

    internal void TrackFrameworkTask(
        Task task,
        string operation,
        TaskObservationMode observationMode = TaskObservationMode.FrameworkOwned)
        => _frameworkTasks.Track(task, operation, observationMode, IsExpectedSessionShutdownException);

    /// <summary>Exposes the pre-call connection admission gate for diagnostics and tests.</summary>
    internal ServerConnectionAdmission ConnectionAdmission => _connectionAdmission;

    internal void RecordConnectionAdmissionRejection(string reason)
    {
        SharpLinkTelemetry.RecordConnectionRejected(reason);
        if (ShouldLogConnectionAdmissionRejection())
            LogConnectionAdmissionRejected(_logger, reason);
    }

    private bool ShouldLogConnectionAdmissionRejection()
        => _connectionAdmissionLogThrottle.ShouldLog(
            _runtimeContext.TimeProvider.GetTimestamp(),
            out _);

    private static bool IsExpectedSessionShutdownException(Exception exception)
        => exception is OperationCanceledException or ObjectDisposedException or
            System.IO.IOException or SocketException or
            SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed };

    private SharpLinkCallContextSnapshot CreateCallContext(
        ServerConnectionState connection,
        IRpcStub stub,
        long methodId,
        long requestId,
        RpcDeadline deadline,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken)
    {
        var session = connection.Session;
        var interceptors = Volatile.Read(ref _serverInterceptorGeneration);
        if (interceptors.Count == 0)
            return connection.GetCallContextSnapshot(deadline, metadata);

        var method = GetMethodDescriptor(stub, methodId);
        if (method.Kind != RpcMethodKind.OneWay)
        {
            ReservePreInvocationRequestStreams(
                session,
                method.ClientStreamCount,
                requestId,
                cancellationToken);
        }

        return CreateServerInvocationContext(
            session,
            stub,
            methodId,
            requestId,
            connection.AuthenticationContext,
            deadline,
            _runtimeContext.TimeProvider,
            metadata,
            cancellationToken,
            interceptors);
    }

    private static SharpLinkServerInvocationContext CreateServerInvocationContext(
        RpcSession session,
        IRpcStub stub,
        long methodId,
        long requestId,
        SharpLinkAuthenticationContext? authenticationContext,
        RpcDeadline deadline,
        TimeProvider deadlineTimeProvider,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken,
        ServerInterceptorGeneration? interceptors = null)
    {
        var method = GetMethodDescriptor(stub, methodId);
        return new SharpLinkServerInvocationContext(
            method,
            requestId,
            session.Id,
            session.LocalEndPoint,
            session.RemoteEndPoint,
            authenticationContext,
            deadline,
            deadlineTimeProvider,
            metadata,
            cancellationToken,
            interceptors);
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

    internal ServerCallAdmissionResult TryAcquireCall(ServerConnectionState connection)
        => _callAdmission.TryAcquireCall(connection);

    private static string GetCallCapacityExhaustionReason(ServerCallAdmissionResult result)
        => result switch
        {
            ServerCallAdmissionResult.PerConnectionCapacityExhausted =>
                SharpLinkResourceExhaustion.PerConnectionCallCapacity,
            ServerCallAdmissionResult.ServerCapacityExhausted =>
                SharpLinkResourceExhaustion.ServerCallCapacity,
            _ => throw new ArgumentOutOfRangeException(nameof(result), result, "A capacity result is required.")
        };

    private bool TryAcceptRequest(ServerConnectionState connection, long requestId)
    {
        if (CurrentState != ServerState.Running)
            return false;
        if (!connection.TryRecordAcceptedRequest(requestId))
            return false;
        return CurrentState == ServerState.Running &&
               connection.LifecycleState == ServerConnectionLifecycleState.Ready;
    }

    internal void ReleaseCall(ServerConnectionState connection)
        => _callAdmission.ReleaseCall(connection);

    private void TrySignalCallsDrained(ServerConnectionState? releasingConnection = null)
        => _lifecycle.TrySignalCallsDrained(releasingConnection);

    internal int ActiveCallCountForDiagnostics => _callAdmission.ActiveCallCount;

    internal int PendingCallAdmissionsForDiagnostics => _callAdmission.PendingCallAdmissions;

    internal Task<bool> CallsDrainedForDiagnostics => _lifecycle.CallsDrainedForDiagnostics;

    internal ServerCallDrainSignalSnapshot? LastCallDrainSignalForDiagnostics
        => _lifecycle.LastCallDrainSignalForDiagnostics;

    internal void AssertCallAccountingInvariant()
        => _lifecycle.AssertCallAccountingInvariant();

    internal int MaxConcurrentCallsPerConnectionForDiagnostics => _maxConcurrentCallsPerConnection;

    internal int MaxConcurrentCallsPerServerForDiagnostics => _maxConcurrentCallsPerServer;

    internal ServerStopDiagnosticSnapshot? LastStopDiagnostics
        => _lifecycle.LastStopDiagnostics;

    internal FrameworkTaskSupervisorSnapshot FrameworkTaskSnapshotForDiagnostics
        => _frameworkTasks.CaptureSnapshot();

    internal ServerShutdownPlan ShutdownPlanForDiagnostics => _shutdownPlan;

    internal ServerDeferredTaskDiagnosticSnapshot DeferredTaskSnapshotForDiagnostics
        => _lifecycle.CaptureDeferredTaskSnapshot(
            Volatile.Read(ref _deferredConnectionCleanups));

    private ServerStopDiagnosticSnapshot CaptureStopDiagnostics(int activeCalls)
    {
        var connections = _connectionRegistry.SnapshotActive();
        var snapshots = new ServerConnectionDiagnosticSnapshot[connections.Length];
        for (var index = 0; index < connections.Length; index++)
        {
            snapshots[index] = connections[index]
                .CaptureStopDiagnostics(_maxConcurrentCallsPerConnection);
        }
        return new ServerStopDiagnosticSnapshot(
            _runtimeContext.TimeProvider.GetUtcNow(),
            activeCalls,
            snapshots);
    }

    private ServerState CurrentState => (ServerState)Volatile.Read(ref _state);

    private void TransitionTo(ServerState state)
        => Interlocked.Exchange(ref _state, (int)state);

    internal void ForceStop() => _lifecycle.ForceStop();

    internal static FrameworkTaskSupervisor CreateFrameworkTaskSupervisor(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return new FrameworkTaskSupervisor((operation, exception) =>
            LogServerBackgroundLoopUnhandledException(logger, operation, exception));
    }
}
