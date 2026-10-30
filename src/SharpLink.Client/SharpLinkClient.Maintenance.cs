namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private static class MaintenanceStorage
    {
        internal static System.Runtime.CompilerServices.ConditionalWeakTable<SharpLinkClient, MaintenanceState>? Owners;
    }
    private MaintenanceState? MaintenanceOwner => Volatile.Read(ref MaintenanceStorage.Owners)?.TryGetValue(this, out var owner) == true ? owner : null;
    private SharpLinkClientMaintenanceOptions? MaintenanceOptions => MaintenanceOwner?.Options;
    private ClientMaintenanceOperation? MaintenanceOperation
    {
        get { var owner = MaintenanceOwner; return owner is null ? null : Volatile.Read(ref owner.Operation); }
        set { var owner = MaintenanceOwner ?? throw new InvalidOperationException("Maintenance is not configured."); Volatile.Write(ref owner.Operation, value); }
    }
    internal Func<CooperativeMaintenanceMessage, RpcSession, Task>? MaintenanceControlSendBarrierForTesting
    {
        get => MaintenanceOwner?.ControlSendBarrier;
        set
        {
            if (MaintenanceOwner is { } owner) owner.ControlSendBarrier = value;
            else if (value is not null) throw new InvalidOperationException("Maintenance is not configured.");
        }
    }

    private sealed class MaintenanceState(SharpLinkClientMaintenanceOptions options)
    {
        internal readonly SharpLinkClientMaintenanceOptions Options = options;
        internal ClientMaintenanceOperation? Operation;
        internal long LastGeneration;
        internal Guid LastServer;
        internal Func<CooperativeMaintenanceMessage, RpcSession, Task>? ControlSendBarrier;
    }

    internal void ConfigureMaintenance(SharpLinkClientMaintenanceOptions options)
    {
        if (options.DomainId == Guid.Empty || options.ParticipantId == Guid.Empty ||
            options.RuntimeIncarnation == Guid.Empty)
            throw new ArgumentException("Maintenance identities must be nonempty.", nameof(options));
        lock (_stateGate)
        {
            if (State != SharpLinkConnectionState.Created || _cluster is not null ||
                MaintenanceOptions is not null)
                throw new InvalidOperationException("Maintenance requires an unconnected fixed-endpoint client.");
            lock (_poolGate)
            {
                _assemblyRegistry.SealForCooperativeMaintenance();
                LazyInitializer.EnsureInitialized(ref MaintenanceStorage.Owners).Add(this, new MaintenanceState(options));
            }
        }
    }

    private bool IsMaintenanceFrozen => MaintenanceOperation is not null;

    private void ThrowIfMaintenanceFrozen()
    {
        if (IsMaintenanceFrozen)
            throw new InvalidOperationException("Client publication is frozen by cooperative maintenance.");
    }

    private ProtocolV2ContractManifest CaptureMaintenanceManifest(RpcSession session)
    {
        foreach (var binding in Volatile.Read(ref _remoteContractManifestSnapshot))
            if (ReferenceEquals(binding.Session, session))
                return binding.Manifest;
        throw CreateProtocolViolationException("Maintenance requires an established exact contract manifest.");
    }

    private async ValueTask HandleMaintenanceEnrollmentAsync(
        RpcSession session, CooperativeMaintenanceMessage message, CancellationToken cancellationToken)
    {
        var options = MaintenanceOptions ?? throw CreateProtocolViolationException(
            "The client did not opt into maintenance.");
        if (!session.HasAuthenticatedTransport ||
            (session.NegotiatedCapabilities & ProtocolV2Capabilities.CooperativeMaintenance) == 0 ||
            message.Domain != options.DomainId)
            throw CreateProtocolViolationException("Maintenance requires the configured TLS-authenticated domain.");
        if (message.Stage == CooperativeMaintenanceStage.Welcome && session.MaintenanceEnrollment is null)
        {
            var enrollment = message with
            {
                Stage = CooperativeMaintenanceStage.Enroll,
                Participant = options.ParticipantId,
                Runtime = options.RuntimeIncarnation,
                Session = Guid.NewGuid()
            };
            session.MaintenanceEnrollment = enrollment;
            await session.SendMaintenanceAsync(enrollment, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (message.Stage == CooperativeMaintenanceStage.Enrolled &&
            session.MaintenanceEnrollment is { Stage: CooperativeMaintenanceStage.Enroll } expected &&
            expected.MatchesOperation(message))
        {
            session.MaintenanceEnrollment = message;
            return;
        }
        throw CreateProtocolViolationException("Unexpected or mismatched maintenance enrollment.");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void HandleMaintenanceControl(ClientConnection connection, in ReadOnlySequence<byte> payload)
    {
        var message = CooperativeMaintenanceMessage.Read(payload);
        ClientMaintenanceOperation operation;
        lock (_poolGate)
        {
            if (MaintenanceOptions is null ||
                connection.Session.MaintenanceEnrollment is not { } enrollment ||
                message.Domain != enrollment.Domain || message.Server != enrollment.Server ||
                message.Participant != enrollment.Participant || message.Runtime != enrollment.Runtime ||
                message.Session != enrollment.Session)
                throw CreateProtocolViolationException("Maintenance control does not match the enrolled session.");
            if (message.Stage == CooperativeMaintenanceStage.Freeze && MaintenanceOperation is null)
            {
                var owner = MaintenanceOwner!;
                if ((message.Server == owner.LastServer && message.Generation <= owner.LastGeneration) || _poolStopping ||
                    Volatile.Read(ref _stopStarted) != 0)
                    throw CreateProtocolViolationException("Maintenance operation is stale or the client is stopping.");
                var captured = _connections.ToArray();
                if (captured.Length != message.SessionCount || captured.Length == 0 ||
                    captured.Any(item => item.State != ClientConnectionState.Ready ||
                        item.HasPlannedSessionRefreshRetirement ||
                        item.Session.MaintenanceEnrollment is not { Stage: CooperativeMaintenanceStage.Enrolled } peer ||
                        peer.Server != message.Server || peer.Runtime != message.Runtime))
                    throw CreateProtocolViolationException("Maintenance freeze does not cover the complete stable runtime cohort.");
                operation = new ClientMaintenanceOperation(this, captured, message);
                owner.LastGeneration = message.Generation;
                owner.LastServer = message.Server;
                MaintenanceOperation = operation;
                foreach (var capturedConnection in captured)
                    capturedConnection.PauseForMaintenance();
                PublishReadySnapshotLocked();
                TrackFrameworkTask(operation.FreezeAsync(), "CooperativeMaintenanceFreeze");
            }
            operation = MaintenanceOperation ?? throw CreateProtocolViolationException(
                "Maintenance operation is not active.");
            operation.Handle(connection, message);
        }
    }

    private sealed class ClientMaintenanceOperation(
        SharpLinkClient client,
        ClientConnection[] connections,
        CooperativeMaintenanceMessage first)
    {
        private readonly Dictionary<ClientConnection, CooperativeMaintenanceStage> _stages =
            connections.ToDictionary(static item => item, static _ => (CooperativeMaintenanceStage)0);
        private readonly Dictionary<ClientConnection, ProtocolV2ContractManifest> _sourceManifests =
            connections.ToDictionary(static item => item, item => client.CaptureMaintenanceManifest(item.Session));
        private bool _failed;
        private bool _ingressSent;

        private CooperativeMaintenanceMessage Message(ClientConnection connection, CooperativeMaintenanceStage stage)
            => first with { Stage = stage, Session = connection.Session.MaintenanceEnrollment!.Value.Session };

        internal void Handle(ClientConnection connection, CooperativeMaintenanceMessage message)
        {
            if (!_stages.TryGetValue(connection, out var prior) ||
                !Message(connection, message.Stage).MatchesOperation(message) || _failed)
                throw CreateProtocolViolationException("Maintenance operation or participant digest mismatch.");
            switch (message.Stage)
            {
                case CooperativeMaintenanceStage.Freeze when prior == 0:
                    _stages[connection] = message.Stage;
                    return;
                case CooperativeMaintenanceStage.ReturnFence when prior == CooperativeMaintenanceStage.Freeze && _ingressSent:
                    _stages[connection] = message.Stage;
                    client.TrackFrameworkTask(AcknowledgeReturnFenceAsync(connection), "CooperativeMaintenanceReturnFence");
                    return;
                case CooperativeMaintenanceStage.Commit when prior == CooperativeMaintenanceStage.Quiescent:
                    ValidateCommittedManifest(connection);
                    _stages[connection] = CooperativeMaintenanceStage.Committed;
                    client.TrackFrameworkTask(AcknowledgeCommitAsync(connection), "CooperativeMaintenanceCommit");
                    return;
                case CooperativeMaintenanceStage.Resume when prior == CooperativeMaintenanceStage.Committed:
                    _stages[connection] = CooperativeMaintenanceStage.Resume;
                    if (_stages.Values.All(static stage => stage == CooperativeMaintenanceStage.Resume))
                        client.TrackFrameworkTask(ResumeAsync(), "CooperativeMaintenanceResume");
                    return;
                default:
                    throw CreateProtocolViolationException("Maintenance control is replayed or out of order.");
            }
        }

        private void ValidateCommittedManifest(ClientConnection connection)
        {
            var source = _sourceManifests[connection];
            var current = client.CaptureMaintenanceManifest(connection.Session);
            if (current.Generation != first.Generation || current.Generation <= source.Generation ||
                current.Contracts.Count != source.Contracts.Count ||
                source.Contracts.Any(pair => !current.Contracts.TryGetValue(pair.Key, out var hash) || hash != pair.Value))
                throw CreateProtocolViolationException("Maintenance commit changed immutable contracts or has the wrong catalog generation.");
        }

        internal async Task FreezeAsync()
        {
            try
            {
                // The desired generation was permanently sealed at opt-in. Reconcile the final
                // snapshot here to join a Ready-publication reconciliation already in flight.
                await ApplyResponseCompressionPreferenceToCohortAsync(
                    connections.Where(static item => item.Session.HasNegotiatedCompression)
                        .Select(static item => item.Session).ToArray(),
                    client.CaptureResponseCompressionPreference(), client._shutdownCts.Token).ConfigureAwait(false);
                // Reservation -> pending/auxiliary ownership is an existing nonzero transfer.
                // Cancelled pending identities permanently reject late Request publication even
                // after resume; immutable code not yet selecting a session belongs to the new phase.
                while (true)
                {
                    lock (client._poolGate)
                    {
                        EnsureCohort();
                        if (_stages.Values.All(static stage => stage == CooperativeMaintenanceStage.Freeze) &&
                            connections.All(static item => item.CallAdmissionReservationCount == 0 && item.ActiveCallCount == 0))
                            break;
                    }
                    await Task.Delay(1, client._shutdownCts.Token).ConfigureAwait(false);
                }
                foreach (var connection in connections)
                    connection.Session.PauseForMaintenance();
                foreach (var connection in connections)
                    await connection.Session.CaptureBackpressuredSendDrain().ConfigureAwait(false);
                lock (client._poolGate)
                {
                    EnsureCohort();
                    _ingressSent = true;
                }
                foreach (var connection in connections)
                    await SendAsync(connection, CooperativeMaintenanceStage.IngressFence).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                FailClosed(exception);
            }
        }

        private async Task AcknowledgeReturnFenceAsync(ClientConnection connection)
        {
            try
            {
                // The reader has already parsed every preceding response/data frame. Existing
                // auxiliary owners retain asynchronous terminal/dispatcher cleanup.
                while (connection.ActiveCallCount != 0 || connection.Session.StreamManager.ActiveStreamCount != 0)
                {
                    lock (client._poolGate)
                        EnsureCohort();
                    await Task.Delay(1, client._shutdownCts.Token).ConfigureAwait(false);
                }
                await connection.Session.CaptureBackpressuredSendDrain().ConfigureAwait(false);
                lock (client._poolGate)
                {
                    EnsureCohort();
                    _stages[connection] = CooperativeMaintenanceStage.Quiescent;
                }
                await SendAsync(connection, CooperativeMaintenanceStage.Quiescent).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                FailClosed(exception);
            }
        }

        private async Task AcknowledgeCommitAsync(ClientConnection connection)
        {
            try { await SendAsync(connection, CooperativeMaintenanceStage.Committed).ConfigureAwait(false); }
            catch (Exception exception) { FailClosed(exception); }
        }

        private async Task ResumeAsync()
        {
            try
            {
                lock (client._poolGate)
                {
                    EnsureCohort();
                    foreach (var connection in connections)
                        connection.ResumeFromMaintenance();
                    client.MaintenanceOperation = null;
                    client.PublishReadySnapshotLocked();
                }
                client.PublishReadyState();
                foreach (var connection in connections)
                    await SendAsync(connection, CooperativeMaintenanceStage.Resumed).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                FailClosed(exception);
            }
        }

        private async Task SendAsync(ClientConnection connection, CooperativeMaintenanceStage stage)
        {
            try
            {
                var message = Message(connection, stage);
                if (client.MaintenanceControlSendBarrierForTesting is { } barrier)
                    await barrier(message, connection.Session).ConfigureAwait(false);
                await connection.Session.SendMaintenanceAsync(message, client._shutdownCts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                FailClosed(exception);
                throw;
            }
        }

        private void EnsureCohort()
        {
            if (_failed || client._poolStopping || Volatile.Read(ref client._stopStarted) != 0 ||
                client._connections.Count != connections.Length ||
                connections.Any(item => !client._connections.Contains(item) || !item.Session.IsConnected ||
                    item.HasObservedFatalFailureForAdmission || item.State != ClientConnectionState.MaintenancePaused))
                throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "Maintenance cohort changed or disconnected.");
        }

        private void FailClosed(Exception exception)
        {
            lock (client._poolGate)
            {
                _failed = true;
                client.MaintenanceOperation = this;
                foreach (var connection in connections)
                    connection.ObserveFatalFailureForAdmission();
                client.PublishReadySnapshotLocked();
            }
            foreach (var connection in connections)
                connection.Session.NotifyDisconnected(exception);
        }
    }
}
