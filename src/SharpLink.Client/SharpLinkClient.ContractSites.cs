namespace SharpLink.Client;

internal readonly record struct ContractSiteKey(long LocalGeneration, long ContractId, RpcHash128 Hash,
    RpcRegistrationRequirement Requirement, RpcRegistrationReceipt? Receipt);
internal sealed record BoundContractTarget(ClientConnection Connection, long SessionGeneration, ulong Token);
internal sealed class ContractTargetSnapshot(BoundContractTarget[] targets)
{
    internal static ContractTargetSnapshot Empty { get; } = new([]);
    internal BoundContractTarget[] Targets { get; } = targets;
    internal ClientConnection[] Connections { get; } = targets.Select(static target => target.Connection).ToArray();
}

internal sealed partial class SharpLinkClient
{
    private readonly Guid _localContractIssuer = Guid.NewGuid();
    private readonly ConditionalWeakTable<ClientProxyRegistration, LocalContractIdentity> _localContractIdentities = new();
    private readonly Lock _contractSiteGate = new();
    private readonly Dictionary<ContractSiteKey, WeakReference<ContractSite>> _contractSites = [];
    private readonly ContractBindingQuota _contractBindingQuota = new();
    private long _localContractGeneration, _bindingSessionGeneration;
    private int _prepareWaiters;
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
        int sites; lock (_contractSiteGate) sites = _contractSites.Count;
        return new(_contractBindingQuota.Bindings, _contractBindingQuota.Operations, 0, sites,
            Volatile.Read(ref _prepareWaiters), CaptureBindingConnections().Sum(static c => c.Bindings.ReceivedUnpublishedAcks));
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
        var identity = _localContractIdentities.GetValue(registration, _ =>
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
        ValidateContractOptions(options);
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
        var channel = AcquireContractChannel(local, options);
        try { CompleteContractConstruction(channel.Site, success: true); return channel; }
        catch { CompleteContractConstruction(channel.Site, success: false); throw; }
    }
    private ContractChannel AcquireContractChannel(RpcLocalContract local, RpcProxyOptions? options)
    {
        ArgumentNullException.ThrowIfNull(local); ValidateContractOptions(options); EnsureContractClientActive();
        if (local.Issuer != _localContractIssuer) throw LocalContractError(SharpLinkErrorDetails.ContractBinding.ForeignLocalContract);
        var registration = ResolveLocalContract(local);
        var receipt = options?.Receipt; var requirement = options?.Requirement ?? RpcRegistrationRequirement.Compatible;
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
                channel = new ContractChannel(site, options?.Metadata);
                _contractSites.Add(key, new(site));
            }
            else channel = new ContractChannel(site, options?.Metadata);
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
            if (registration.Descriptor.ContractId == local.ContractId && _localContractIdentities.TryGetValue(registration, out var identity) &&
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
    private void ValidateContractOptions(RpcProxyOptions? options)
    {
        if (options is null) return;
        if (!Enum.IsDefined(options.Requirement)) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.Receipt is not { } receipt)
        { if (options.Requirement != RpcRegistrationRequirement.Compatible) throw new ArgumentException("This requirement needs a receipt.", nameof(options)); }
        else if (receipt.ServerInstanceId == Guid.Empty || receipt.RegistryEpoch == Guid.Empty || receipt.Generation < 0 ||
            receipt.ContractId == 0 || receipt.RpcAssemblyHash.IsEmpty || receipt.RegistrationId <= 0)
            throw new ArgumentException("Registration receipt structure is invalid.", nameof(options));
        if (options.Metadata is { } metadata && ProtocolV2PayloadCodec.GetMetadataPayloadLength(metadata) > _protocolOptions.MaxMetadataBytes)
            throw new ArgumentOutOfRangeException(nameof(options), "Metadata exceeds the configured limit.");
    }
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
        private readonly FrozenDictionary<long, RpcMethodDescriptor> _localMethods;
        internal int ConstructionReservations;
        internal bool Committed;
        private readonly Lock _gate = new();
        private ContractTargetSnapshot _targets = ContractTargetSnapshot.Empty;
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _running, _dirty;
        private Exception? _refreshError;
        internal ContractSite(SharpLinkClient client, RpcLocalContract local, ContractSiteKey key, ClientProxyRegistration registration, FrozenDictionary<long, RpcMethodDescriptor> localMethods)
        { _client = client; _local = local; Key = key; Registration = registration; _localMethods = localMethods; }
        internal void Validate(RpcMethodDescriptor method, RpcMethodKind kind)
        {
            _client.EnsureContractClientActive(); _client.ResolveLocalContract(_local);
            if (method.ContractId != Key.ContractId || !_localMethods.TryGetValue(method.MethodId, out var facts) || facts != method)
                throw LocalContractError(SharpLinkErrorDetails.ContractBinding.InvalidLocalContract);
            if (method.Kind != kind || !ContractMethodFacts.IsStaticSlice(method))
                throw ContractBindingWire.Error(ContractBindStatus.UnsupportedShape);
        }
        internal void Stop()
        {
            lock (_gate) { _targets = ContractTargetSnapshot.Empty; _dirty = false; _changed.TrySetResult(); }
        }
        internal void ScheduleRefresh()
        {
            lock (_gate)
            {
                _dirty = true;
                if (_running || _client._shutdownCts.IsCancellationRequested) return;
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
                lock (_gate) { _refreshError = exception; _running = false; _dirty = false; changed = _changed; }
                changed.TrySetResult();
                throw;
            }
        }
        private async Task RefreshCoreAsync()
        {
            while (true)
            {
                lock (_gate) _dirty = false;
                var targets = new List<BoundContractTarget>(); Exception? error = null;
                try
                {
                    _client.EnsureContractClientActive(); _client.ResolveLocalContract(_local);
                    var connections = _client.CaptureBindingConnections();
                    if (connections.Length > 1024) throw ContractBindingWire.Error(ContractBindStatus.Capacity);
                    var pending = new List<(ClientConnection Connection, Task<BoundContractTarget> Bind)>(connections.Length);
                    foreach (var connection in connections)
                    {
                        if (!connection.CanAcceptCalls) continue;
                        try
                        {
                            if (_client.QualifyBindingConnection(Key, connection))
                                pending.Add((connection, connection.Bindings.BindAsync(Key)));
                        }
                        catch (Exception exception) { error ??= exception; }
                    }
                    foreach (var item in pending)
                    {
                        try
                        {
                            var target = await item.Bind.ConfigureAwait(false);
                            if (item.Connection.CanAcceptCalls) targets.Add(target);
                        }
                        catch (Exception exception) { error ??= exception; }
                    }
                }
                catch (Exception exception) { error = exception; }
                try { _client.EnsureContractClientActive(); _client.ResolveLocalContract(_local); }
                catch (Exception exception) { targets.Clear(); error = exception; }
                var nextTargets = new ContractTargetSnapshot([.. targets]);
                var nextChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource changed; bool again;
                lock (_gate)
                {
                    _targets = nextTargets; _refreshError = error; changed = _changed;
                    _changed = nextChanged;
                    again = _dirty;
                    if (!again) _running = false;
                }
                changed.TrySetResult();
                if (!again) return;
            }
        }
        internal BoundContractTarget SelectFixedTarget()
        {
            _client.EnsureContractClientActive(); _client.ResolveLocalContract(_local);
            var snapshot = Volatile.Read(ref _targets);
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
        internal BoundContractTarget FindTarget(ClientConnection connection)
        {
            _client.EnsureContractClientActive(); _client.ResolveLocalContract(_local);
            var targets = Volatile.Read(ref _targets).Targets;
            foreach (var target in targets)
                if (ReferenceEquals(target.Connection, connection) && target.SessionGeneration == connection.BindingGeneration &&
                    connection.Bindings.IsConfirmed(Key, target.Token) && _client.QualifyBindingConnection(Key, connection)) return target;
            throw ContractBindingWire.Error(ContractBindStatus.NoBoundRoute);
        }
        internal async ValueTask PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (timeout <= TimeSpan.Zero || timeout > SharpLinkTimer.MaximumDelay) throw new ArgumentOutOfRangeException(nameof(timeout));
            cancellationToken.ThrowIfCancellationRequested();
            _client.EnsureContractClientActive(); _client.ResolveLocalContract(_local);
            var deadline = RpcDeadline.Create(timeout, _client._runtimeContext.TimeProvider);
            if (IsReady()) { VerifyReadyCompletion(deadline, cancellationToken); return; }
            if (Interlocked.Increment(ref _client._prepareWaiters) > 64)
            { Interlocked.Decrement(ref _client._prepareWaiters); throw ContractBindingWire.Error(ContractBindStatus.Capacity); }
            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _client._shutdownCts.Token);
            try
            {
                ScheduleRefresh();
                while (true)
                {
                    _client.EnsureContractClientActive(); _client.ResolveLocalContract(_local); cancellationToken.ThrowIfCancellationRequested();
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
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _client._shutdownCts.IsCancellationRequested)
                    { throw CreateConnectionClosedException("Client is stopping."); }
                }
            }
            finally { Interlocked.Decrement(ref _client._prepareWaiters); }
        }
        private void VerifyReadyCompletion(RpcDeadline deadline, CancellationToken cancellationToken)
        {
            if (deadline.IsExpired(_client._runtimeContext.TimeProvider)) throw CreateDeadlineExceededException();
            cancellationToken.ThrowIfCancellationRequested();
            _client.EnsureContractClientActive(); _client.ResolveLocalContract(_local);
        }
        internal bool HasReadyTarget => IsReady();
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
