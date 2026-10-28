using System.Reflection;
using System.Security.Cryptography;

namespace SharpLink.Server;

// The only issuer of a catalog quiescence proof. No RPC route, callback or public boolean can
// establish this authority; every stage is driven by exact-session framework-owned evidence.
internal sealed partial class ServerMaintenanceCoordinator : ISharpLinkMaintenanceController
{
    private readonly SharpLinkServer _server;
    private readonly Guid _domain;
    private readonly Guid _incarnation = Guid.NewGuid();
    private readonly Dictionary<Guid, SharpLinkMaintenanceParticipant> _participants;
    private readonly Lock _gate = new();
    private Operation? _active;

    internal ServerMaintenanceCoordinator(SharpLinkServer server, Guid domain,
        IReadOnlyList<SharpLinkMaintenanceParticipant> participants)
    {
        if (domain == Guid.Empty || participants.Count is 0 or > 1024)
            throw new ArgumentException("A nonempty domain and bounded complete participant roster are required.");
        _server = server;
        _domain = domain;
        _participants = [];
        var runtimes = new HashSet<Guid>();
        foreach (var participant in participants)
        {
            ArgumentNullException.ThrowIfNull(participant);
            if (participant.ParticipantId == Guid.Empty || participant.RuntimeIncarnation == Guid.Empty ||
                string.IsNullOrWhiteSpace(participant.AuthenticatedSubject) ||
                !_participants.TryAdd(participant.ParticipantId, participant) ||
                !runtimes.Add(participant.RuntimeIncarnation))
                throw new ArgumentException("Maintenance roster identities must be unique, exact and authenticated.");
        }
    }

    public long CatalogGeneration => _server.MaintenanceCatalogGeneration;
    internal bool IsOperationActive => Volatile.Read(ref _active) is not null;
    internal CooperativeMaintenanceMessage Welcome => new(CooperativeMaintenanceStage.Welcome,
        _domain, _incarnation, default, default, default, default, default, 0, 0);

    internal void Enroll(ServerConnectionState connection, SharpLinkAuthenticationContext authentication,
        CooperativeMaintenanceMessage enrollment)
    {
        lock (_gate)
        {
            if (_active is not null || !_server.MaintenanceServerRunning ||
                enrollment.Stage != CooperativeMaintenanceStage.Enroll ||
                enrollment.Domain != _domain || enrollment.Server != _incarnation ||
                !_participants.TryGetValue(enrollment.Participant, out var participant) ||
                participant.RuntimeIncarnation != enrollment.Runtime ||
                !string.Equals(authentication.Subject, participant.AuthenticatedSubject, StringComparison.Ordinal) ||
                authentication.IsExpired(_server.MaintenanceUtcNow) || connection.Session.MaintenanceEnrollment is not null)
                throw ProtocolV2FrameParser.Violation("Maintenance enrollment is unauthorized, stale or frozen.");
            connection.Session.MaintenanceEnrollment = enrollment with { Stage = CooperativeMaintenanceStage.Enrolled };
        }
    }

    public ISharpLinkMaintenanceOperation BeginMaintenance(long expectedGeneration,
        IReadOnlyList<SharpLinkImplementationReplacement> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        if (replacements.Count is 0 or > 4096)
            throw new ArgumentException("A bounded nonempty implementation replacement set is required.", nameof(replacements));
        var copied = replacements.Select(static item =>
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(item.Source);
            ArgumentNullException.ThrowIfNull(item.Target);
            return (OldAssembly: item.Source, NewAssembly: item.Target);
        }).ToArray();
        // Assembly metadata can be supplied by delegators with reentrant virtual callbacks.
        // Build the correlation digest before taking authority, then reject any nested winner.
        var operation = new Operation(this, Guid.NewGuid(), expectedGeneration, copied);
        lock (_gate)
        {
            if (_active is not null)
                throw new InvalidOperationException("Another maintenance operation owns this domain.");
            _active = operation;
            try { _server.StartMaintenanceOperation(operation); }
            catch { _active = null; throw; }
            return operation;
        }
    }

    internal void Handle(ServerConnectionState connection, CooperativeMaintenanceMessage message)
    {
        lock (_gate)
            (_active ?? throw ProtocolV2FrameParser.Violation("No maintenance operation is active.")).Handle(connection, message);
    }

    internal sealed class QuiescenceProof
    {
        private readonly Operation _operation;
        internal QuiescenceProof(Operation operation) => _operation = operation;
        internal void Validate(SharpLinkServer server, SharpLinkServer.PreparedImplementationReplacement plan)
        {
            if (!ReferenceEquals(server, _operation.Coordinator._server) ||
                !ReferenceEquals(plan, _operation.Plan) ||
                !ReferenceEquals(_operation.Coordinator._active, _operation) ||
                _operation.Stage is not (SharpLinkMaintenanceStage.Quiescent or
                    SharpLinkMaintenanceStage.Retiring or SharpLinkMaintenanceStage.CommittedPaused))
                throw new InvalidOperationException("Catalog proof is stale or belongs to another operation.");
            _operation.EnsureCohort();
            if (_operation.Connections.Any(static connection =>
                    connection.Session.ProtocolPhase != RpcSessionProtocolPhase.MaintenancePaused))
                throw new InvalidOperationException("Catalog proof requires every physical session to remain fenced.");
        }
    }

    internal sealed class Operation : ISharpLinkMaintenanceOperation
    {
        internal readonly ServerMaintenanceCoordinator Coordinator;
        private readonly long _expectedGeneration;
        private (Assembly OldAssembly, Assembly NewAssembly)[] _replacements;
        private readonly MaintenanceDigest _digest;
        private readonly Dictionary<ServerConnectionState, Peer> _peers = [];
        private readonly TaskCompletionSource<SharpLinkMaintenanceResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _stage = (int)SharpLinkMaintenanceStage.Validating;
        private bool _freezeStarted;
        internal SharpLinkServer.PreparedImplementationReplacement? Plan;
        internal ServerConnectionState[] Connections = [];

        internal Operation(ServerMaintenanceCoordinator coordinator, Guid id,
            long expectedGeneration, (Assembly OldAssembly, Assembly NewAssembly)[] replacements)
        {
            Coordinator = coordinator;
            OperationId = id;
            _expectedGeneration = expectedGeneration;
            _replacements = replacements;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(coordinator._domain.ToByteArray());
            hash.AppendData(coordinator._incarnation.ToByteArray());
            hash.AppendData(BitConverter.GetBytes(expectedGeneration));
            foreach (var replacement in replacements)
            {
                hash.AppendData(replacement.OldAssembly.ManifestModule.ModuleVersionId.ToByteArray());
                hash.AppendData(replacement.NewAssembly.ManifestModule.ModuleVersionId.ToByteArray());
            }
            _digest = MaintenanceDigest.Read(hash.GetHashAndReset());
        }

        public Guid OperationId { get; }
        public string PlanDigest => _digest.ToString();
        public SharpLinkMaintenanceStage Stage => (SharpLinkMaintenanceStage)Volatile.Read(ref _stage);
        public Task<SharpLinkMaintenanceResult> WaitAsync(CancellationToken cancellationToken = default)
            => cancellationToken.CanBeCanceled ? _completion.Task.WaitAsync(cancellationToken) : _completion.Task;
        internal Task CompletionTask => _completion.Task;
        internal void Start()
        {
            try { _ = Task.Run(RunAsync); }
            catch (Exception exception)
            {
                _replacements = [];
                Coordinator._active = null;
                SetStage(SharpLinkMaintenanceStage.RejectedBeforeSeal);
                _completion.TrySetResult(new(OperationId, PlanDigest, Stage, _expectedGeneration, exception));
            }
        }

        private void SetStage(SharpLinkMaintenanceStage stage) => Volatile.Write(ref _stage, (int)stage);

        internal void Handle(ServerConnectionState connection, CooperativeMaintenanceMessage message)
        {
            if (!_peers.TryGetValue(connection, out var peer) || !peer.Message.MatchesOperation(message))
                throw ProtocolV2FrameParser.Violation("Maintenance acknowledgement has the wrong operation, digest or incarnation.");
            var expected = peer.Stage switch
            {
                CooperativeMaintenanceStage.Freeze => CooperativeMaintenanceStage.IngressFence,
                CooperativeMaintenanceStage.ReturnFence => CooperativeMaintenanceStage.Quiescent,
                CooperativeMaintenanceStage.Commit => CooperativeMaintenanceStage.Committed,
                CooperativeMaintenanceStage.Resume => CooperativeMaintenanceStage.Resumed,
                _ => (CooperativeMaintenanceStage)0
            };
            if (message.Stage != expected)
                throw ProtocolV2FrameParser.Violation("Maintenance acknowledgement is replayed or out of order.");
            if (message.Stage == CooperativeMaintenanceStage.IngressFence)
                connection.Session.PauseForMaintenance(); // Reader cut, before the next Request is parsed.
            peer.Stage = message.Stage;
            peer.Completion.TrySetResult();
        }

        internal void EnsureCohort()
        {
            // Sample potentially application-defined time before the no-callback cohort check.
            var now = Coordinator._server.MaintenanceUtcNow;
            Coordinator._server.ValidateMaintenanceCohort(Connections);
            foreach (var connection in Connections)
            {
                var enrollment = connection.Session.MaintenanceEnrollment!.Value;
                if (!Coordinator._participants.TryGetValue(enrollment.Participant, out var participant) ||
                    participant.RuntimeIncarnation != enrollment.Runtime ||
                    connection.AuthenticationContext is not { } authentication || authentication.IsExpired(now) ||
                    !string.Equals(authentication.Subject, participant.AuthenticatedSubject, StringComparison.Ordinal))
                    throw new SharpLinkException(SharpLinkErrorCode.AuthenticationExpired,
                        "Maintenance participant authentication changed or expired while the domain was closed.");
            }
        }

        private async Task RunAsync()
        {
            Exception? failure = null;
            long targetGeneration = _expectedGeneration;
            try
            {
                if (Coordinator.CatalogGeneration != _expectedGeneration)
                    throw new InvalidOperationException("Maintenance source catalog generation changed.");
                Plan = Coordinator._server.PrepareImplementationReplacement(_replacements);
                if (Plan.BaseGeneration != _expectedGeneration)
                    throw new InvalidOperationException("Maintenance source catalog generation changed during preparation.");
                targetGeneration = Plan.TargetGeneration;
                Connections = Coordinator._server.CaptureMaintenanceCohort();
                ValidateRoster();
                using var disconnected = CancellationTokenSource.CreateLinkedTokenSource(
                    Connections.Select(static item => item.Session.LifetimeToken)
                        .Append(Coordinator._server.MaintenanceStopToken).ToArray());
                SetStage(SharpLinkMaintenanceStage.Freezing);
                _freezeStarted = true;
                await ExchangeAsync(CooperativeMaintenanceStage.Freeze, disconnected.Token).ConfigureAwait(false);
                SetStage(SharpLinkMaintenanceStage.Draining);
                while (Coordinator._server.HasMaintenanceOwners() ||
                       Connections.Any(static connection => connection.ActiveCalls != 0 ||
                           connection.CallCancellations.Count != 0 ||
                           connection.Session.StreamManager.ActiveStreamCount != 0))
                {
                    EnsureCohort();
                    await Task.Delay(1, disconnected.Token).ConfigureAwait(false);
                }
                foreach (var connection in Connections)
                    await connection.DrainPhysicalCancellationCallbacksAsync().WaitAsync(disconnected.Token).ConfigureAwait(false);
                foreach (var connection in Connections)
                    await connection.Session.CaptureBackpressuredSendDrain().WaitAsync(disconnected.Token).ConfigureAwait(false);
                await ExchangeAsync(CooperativeMaintenanceStage.ReturnFence, disconnected.Token).ConfigureAwait(false);
                EnsureCohort();
                SetStage(SharpLinkMaintenanceStage.Quiescent);
                var proof = new QuiescenceProof(this);
                SetStage(SharpLinkMaintenanceStage.Retiring);
                await Plan.SealOldAndReleaseAsync(proof).ConfigureAwait(false);
                Plan.CommitPaused(proof);
                _replacements = [];
                SetStage(SharpLinkMaintenanceStage.CommittedPaused);
                foreach (var connection in Connections)
                    connection.Session.SendContractManifest(Plan.TargetManifest!);
                await ExchangeAsync(CooperativeMaintenanceStage.Commit, disconnected.Token).ConfigureAwait(false);
                EnsureCohort();
                SetStage(SharpLinkMaintenanceStage.Resuming);
                foreach (var connection in Connections)
                    connection.Session.ResumeFromMaintenance();
                await ExchangeAsync(CooperativeMaintenanceStage.Resume, disconnected.Token).ConfigureAwait(false);
                EnsureCohort();
                Plan.Complete();
                SetStage(SharpLinkMaintenanceStage.Succeeded);
            }
            catch (Exception exception)
            {
                failure = exception;
                if (!_freezeStarted && Plan?.IsSealed != true)
                {
                    SetStage(SharpLinkMaintenanceStage.RejectedBeforeSeal);
                }
                else
                {
                    SetStage(SharpLinkMaintenanceStage.FailedClosed);
                    foreach (var connection in Connections)
                        connection.Session.NotifyDisconnected(exception);
                }
            }
            finally
            {
                _replacements = [];
                if (Plan is not null)
                {
                    try { await Plan.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception exception)
                    {
                        failure = failure is null ? exception : new AggregateException(failure, exception);
                        SetStage(SharpLinkMaintenanceStage.FailedClosed);
                        foreach (var connection in Connections)
                            connection.Session.NotifyDisconnected(exception);
                    }
                }
                if (Stage is SharpLinkMaintenanceStage.Succeeded or SharpLinkMaintenanceStage.RejectedBeforeSeal)
                {
                    lock (Coordinator._gate)
                        Coordinator._active = null;
                }
                _completion.TrySetResult(new SharpLinkMaintenanceResult(OperationId, PlanDigest, Stage, targetGeneration, failure));
            }
        }

        private void ValidateRoster()
        {
            var counts = new Dictionary<Guid, int>();
            var sessions = new HashSet<Guid>();
            foreach (var connection in Connections)
            {
                var enrollment = connection.Session.MaintenanceEnrollment!.Value;
                if (!Coordinator._participants.TryGetValue(enrollment.Participant, out var participant) ||
                    participant.RuntimeIncarnation != enrollment.Runtime || enrollment.Domain != Coordinator._domain ||
                    enrollment.Server != Coordinator._incarnation || !sessions.Add(enrollment.Session) ||
                    connection.AuthenticationContext is not { } authentication || authentication.IsExpired(Coordinator._server.MaintenanceUtcNow) ||
                    !string.Equals(authentication.Subject, participant.AuthenticatedSubject, StringComparison.Ordinal))
                    throw new InvalidOperationException("Maintenance cohort identity is invalid or expired.");
                counts[enrollment.Participant] = counts.GetValueOrDefault(enrollment.Participant) + 1;
            }
            if (counts.Count != Coordinator._participants.Count)
                throw new InvalidOperationException("Every configured participant runtime must be connected.");
            lock (Coordinator._gate)
                foreach (var connection in Connections)
                {
                    var enrollment = connection.Session.MaintenanceEnrollment!.Value;
                    _peers.Add(connection, new Peer(enrollment with
                    {
                        Operation = OperationId,
                        Digest = _digest,
                        SessionCount = counts[enrollment.Participant],
                        Generation = Plan!.TargetGeneration
                    }));
                }
        }

        private async Task ExchangeAsync(CooperativeMaintenanceStage stage, CancellationToken cancellationToken)
        {
            EnsureCohort();
            Task[] completions;
            lock (Coordinator._gate)
            {
                foreach (var peer in _peers.Values)
                {
                    peer.Stage = stage;
                    peer.Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                completions = _peers.Values.Select(static peer => peer.Completion.Task).ToArray();
            }
            foreach (var pair in _peers)
                await pair.Key.Session.SendMaintenanceAsync(pair.Value.Message with { Stage = stage }, cancellationToken)
                    .ConfigureAwait(false);
            await Task.WhenAll(completions).WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private sealed class Peer(CooperativeMaintenanceMessage message)
        {
            internal readonly CooperativeMaintenanceMessage Message = message;
            internal CooperativeMaintenanceStage Stage;
            internal TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
