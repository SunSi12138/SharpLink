namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private static class MaintenanceStorage
    {
        internal static System.Runtime.CompilerServices.ConditionalWeakTable<SharpLinkServer, ServerMaintenanceCoordinator>? Owners;
    }
    private ServerMaintenanceCoordinator? MaintenanceCoordinator => Volatile.Read(ref MaintenanceStorage.Owners)?.TryGetValue(this, out var owner) == true ? owner : null;
    internal bool MaintenanceConfigured => MaintenanceCoordinator is not null;
    private bool IsMaintenancePublicationClosed => MaintenanceCoordinator?.IsOperationActive == true || _serviceModuleRegistry.IsColdWriterHeld;
    internal bool MaintenanceServerRunning => CurrentState == ServerState.Running && !_lifecycle.HasStopStarted;
    internal long MaintenanceCatalogGeneration => Volatile.Read(ref _registryGeneration);
    internal DateTimeOffset MaintenanceUtcNow => _runtimeContext.TimeProvider.GetUtcNow();
    internal CancellationToken MaintenanceStopToken => _forceStopCts.Token;

    internal ISharpLinkMaintenanceController ConfigureMaintenance(
        Guid domainId, IReadOnlyList<SharpLinkMaintenanceParticipant> participants)
    {
        var coordinator = new ServerMaintenanceCoordinator(this, domainId, participants);
        lock (_stateGate)
            lock (_registryGate)
                lock (_telemetryDetailGate)
                {
                    if (CurrentState != ServerState.Created || MaintenanceCoordinator is not null)
                        throw new InvalidOperationException("Maintenance must be configured exactly once before starting.");
                    _serviceModuleRegistry.ValidateMaintenanceConfiguration();
                    _admissionController?.Kernel.ValidateMaintenanceEligibility();
                    LazyInitializer.EnsureInitialized(ref MaintenanceStorage.Owners).Add(this, coordinator);
                }
        return coordinator;
    }

    internal void StartMaintenanceOperation(ServerMaintenanceCoordinator.Operation operation)
    {
        lock (_stateGate)
            lock (_registryGate)
            {
                if (!MaintenanceServerRunning)
                    throw new InvalidOperationException("Server stop has closed maintenance admission.");
                // Register the terminal owner before scheduling any preparation. Stop publishes
                // Draining through these same cold boundaries before draining framework work.
                TrackFrameworkTask(operation.CompletionTask, "CooperativeMaintenance");
                operation.Start();
            }
    }

    internal ServerConnectionState[] CaptureMaintenanceCohort()
    {
        lock (_registryGate)
        {
            if (!MaintenanceServerRunning || _serviceModuleRegistry.HasRetirementFailure)
                throw new InvalidOperationException("Server cannot certify successful physical maintenance cleanup.");
            var connections = _connectionRegistry.SnapshotOwned();
            if (connections.Length == 0 || connections.Length > CooperativeMaintenanceMessage.MaximumSessions ||
                connections.Any(static connection => connection.LifecycleState != ServerConnectionLifecycleState.Ready ||
                    !connection.Session.IsConnected ||
                    connection.Session.MaintenanceEnrollment is not { Stage: CooperativeMaintenanceStage.Enrolled }))
                throw new InvalidOperationException("The whole server domain must be a stable enrolled physical-session cohort.");
            return connections;
        }
    }

    internal bool HasMaintenanceOwners()
        => _callAdmission.ActiveCallCount != 0 || _callAdmission.PendingCallAdmissions != 0 ||
           (_admissionController is not null && !_admissionController.Kernel.IsMaintenanceQuiescent) ||
           Volatile.Read(ref _deferredConnectionCleanups) != 0;

    internal void ValidateMaintenanceCohort(ServerConnectionState[] captured)
    {
        lock (_registryGate)
            if (_serviceModuleRegistry.HasRetirementFailure)
                throw new InvalidOperationException("A retired server owner failed physical cleanup.");
        if (!MaintenanceServerRunning || captured.Any(static connection => !connection.Session.IsConnected))
            throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "Maintenance cohort disconnected or server stopped.");
        var owned = _connectionRegistry.SnapshotOwned();
        if (owned.Length != captured.Length || owned.Any(connection => !captured.Contains(connection)))
            throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "Maintenance physical-session cohort changed.");
    }

    private void EnsureMaintenanceReadyPublicationAllowed()
    {
        if (IsMaintenancePublicationClosed)
            throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "Server Ready publication is frozen by maintenance.");
    }

    private async Task EnrollMaintenanceConnectionAsync(
        ServerConnectionState connection,
        SharpLinkAuthenticationContext? authentication,
        CancellationToken cancellationToken)
    {
        var coordinator = MaintenanceCoordinator;
        if (coordinator is null)
            return;
        var session = connection.Session;
        if (!session.HasAuthenticatedTransport || authentication?.Subject is null ||
            (session.NegotiatedCapabilities & ProtocolV2Capabilities.CooperativeMaintenance) == 0)
            throw new SharpLinkException(SharpLinkErrorCode.AuthenticationRejected,
                "Maintenance requires TLS, authenticated subject, and explicit client capability.");
        await session.SendMaintenanceAsync(coordinator.Welcome, cancellationToken).ConfigureAwait(false);
        var reader = session.Input;
        while (session.IsConnected)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;
            var complete = false;
            try
            {
                if (session.TryReadInboundFrame(ref buffer, _protocolOptions, out var header, out var payload))
                {
                    if (header.Type != ProtocolV2FrameType.CooperativeMaintenance)
                        throw ProtocolV2FrameParser.Violation("Enrollment must precede ordinary request admission.");
                    var enrollment = CooperativeMaintenanceMessage.Read(payload);
                    coordinator.Enroll(connection, authentication, enrollment);
                    complete = true;
                }
                else if (result.IsCompleted)
                    throw new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "Enrollment transport closed.");
            }
            finally
            {
                reader.AdvanceTo(buffer.Start, complete ? buffer.Start : buffer.End);
            }
            if (complete)
            {
                await session.SendMaintenanceAsync(session.MaintenanceEnrollment!.Value, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
        }
        throw new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "Enrollment transport closed.");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void HandleMaintenanceControl(ServerConnectionState connection, in ReadOnlySequence<byte> payload)
        => (MaintenanceCoordinator ?? throw ProtocolV2FrameParser.Violation(
            "Maintenance is not configured.")).Handle(connection, CooperativeMaintenanceMessage.Read(payload));
}
