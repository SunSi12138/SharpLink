namespace SharpLink.Client;

internal readonly record struct ContractSiteKey(long LocalGeneration, long ContractId, RpcHash128 Hash,
    RpcRegistrationRequirement Requirement, RpcRegistrationReceipt? Receipt);
internal sealed record BoundContractTarget(ClientConnection Connection, long SessionGeneration, ulong Token);
internal sealed class ContractTargetSnapshot(BoundContractTarget[] targets, ContractClusterSnapshot? cluster)
{
    internal static ContractTargetSnapshot Empty { get; } = new([], null);
    internal ContractClusterSnapshot? Cluster { get; } = cluster;
    internal BoundContractTarget[] Targets { get; } = targets;
    internal ClientConnection[] Connections { get; } = targets.Select(static target => target.Connection).ToArray();
    internal int ActiveCallCount { get { var count = 0; foreach (var c in Connections) count += c.ActiveCallCount; return count; } }
    internal bool HasSameRouting(ContractTargetSnapshot other)
    {
        if (!ReferenceEquals(Cluster?.SourceTopology, other.Cluster?.SourceTopology) || Targets.Length != other.Targets.Length) return false;
        foreach (var target in Targets)
            if (!other.Targets.Any(candidate => ReferenceEquals(candidate.Connection, target.Connection) && candidate.Token == target.Token && candidate.SessionGeneration == target.SessionGeneration)) return false;
        return true;
    }
}

internal sealed partial class SharpLinkClient
{
    private readonly Guid _localContractIssuer = Guid.NewGuid();
    private readonly ConditionalWeakTable<ClientProxyRegistrationIdentity, LocalContractIdentity> _localContractIdentities = new();
    private readonly Lock _contractSiteGate = new();
    private readonly Dictionary<ContractSiteKey, WeakReference<ContractSite>> _contractSites = [];
    private readonly ContractBindingQuota _contractBindingQuota = new();
    private long _localContractGeneration, _bindingSessionGeneration;
    private int _prepareWaiters;
    private readonly CancellationToken _contractShutdownToken;
    private sealed record LocalContractIdentity(long Generation, RpcHash128 Hash);
    internal long NextBindingSessionGeneration()
    {
        lock (_contractSiteGate)
        {
            if (_bindingSessionGeneration == long.MaxValue) throw new InvalidOperationException("Session generations are exhausted.");
            return ++_bindingSessionGeneration;
        }
    }
    internal ContractBindingDiagnosticSnapshot CaptureContractBindingDiagnostics()
    {
        int sites; var targets = 0;
        lock (_contractSiteGate)
        {
            sites = _contractSites.Count;
            foreach (var weak in _contractSites.Values) if (weak.TryGetTarget(out var site)) targets += site.PublishedTargetCount;
        }
        return new(_contractBindingQuota.Bindings, _contractBindingQuota.Operations, 0, sites,
            Volatile.Read(ref _prepareWaiters), CaptureBindingConnections().Sum(static c => c.Bindings.ReceivedUnpublishedAcks), targets);
    }
    internal ContractBindingQuota BindingQuota => _contractBindingQuota;
    internal TimeProvider BindingTimeProvider => _runtimeContext.TimeProvider;
    internal bool TryStartBindingWork(Func<Task> factory, string operation)
    {
        lock (_poolGate)
        {
            if (_poolStopping) return false;
            TrackFrameworkTask(Task.Run(factory), operation);
            return true;
        }
    }
    internal void RefreshBindingSites() => ScheduleContractSiteRefresh();

    public RpcLocalContract GetLocalContract<TContract>() where TContract : IService
    {
        EnsureContractClientActive();
        if (!_assemblyRegistry.CaptureProxySnapshot().TryGetValue(typeof(TContract), out var registration))
            throw LocalContractError(SharpLinkErrorDetails.ContractBinding.LocalContractUnavailable);
        return CaptureLocalContract(registration);
    }
    public RpcLocalContract GetLocalContract(System.Reflection.Assembly registeredAssembly, long contractId)
    {
        ArgumentNullException.ThrowIfNull(registeredAssembly);
        if (contractId == 0) throw new ArgumentException("Contract ID must be nonzero.", nameof(contractId));
        EnsureContractClientActive();
        var matches = _assemblyRegistry.CaptureProxySnapshot().Values.Where(r => r.Descriptor.ContractId == contractId &&
            ReferenceEquals(FindOwningManifest(r).OwnerAssembly, registeredAssembly)).ToArray();
        if (matches.Length != 1) throw LocalContractError(SharpLinkErrorDetails.ContractBinding.LocalContractUnavailable);
        return CaptureLocalContract(matches[0]);
    }
    private RpcLocalContract CaptureLocalContract(ClientProxyRegistration registration)
    {
        if (registration.Module is not null) throw ContractBindingWire.Error(ContractBindStatus.DynamicUnsupported);
        var hash = FindOwningManifest(registration).RpcAssemblyHash;
        if (hash.IsEmpty) throw LocalContractError(SharpLinkErrorDetails.ContractBinding.InvalidLocalContract);
        var identity = _localContractIdentities.GetValue(registration.Identity, _ =>
        {
            lock (_contractSiteGate)
            {
                if (_localContractGeneration == long.MaxValue) throw new InvalidOperationException("Local contract generations are exhausted.");
                return new(++_localContractGeneration, hash);
            }
        });
        if (identity.Hash != hash) throw LocalContractError(SharpLinkErrorDetails.ContractBinding.InvalidLocalContract);
        return new(_localContractIssuer, identity.Generation, registration.Descriptor.ContractId, hash);
    }
    public RpcProxy<TContract> CreateProxy<TContract>(RpcProxyOptions? options = null) where TContract : IService
    {
        var captured = CaptureContractOptions(options);
        ValidateContractMetadata(captured);
        return CreateProxyCaptured<TContract>(captured);
    }
    internal RpcProxy<TContract> CreateProxyCaptured<TContract>(CapturedContractOptions options) where TContract : IService
    {
        var local = GetLocalContract<TContract>();
        var channel = AcquireContractChannel(local, options);
        try
        {
            var registration = channel.Site.Registration;
            var service = (TContract)registration.Descriptor.ProxyFactory(channel, registration.Codecs);
            var proxy = new RpcProxy<TContract>(service, channel);
            CompleteContractConstruction(channel.Site, success: true);
            return proxy;
        }
        catch { CompleteContractConstruction(channel.Site, success: false); throw; }
    }
    public IRpcContractChannel CreateContractChannel(RpcLocalContract local, RpcProxyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(local);
        var captured = CaptureContractOptions(options);
        ValidateContractMetadata(captured);
        var channel = AcquireContractChannel(local, captured);
        try { CompleteContractConstruction(channel.Site, success: true); return channel; }
        catch { CompleteContractConstruction(channel.Site, success: false); throw; }
    }
    private ContractChannel AcquireContractChannel(RpcLocalContract local, CapturedContractOptions options)
    {
        ArgumentNullException.ThrowIfNull(local); EnsureContractClientActive();
        if (local.Issuer != _localContractIssuer) throw LocalContractError(SharpLinkErrorDetails.ContractBinding.ForeignLocalContract);
        var registration = ResolveLocalContract(local);
        var receipt = options.Receipt; var requirement = options.Requirement;
        if (receipt is { } r && (r.ContractId != local.ContractId || r.RpcAssemblyHash != local.Hash))
            throw LocalContractError(SharpLinkErrorDetails.ContractBinding.InvalidLocalContract);
        var methodFacts = new Dictionary<long, RpcMethodDescriptor>(registration.Descriptor.Methods.Count);
        foreach (var method in registration.Descriptor.Methods)
            if (method.MethodId != method.Facts.MethodId || method.Kind != method.Facts.Kind ||
                method.SupportsCancellation != method.Facts.SupportsCancellation || !ContractMethodFacts.IsValid(method.Facts, local.ContractId) || !methodFacts.TryAdd(method.MethodId, method.Facts))
                throw LocalContractError(SharpLinkErrorDetails.ContractBinding.InvalidLocalContract);
        var localMethods = methodFacts.ToFrozenDictionary();
        var key = new ContractSiteKey(local.Generation, local.ContractId, local.Hash, requirement, receipt);
        ContractSite site; ContractChannel channel;
        lock (_contractSiteGate)
        {
            foreach (var dead in _contractSites.Where(static pair => !pair.Value.TryGetTarget(out _)).Select(static pair => pair.Key).ToArray())
                _contractSites.Remove(dead);
            if (!_contractSites.TryGetValue(key, out var weak) || !weak.TryGetTarget(out site!))
            {
                if (_contractSites.Count >= 64) throw ContractBindingWire.Error(ContractBindStatus.Capacity);
                site = new(this, local, key, registration, localMethods);
                channel = new ContractChannel(site, options.Metadata);
                _contractSites.Add(key, new(site));
            }
            else channel = new ContractChannel(site, options.Metadata);
            site.ConstructionReservations++;
        }
        return channel;
    }
    private void CompleteContractConstruction(ContractSite site, bool success)
    {
        lock (_poolGate)
        lock (_contractSiteGate)
        {
            if (success) EnsureContractClientActive();
            site.ConstructionReservations--;
            if (success) site.Committed = true;
            else if (!site.Committed && site.ConstructionReservations == 0) _contractSites.Remove(site.Key);
        }
        if (success) ScheduleContractSiteRefresh();
    }
    private bool QualifyBindingConnection(ContractSiteKey key, ClientConnection connection)
    {
        if (key.Receipt is { } receipt)
        {
            if (connection.Session.BindingServerInstanceId != receipt.ServerInstanceId || connection.Session.BindingRegistryEpoch != receipt.RegistryEpoch)
                throw ContractBindingWire.Error(ContractBindStatus.ReceiptMismatch);
            return true;
        }
        foreach (var binding in Volatile.Read(ref _remoteContractManifestSnapshot))
        {
            if (!ReferenceEquals(binding.Session, connection.Session)) continue;
            if (!binding.Manifest.Contracts.TryGetValue(key.ContractId, out var hash)) return false;
            if (hash != key.Hash) throw ContractBindingWire.Error(ContractBindStatus.HashMismatch);
            return true;
        }
        return false;
    }
    private ClientProxyRegistration ResolveLocalContract(RpcLocalContract local)
    {
        foreach (var registration in _assemblyRegistry.CaptureProxySnapshot().Values)
            if (registration.Descriptor.ContractId == local.ContractId && _localContractIdentities.TryGetValue(registration.Identity, out var identity) &&
                identity.Generation == local.Generation && registration.Module is null && identity.Hash == local.Hash)
                return registration;
        throw LocalContractError(SharpLinkErrorDetails.ContractBinding.LocalContractUnavailable);
    }
    private static SharpLinkException LocalContractError(ushort detail)
        => new(SharpLinkErrorCode.FailedPrecondition, detail, "The exact local contract selector is unavailable or invalid.");
    private void EnsureContractClientActive()
    {
        if (_shutdownCts.IsCancellationRequested || Volatile.Read(ref _stopStarted) != 0)
            throw CreateConnectionClosedException("Client is not accepting contract operations.");
    }
    internal readonly record struct CapturedContractOptions(SharpLinkMetadata? Metadata,
        RpcRegistrationReceipt? Receipt, RpcRegistrationRequirement Requirement, int MetadataBytes);
    internal static CapturedContractOptions CaptureContractOptions(RpcProxyOptions? options)
    {
        var metadata = options?.Metadata; var receipt = options?.Receipt;
        var requirement = options?.Requirement ?? RpcRegistrationRequirement.Compatible;
        if (!Enum.IsDefined(requirement)) throw new ArgumentOutOfRangeException(nameof(options));
        if (receipt is not { } value)
        { if (requirement != RpcRegistrationRequirement.Compatible) throw new ArgumentException("This requirement needs a receipt.", nameof(options)); }
        else if (value.ServerInstanceId == Guid.Empty || value.RegistryEpoch == Guid.Empty || value.Generation < 0 ||
            value.ContractId == 0 || value.RpcAssemblyHash.IsEmpty || value.RegistrationId <= 0)
            throw new ArgumentException("Registration receipt structure is invalid.", nameof(options));
        return new(metadata, receipt, requirement, metadata is null ? 0 : ProtocolV2PayloadCodec.GetMetadataPayloadLength(metadata));
    }
    internal int ContractMetadataLimit => _protocolOptions.MaxMetadataBytes;
    internal void ValidateContractMetadata(CapturedContractOptions options) => ValidateContractMetadata(options, ContractMetadataLimit);
    internal static void ValidateContractMetadata(CapturedContractOptions options, int limit)
    {
        if (options.MetadataBytes > limit)
            throw new ArgumentOutOfRangeException(nameof(options), "Metadata exceeds the configured limit.");
    }
    internal static bool IsExpectedContractRefreshStopExit(bool stopStarted, Exception exception)
        => stopStarted && exception is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed };
    private void StopContractSitesLocked()
    {
        lock (_contractSiteGate)
        {
            foreach (var weak in _contractSites.Values) if (weak.TryGetTarget(out var site)) site.Stop();
            _contractSites.Clear();
        }
    }
    private void ScheduleContractSiteRefresh()
    {
        ContractSite[] sites; HashSet<ContractSiteKey> liveKeys;
        lock (_contractSiteGate)
        {
            foreach (var dead in _contractSites.Where(static pair => !pair.Value.TryGetTarget(out _)).Select(static pair => pair.Key).ToArray())
                _contractSites.Remove(dead);
            sites = _contractSites.Values.Select(w => w.TryGetTarget(out var site) ? site : null).OfType<ContractSite>().Where(static site => site.Committed).ToArray();
            liveKeys = sites.Select(static site => site.Key).ToHashSet();
        }
        foreach (var connection in CaptureBindingConnections()) connection.Bindings.Prune(liveKeys);
        foreach (var site in sites) site.ScheduleRefresh();
    }
    private ClientConnection[] CaptureBindingConnections() => _cluster?.CaptureReadyConnections() ?? Volatile.Read(ref _readyConnections);

    internal sealed class ContractSite
    {
        internal readonly SharpLinkClient _client;
        private readonly RpcLocalContract _local;
        internal ContractSiteKey Key { get; }
        internal ClientProxyRegistration Registration { get; }
        private readonly ClientProxyRegistrationIdentity _registrationIdentity;
        private readonly FrozenDictionary<long, RpcMethodDescriptor> _localMethods;
        internal int ConstructionReservations;
        internal bool Committed;
        private readonly Lock _gate = new();
        private ContractTargetSnapshot _targets = ContractTargetSnapshot.Empty;
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _running, _dirty, _stopped;
        private string _refreshPhase = "NotStarted";
        private int _capturedConnections, _qualifiedConnections;
        private Exception? _refreshError;
        internal ContractSite(SharpLinkClient client, RpcLocalContract local, ContractSiteKey key, ClientProxyRegistration registration, FrozenDictionary<long, RpcMethodDescriptor> localMethods)
        { _client = client; _local = local; Key = key; Registration = registration; _registrationIdentity = registration.Identity; _localMethods = localMethods; }
        private void EnsureCapturedLocalAvailable()
        {
            _client.EnsureContractClientActive();
            if (!_registrationIdentity.IsAvailable)
                throw LocalContractError(SharpLinkErrorDetails.ContractBinding.LocalContractUnavailable);
        }
        internal void Validate(RpcMethodDescriptor method, RpcMethodKind kind)
        {
            EnsureCapturedLocalAvailable();
            if (method.ContractId != Key.ContractId || !_localMethods.TryGetValue(method.MethodId, out var facts) || facts != method)
                throw LocalContractError(SharpLinkErrorDetails.ContractBinding.InvalidLocalContract);
            if (method.Kind != kind || !ContractMethodFacts.IsStaticSlice(method))
                throw ContractBindingWire.Error(ContractBindStatus.UnsupportedShape);
        }
        internal void Stop()
        {
            lock (_gate) { _stopped = true; _targets = ContractTargetSnapshot.Empty; _dirty = false; _changed.TrySetResult(); }
        }
        internal void ScheduleRefresh()
        {
            lock (_gate)
            {
                _dirty = true;
                if (_running || _stopped || _client._shutdownCts.IsCancellationRequested) return;
                _running = true;
            }
            if (!_client.TryStartBindingWork(RefreshAsync, "ContractSiteRefresh"))
            {
                TaskCompletionSource changed;
                lock (_gate) { _running = false; _refreshError = CreateConnectionClosedException("Client is stopping."); changed = _changed; }
                changed.TrySetResult();
            }
        }
        private async Task RefreshAsync()
        {
            try { await RefreshCoreAsync().ConfigureAwait(false); }
            catch (Exception exception)
            {
                TaskCompletionSource changed;
                lock (_gate) { _refreshError = exception; _running = false; _dirty = false; _refreshPhase = "Faulted"; changed = _changed; }
                changed.TrySetResult();
                // Explicit Stop can cut this task before the supervisor is sealed. It is a
                // control lifetime exit, not an unexpected framework fault or a business result.
                if (IsExpectedContractRefreshStopExit(Volatile.Read(ref _client._stopStarted) != 0, exception)) return;
                throw;
            }
        }
        private async Task RefreshCoreAsync()
        {
            while (true)
            {
                lock (_gate) { _dirty = false; _refreshPhase = "CaptureConnections"; _capturedConnections = 0; _qualifiedConnections = 0; }
                var targets = new List<BoundContractTarget>(); Exception? error = null;
                try
                {
                    EnsureCapturedLocalAvailable();
                    var connections = _client.CaptureBindingConnections();
                    lock (_gate) { _capturedConnections = connections.Length; _refreshPhase = "QualifyAndStartBinds"; }
                    if (connections.Length > 1024) throw ContractBindingWire.Error(ContractBindStatus.Capacity);
                    var pending = new List<(ClientConnection Connection, Task<BoundContractTarget> Bind)>(connections.Length);
                    foreach (var connection in connections)
                    {
                        if (!connection.CanAcceptCalls) continue;
                        try
                        {
                            if (_client.QualifyBindingConnection(Key, connection))
                            {
                                pending.Add((connection, connection.Bindings.BindAsync(Key)));
                                lock (_gate) _qualifiedConnections++;
                            }
                        }
                        catch (Exception exception) { error ??= exception; }
                    }
                    while (pending.Count != 0)
                    {
                        // Any confirmed Session is eligible immediately; a slow peer must not hide
                        // another peer's real Ack. Drain all currently-complete results in one batch
                        // and publish one coherent target/connection snapshot for the normal Picker.
                        lock (_gate) _refreshPhase = "AwaitBinds";
                        _ = await Task.WhenAny(pending.Select(static item => item.Bind)).ConfigureAwait(false);
                        for (var index = pending.Count - 1; index >= 0; index--)
                        {
                            var item = pending[index];
                            if (!item.Bind.IsCompleted) continue;
                            pending.RemoveAt(index);
                            try
                            {
                                var target = await item.Bind.ConfigureAwait(false);
                                if (item.Connection.CanAcceptCalls) targets.Add(target);
                            }
                            catch (Exception exception) { error ??= exception; }
                        }
                        try { PublishConfirmedTargets(targets, error); }
                        catch (Exception exception) { error ??= exception; }
                    }
                }
                catch (Exception exception) { error = exception; }
                try { EnsureCapturedLocalAvailable(); }
                catch (Exception exception) { targets.Clear(); error = exception; }
                var again = PublishConfirmedTargets(targets, error, finishing: true);
                if (!again) return;
            }
        }
        private bool PublishConfirmedTargets(List<BoundContractTarget> targets, Exception? error, bool finishing = false)
        {
            EnsureCapturedLocalAvailable();
            var eligible = targets.Where(target => target.Connection.CanAcceptCalls &&
                target.Connection.Bindings.IsConfirmed(Key, target.Token)).ToArray();
            var cluster = _client._cluster?.CreateContractSnapshot(eligible);
            var qualified = cluster is null ? eligible : cluster.Endpoints.SelectMany(static endpoint => endpoint.Targets).ToArray();
            var nextTargets = new ContractTargetSnapshot(qualified, cluster);
            var nextChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource changed; bool again;
            lock (_gate)
            {
                if (_targets.HasSameRouting(nextTargets)) nextTargets = _targets;
                _targets = _stopped ? ContractTargetSnapshot.Empty : nextTargets;
                _refreshPhase = finishing ? "Finished" : "PublishingBatch";
                _refreshError = error; changed = _changed; _changed = nextChanged;
                again = _dirty && !_stopped;
                if (finishing && !again) _running = false;
            }
            changed.TrySetResult();
            return again;
        }
        internal ContractTargetSnapshot CaptureTargetSnapshot()
        { EnsureCapturedLocalAvailable(); return Volatile.Read(ref _targets); }
        internal BoundContractTarget SelectFixedTarget(ContractTargetSnapshot snapshot)
        {
            EnsureCapturedLocalAvailable();
            var connection = EndpointSelectionKernel.SelectConnection(snapshot.Connections);
            if (connection is null) throw ContractBindingWire.Error(ContractBindStatus.NoBoundRoute);
            try
            {
                foreach (var target in snapshot.Targets)
                    if (ReferenceEquals(target.Connection, connection) && target.SessionGeneration == connection.BindingGeneration && connection.Bindings.IsConfirmed(Key, target.Token))
                    {
                        if (connection.ActiveCallCount != 0) _client.EnsureExpansion();
                        return target;
                    }
                throw ContractBindingWire.Error(ContractBindStatus.NoBoundRoute);
            }
            catch { connection.ReleaseCallAdmissionReservation(); throw; }
        }
        internal async ValueTask PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (timeout <= TimeSpan.Zero || timeout > SharpLinkTimer.MaximumDelay) throw new ArgumentOutOfRangeException(nameof(timeout));
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCapturedLocalAvailable();
            var deadline = RpcDeadline.Create(timeout, _client._runtimeContext.TimeProvider);
            if (IsReady()) { VerifyReadyCompletion(deadline, cancellationToken); return; }
            if (Interlocked.Increment(ref _client._prepareWaiters) > 64)
            { Interlocked.Decrement(ref _client._prepareWaiters); throw ContractBindingWire.Error(ContractBindStatus.Capacity); }
            try
            {
                using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _client._contractShutdownToken);
                ScheduleRefresh();
                while (true)
                {
                    EnsureCapturedLocalAvailable(); cancellationToken.ThrowIfCancellationRequested();
                    var remaining = deadline.GetRemaining(_client._runtimeContext.TimeProvider);
                    if (remaining <= TimeSpan.Zero) throw CreateDeadlineExceededException();
                    Task changed; Exception? error;
                    lock (_gate) { if (IsReady()) { VerifyReadyCompletion(deadline, cancellationToken); return; } changed = _changed.Task; error = _running ? null : _refreshError; }
                    if (error is not null && error is not SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed or SharpLinkErrorCode.Unavailable }) throw error;
                    try
                    {
                        if (!await SharpLinkTimer.WaitAsync(changed, deadline, _client._runtimeContext.TimeProvider, waitCancellation.Token).ConfigureAwait(false))
                            throw CreateDeadlineExceededException();
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    { cancellationToken.ThrowIfCancellationRequested(); throw; }
                    catch (OperationCanceledException) when (_client._shutdownCts.IsCancellationRequested)
                    { throw CreateConnectionClosedException("Client is stopping."); }
                }
            }
            finally { Interlocked.Decrement(ref _client._prepareWaiters); }
        }
        private void VerifyReadyCompletion(RpcDeadline deadline, CancellationToken cancellationToken)
        {
            if (deadline.IsExpired(_client._runtimeContext.TimeProvider)) throw CreateDeadlineExceededException();
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCapturedLocalAvailable();
        }
        internal ContractBindingSiteTrace CaptureTrace()
        {
            lock (_gate) return new(Key.LocalGeneration, Key.ContractId, _registrationIdentity.IsAvailable, Committed,
                _running, _dirty, _stopped, _refreshPhase, _capturedConnections, _qualifiedConnections, _refreshError?.GetType().FullName,
                (_refreshError as SharpLinkException)?.Code, (_refreshError as SharpLinkException)?.DetailCode,
                _targets.Targets.Select(static target => new ContractBindingTargetTrace(target.Connection.Session.Id, target.SessionGeneration, target.Token)).ToArray());
        }
        internal int PublishedTargetCount => Volatile.Read(ref _targets).Targets.Length;
        private bool IsReady()
        {
            foreach (var target in Volatile.Read(ref _targets).Targets)
                if (target.Connection.CanAcceptCalls && target.Connection.Bindings.IsConfirmed(Key, target.Token)) return true;
            return false;
        }
    }

    private sealed class ContractChannel(ContractSite site, SharpLinkMetadata? metadata) : IRpcContractChannel
    {
        internal ContractSite Site => site;
        public IRpcRuntimeContext RuntimeContext => site._client.RuntimeContext;
        public ValueTask PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => site.PrepareAsync(timeout, cancellationToken);
        public ValueTask<TResponse> InvokeUnaryAsync<TRequest,TResponse>(RpcMethodDescriptor method, in TRequest request,
            IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec, SharpLinkMetadata? callMetadata, CancellationToken cancellationToken = default)
            => site._client.InvokeUnaryAsync(site, method, request, requestCodec, responseCodec, callMetadata ?? metadata, cancellationToken);
        public ValueTask InvokeOneWayAsync<TRequest,TStreams>(RpcMethodDescriptor method, in TRequest request,
            IRpcCodec<TRequest> requestCodec, in TStreams streams, SharpLinkMetadata? callMetadata, CancellationToken cancellationToken = default)
            where TStreams : struct, IRpcClientStreamWriter
            => site._client.InvokeOneWayAsync(site, method, request, requestCodec, streams, callMetadata ?? metadata, cancellationToken);
        public ValueTask<TResponse> InvokeClientStreamingAsync<TRequest,TResponse,TStreams>(RpcMethodDescriptor method, in TRequest request,
            IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec, in TStreams streams, SharpLinkMetadata? callMetadata, CancellationToken cancellationToken = default)
            where TStreams : struct, IRpcClientStreamWriter => throw ContractBindingWire.Error(ContractBindStatus.UnsupportedShape);
        public IAsyncEnumerable<TResponse> InvokeServerStreamingAsync<TRequest,TResponse>(RpcMethodDescriptor method, in TRequest request,
            IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec, SharpLinkMetadata? callMetadata, CancellationToken cancellationToken = default)
            => throw ContractBindingWire.Error(ContractBindStatus.UnsupportedShape);
        public IAsyncEnumerable<TResponse> InvokeDuplexStreamingAsync<TRequest,TResponse,TStreams>(RpcMethodDescriptor method, in TRequest request,
            IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec, in TStreams streams, SharpLinkMetadata? callMetadata, CancellationToken cancellationToken = default)
            where TStreams : struct, IRpcClientStreamWriter => throw ContractBindingWire.Error(ContractBindStatus.UnsupportedShape);
    }
}
