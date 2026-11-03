using System.Reflection;
using System.Runtime.ExceptionServices;

namespace SharpLink.Client;

internal sealed partial class SharpLinkMultiClusterClient : ISharpLinkMultiClusterClient, ISharpLinkMultiClusterLifecycleControl
{
    private readonly SharpLinkMultiClusterOptions _options;
    private readonly ColdMetadataDomain _metadataDomain;
    private IDisposable? _metadataOwner;
    private readonly ColdMetadataPreparation _metadataPreparation;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly FrameworkTaskSupervisor _frameworkTasks;
    private MultiClusterSnapshot _snapshot;
    private readonly List<DynamicAssemblyRegistration> _dynamicRegistrations = [];
    private readonly HashSet<DynamicAssemblyRegistration> _drainingRegistrations =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<DynamicAssemblyRegistration, CoordinatorAssemblyRetirement>
        _unregisterOperations = new(ReferenceEqualityComparer.Instance);
    private Task? _connectTask;
    private Task? _stopTask;
    private int _activeAssemblyReplacements;
    private bool _slotMutationInProgress;
    private int _transitionConnectionBudget;
    private int _state = (int)SharpLinkMultiClusterState.Created;

    internal SharpLinkMultiClusterClient(
        SharpLinkMultiClusterOptions options,
        FrozenDictionary<SharpLinkClusterKey, SharpLinkClusterSlot> clusters,
        FrozenDictionary<Type, SharpLinkClusterRouteRegistration> routes,
        IReadOnlyList<ISharpLinkGeneratedClusterRouteManifest> routeManifestSnapshot,
        int configuredConnectionBudget = 0,
        ILoggerFactory? loggerFactory = null,
        ColdMetadataDomain? metadataDomain = null,
        IDisposable? metadataOwner = null,
        ColdMetadataPreparation? metadataPreparation = null)
    {
        ArgumentNullException.ThrowIfNull(routeManifestSnapshot);
        _options = options;
        _metadataDomain = metadataDomain ?? new ColdMetadataAuthority().CreateDomain();
        _metadataPreparation = metadataPreparation ?? _metadataDomain.BeginPreparation();
        _metadataOwner = metadataOwner ?? _metadataPreparation;
        if (metadataPreparation is null) _metadataPreparation.Complete();
        try
        {
        _metadataPreparation.ReserveCollection(checked(32 + 3 * options.MaxClusters));
        _assemblyStopEntries = new AssemblyStopEntry[options.MaxClusters];
        _assemblyStopFailures = new Exception?[8];
        _assemblyOperationMapCapacity = _metadataPreparation.ReserveCollection(0);
        _assemblyDrainingMapCapacity = _metadataPreparation.ReserveCollection(0);
        _assemblyRegistrationListCapacity = _metadataPreparation.ReserveCollection(0);
        _assemblyStopError = new AssemblyStopFailure(this);
        InitializeClusterDialLimiter(options, clusters.Values);
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<SharpLinkMultiClusterClient>();
        _timeProvider = clusters.Values
            .Select(static slot => slot.Client)
            .OfType<ISharpLinkClientTimeProvider>()
            .FirstOrDefault()?.TimeProvider ?? TimeProvider.System;
        _frameworkTasks = new FrameworkTaskSupervisor((operation, exception) =>
            LogMultiClusterFrameworkTaskFailure(_logger, operation, exception));
        _snapshot = MultiClusterSnapshot.Create(
            clusters,
            routes,
            configuredConnectionBudget > 0
                ? configuredConnectionBudget
                : clusters.Values.Sum(static slot => slot.ConfiguredConnectionBudget), _metadataPreparation);
        }
        catch
        {
            if (metadataPreparation is null) Interlocked.Exchange(ref _metadataOwner, null)?.Dispose();
            throw;
        }
    }

    private MultiClusterSnapshot AcquireSnapshot()
    {
        lock (_gate) return _snapshot.Retain();
    }
    private void PublishSnapshotLocked(MultiClusterSnapshot snapshot)
    {
        if (_assemblyWindow is not null || _assemblyPublicationFailed)
            throw new InvalidOperationException("A foreign assembly publication window owns the snapshot.");
        var old = _snapshot;
        Volatile.Write(ref _snapshot, snapshot);
        old.Dispose();
    }
    private ColdMetadataPreparation CreateMetadataStorage() => _metadataPreparation.ForkStorage();

    public SharpLinkMultiClusterState State
    {
        get
        {
            var state = (SharpLinkMultiClusterState)Volatile.Read(ref _state);
            if (state is not SharpLinkMultiClusterState.Ready and not SharpLinkMultiClusterState.Degraded)
                return state;

            using var snapshotLease = AcquireSnapshot();
            var slots = snapshotLease.Slots;
            if (slots.Length == 0)
                return state;
            var ready = 0;
            for (var index = 0; index < slots.Length; index++)
            {
                if (slots[index].Client.State == SharpLinkConnectionState.Ready)
                    ready++;
            }
            return ready == slots.Length ? SharpLinkMultiClusterState.Ready : SharpLinkMultiClusterState.Degraded;
        }
    }

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        Task operation;
        lock (_gate)
        {
            var state = (SharpLinkMultiClusterState)_state;
            if (state == SharpLinkMultiClusterState.Ready)
                return ValueTask.CompletedTask;
            if (state is SharpLinkMultiClusterState.Draining or SharpLinkMultiClusterState.Stopped or SharpLinkMultiClusterState.Faulted)
                return ValueTask.FromException(new InvalidOperationException($"Multi-cluster client state '{state}' cannot connect."));
            if (_slotMutationInProgress)
                return ValueTask.FromException(new InvalidOperationException("A cluster slot lifecycle mutation is in progress."));

            if (_connectTask is null)
            {
                _connectTask = ConnectCoreAsync();
                TrackFrameworkTask(
                    _connectTask,
                    "MultiClusterInitialConnect",
                    TaskObservationMode.ExternallyObserved);
            }
            operation = _connectTask;
        }

        return cancellationToken.CanBeCanceled
            ? new ValueTask(operation.WaitAsync(cancellationToken))
            : new ValueTask(operation);
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        ColdMetadataPreparation.ThrowIfCallbackReentry(_metadataDomain);
        Task operation;
        lock (_gate)
        {
            _stopTask ??= StopCoreAsync();
            operation = _stopTask;
        }
        return cancellationToken.CanBeCanceled
            ? new ValueTask(operation.WaitAsync(cancellationToken))
            : new ValueTask(operation);
    }

    public ValueTask DisposeAsync() => StopAsync();

    public SharpLinkConnectionState GetClusterState(SharpLinkClusterKey cluster)
        => GetSlot(cluster).Client.State;

    public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(
        SharpLinkClusterKey cluster,
        CancellationToken cancellationToken = default)
        => GetSlot(cluster).Client.CheckHealthAsync(cancellationToken);

    public SharpLinkAssemblyRegistrationResult RegisterAssembly(SharpLinkClusterKey cluster, Assembly assembly)
    {
        using var preparation = _metadataDomain.BeginPreparation();
        if (assembly is null)
            return SharpLinkAssemblyManifestLoader.TryLoad(null, preparation, out _);

        SharpLinkClusterSlot slot;
        lock (_gate)
        {
            var state = (SharpLinkMultiClusterState)_state;
            if (_assemblyPublicationFailed || state is SharpLinkMultiClusterState.Draining or SharpLinkMultiClusterState.Stopped or SharpLinkMultiClusterState.Faulted)
            {
                return Failure(SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                    $"Multi-cluster client state '{state}' does not accept runtime assembly registration.", assembly);
            }

            slot = GetSlot(cluster);
        }
        if (!slot.AllowDynamicContracts)
            return Failure(SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                $"Cluster '{cluster}' does not allow dynamic contract registration.", assembly);

        var loaded = SharpLinkAssemblyManifestLoader.TryLoad(assembly, preparation, out var manifest);
        if (!loaded.Succeeded)
            return loaded;

        if (slot.Client is not ISharpLinkPreparedAssemblyClient preparedChild)
            return Failure(SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                "The internal child does not implement the prepared metadata transaction.", assembly);
        return preparedChild.RegisterPreparedAssembly(manifest!, CommitChild);

        SharpLinkAssemblyRegistrationResult CommitChild(Func<SharpLinkAssemblyRegistrationResult> commit)
        {
        lock (_gate)
        {
            var state = (SharpLinkMultiClusterState)_state;
            if (_assemblyPublicationFailed || state is SharpLinkMultiClusterState.Draining or SharpLinkMultiClusterState.Stopped or SharpLinkMultiClusterState.Faulted)
            {
                return Failure(SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                    $"Multi-cluster client state '{state}' does not accept runtime assembly registration.", assembly);
            }
            if (_dynamicRegistrations.Any(registration => ReferenceEquals(registration.Assembly, assembly) &&
                ReferenceEquals(registration.Slot, slot)))
            {
                return Failure(SharpLinkAssemblyRegistrationErrorCode.DuplicateAssembly,
                    $"Assembly '{assembly.FullName}' is already registered in cluster '{cluster}'.", assembly);
            }
            if (manifest!.Contracts.Count > 0 && _dynamicRegistrations.Any(registration =>
                ReferenceEquals(registration.Assembly, assembly) && registration.Manifest.Contracts.Count > 0))
            {
                return Failure(SharpLinkAssemblyRegistrationErrorCode.ContractConflict,
                    $"Contract-owning assembly '{assembly.FullName}' is already routed to another cluster.", assembly);
            }

            if (_slotMutationInProgress || _assemblyWindow is not null || _assemblyPublicationFailed || _assemblySealed)
            {
                return Failure(SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                    "A cluster slot lifecycle mutation is in progress.", assembly);
            }

            var snapshot = Volatile.Read(ref _snapshot);
            if (!snapshot.Clusters.TryGetValue(cluster, out var currentSlot) ||
                !ReferenceEquals(currentSlot, slot))
            {
                return Failure(SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                    $"Cluster '{cluster}' changed while its assembly manifest was loaded.", assembly);
            }
            var currentRoutes = snapshot.Routes;
            foreach (var contract in manifest.Contracts)
            {
                if (currentRoutes.ContainsKey(contract.ContractType) ||
                    currentRoutes.Values.Any(route => route.ContractId == contract.ContractId) ||
                    _dynamicRegistrations.Any(registration => registration.Manifest.Contracts.Any(
                        existingContract => existingContract.ContractId == contract.ContractId)))
                {
                    return Failure(SharpLinkAssemblyRegistrationErrorCode.ContractConflict,
                        $"Contract '{contract.ContractName}' ({contract.ContractId}) is already routed to another assembly or cluster.", assembly);
                }
            }

            MultiClusterSnapshot? candidateSnapshot = null;
            DynamicAssemblyRegistration? nextRegistration = null;
            var routePublished = false;
            Exception? candidateFailure = null;
            try
            {
                preparation.ReserveCollection(checked(currentRoutes.Count + manifest.Contracts.Count));
                preparation.ReserveCollection(checked(currentRoutes.Count + manifest.Contracts.Count));
                var nextRoutes = new Dictionary<Type, SharpLinkClusterRouteRegistration>(
                    checked(currentRoutes.Count + manifest.Contracts.Count));
                foreach (var pair in currentRoutes) nextRoutes.Add(pair.Key, pair.Value);
                foreach (var contract in manifest.Contracts)
                {
                    nextRoutes.Add(contract.ContractType, new SharpLinkClusterRouteRegistration(
                        contract.ContractType, contract.ContractId, contract.Fingerprint, slot, assembly));
                }
                var frozenRoutes = nextRoutes.ToFrozenDictionary();
                nextRegistration = new DynamicAssemblyRegistration(slot, assembly, manifest, this);
                nextRegistration.Capacity = CreateAssemblyCapacityLocked(preparation);
                var registrationCapacity = checked(_dynamicRegistrations.Count + 1);
                _assemblyRegistrationListCapacity.GrowTo(registrationCapacity);
                if (_dynamicRegistrations.Capacity < registrationCapacity)
                    _dynamicRegistrations.Capacity = registrationCapacity;
                candidateSnapshot = MultiClusterSnapshot.Create(snapshot.Clusters, frozenRoutes,
                    snapshot.ConfiguredConnectionBudget, preparation, snapshot, manifest);
                var childResult = commit();
                if (!childResult.Succeeded) return childResult;
                // Both collections were prebuilt under this gate. Mark the
                // actual publication before any fallible old-snapshot cleanup.
                var oldSnapshot = _snapshot;
                Volatile.Write(ref _snapshot, candidateSnapshot);
                _dynamicRegistrations.Add(nextRegistration);
                routePublished = true;
                try { oldSnapshot.Dispose(); }
                catch (Exception failure)
                {
                    nextRegistration.RecordSnapshotFailure(oldSnapshot, failure);
                    throw;
                }
                return SharpLinkAssemblyRegistrationResult.Success();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                candidateFailure = exception;
                return Failure(SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                    $"Cluster route candidate publication failed: {exception.GetType().Name}: {exception.Message}", assembly);
            }
            finally
            {
                if (!routePublished)
                {
                    Exception? cleanup = null;
                    try { candidateSnapshot?.Dispose(); }
                    catch (Exception failure)
                    {
                        // The snapshot is created only after its registration.
                        // Keep its original owner and prohibit capacity refund.
                        nextRegistration!.RecordSnapshotFailure(candidateSnapshot!, failure);
                        cleanup = failure;
                    }
                    try { nextRegistration?.Dispose(); }
                    catch (Exception failure)
                    { cleanup = cleanup is null ? failure : new AggregateException(cleanup, failure); }
                    if (cleanup is not null)
                    {
                        if (candidateFailure is not null) throw new AggregateException(candidateFailure, cleanup);
                        ExceptionDispatchInfo.Capture(cleanup).Throw();
                    }
                }
            }
        }
        }
    }

    public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
        SharpLinkClusterKey cluster,
        Assembly assembly,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentOutOfRangeException.ThrowIfLessThan(gracefulTimeout, TimeSpan.Zero);
        SharpLinkClusterSlot slot;
        DynamicAssemblyRegistration? registration;
        ISharpLinkPreparedAssemblyClient preparedChild;
        lock (_gate)
        {
            var state = (SharpLinkMultiClusterState)_state;
            if (_assemblyPublicationFailed || state is SharpLinkMultiClusterState.Draining or SharpLinkMultiClusterState.Stopped or SharpLinkMultiClusterState.Faulted)
                return ValueTask.FromResult(new SharpLinkAssemblyUnregisterResult { ReferencesReleased = false });

            slot = GetSlot(cluster);
            registration = _dynamicRegistrations.FirstOrDefault(candidate =>
                ReferenceEquals(candidate.Slot, slot) && ReferenceEquals(candidate.Assembly, assembly));
            if (registration is null)
                return ValueTask.FromResult(new SharpLinkAssemblyUnregisterResult { ReferencesReleased = false });
            if (_unregisterOperations.TryGetValue(registration, out var activeOperation))
                return WaitAssemblyUnregisterAsync(activeOperation, cancellationToken);

            if (_slotMutationInProgress)
                return ValueTask.FromResult(new SharpLinkAssemblyUnregisterResult { ReferencesReleased = false });

            if (slot.Client is not ISharpLinkPreparedAssemblyClient capability)
                throw new InvalidOperationException("The internal child does not support exact retirement budgets.");
            preparedChild = capability;
        }

        // Identity-only locals cross this synchronous provider call. No manifest,
        // snapshot lease or preparation is accessed until the second validation.
        var budget = preparedChild.CaptureRetirementBudget(gracefulTimeout);
        lock (_gate)
        {
            if (!CapturedAssemblyRegistrationIsCurrentLocked(cluster, slot, registration))
                return ValueTask.FromResult(new SharpLinkAssemblyUnregisterResult { ReferencesReleased = false });
            if (_unregisterOperations.TryGetValue(registration, out var activeOperation))
                return WaitAssemblyUnregisterAsync(activeOperation, cancellationToken);
            var owner = StartAssemblyOwnerLocked(registration, budget, null);
            return WaitAssemblyUnregisterAsync(owner, cancellationToken);
        }
    }

    public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
        SharpLinkClusterKey cluster,
        Assembly oldAssembly,
        Assembly newAssembly,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oldAssembly);
        ArgumentNullException.ThrowIfNull(newAssembly);
        ArgumentOutOfRangeException.ThrowIfLessThan(gracefulTimeout, TimeSpan.Zero);
        SharpLinkClusterSlot slot;
        DynamicAssemblyRegistration? registration;
        ISharpLinkPreparedAssemblyClient preparedChild;
        lock (_gate)
        {
            var state = (SharpLinkMultiClusterState)_state;
            if (_assemblyPublicationFailed || state is SharpLinkMultiClusterState.Draining or SharpLinkMultiClusterState.Stopped or SharpLinkMultiClusterState.Faulted)
            {
                return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(Error(
                    SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                    $"Multi-cluster client state '{state}' does not accept runtime assembly replacement.", newAssembly)));
            }

            if (_slotMutationInProgress)
            {
                return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(Error(
                    SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                    "A cluster slot lifecycle mutation is in progress.", newAssembly)));
            }

            slot = GetSlot(cluster);
            registration = _dynamicRegistrations.FirstOrDefault(candidate =>
                ReferenceEquals(candidate.Slot, slot) && ReferenceEquals(candidate.Assembly, oldAssembly));
            if (registration is null)
                return RejectChangedAssemblyReplacement(newAssembly);
            if (slot.Client is not ISharpLinkPreparedAssemblyClient capability)
                return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(Error(
                    SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                    "The internal child does not support exact retirement budgets.", newAssembly)));
            preparedChild = capability;
        }

        var budget = preparedChild.CaptureRetirementBudget(gracefulTimeout);
        lock (_gate)
        {
            if (!CapturedAssemblyRegistrationIsCurrentLocked(cluster, slot, registration))
                return RejectChangedAssemblyReplacement(newAssembly);
        }
        using var preparation = _metadataDomain.BeginPreparation();
        var loaded = SharpLinkAssemblyManifestLoader.TryLoad(newAssembly, preparation, out var newManifest);
        if (!loaded.Succeeded)
            return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(loaded.Error!));

        lock (_gate)
        {
            var state = (SharpLinkMultiClusterState)_state;
            if (_assemblyPublicationFailed || state is SharpLinkMultiClusterState.Draining or SharpLinkMultiClusterState.Stopped or SharpLinkMultiClusterState.Faulted)
            {
                return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(Error(
                    SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                    $"Multi-cluster client state '{state}' does not accept runtime assembly replacement.", newAssembly)));
            }

            // Loader callbacks may race disposal/publication. Revalidate exact
            // identities before reading registration.Manifest, never by Assembly alone.
            if (!CapturedAssemblyRegistrationIsCurrentLocked(cluster, slot, registration))
                return RejectChangedAssemblyReplacement(newAssembly);

            var oldIds = registration.Manifest.Contracts.Select(static contract => contract.ContractId).Order().ToArray();
            var newIds = newManifest!.Contracts.Select(static contract => contract.ContractId).Order().ToArray();
            if (!oldIds.SequenceEqual(newIds))
            {
                return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(Error(
                    SharpLinkAssemblyRegistrationErrorCode.ContractConflict,
                    "Replacement assemblies must preserve the exact ContractId set within the same cluster.", newAssembly)));
            }
            if (_slotMutationInProgress)
            {
                return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(Error(
                    SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                    "A cluster slot lifecycle mutation is in progress.", newAssembly)));
            }
            var owner = StartAssemblyOwnerLocked(registration, budget, newManifest);
            return WaitAssemblyReplacementAsync(owner, cancellationToken);
        }
    }

    // No metadata getter, retained lease or callback occurs in this identity check.
    // The caller holds _gate; immutable identity locals need no metadata lease.
    private bool CapturedAssemblyRegistrationIsCurrentLocked(
        SharpLinkClusterKey cluster, SharpLinkClusterSlot slot, DynamicAssemblyRegistration registration)
    {
        var state = (SharpLinkMultiClusterState)_state;
        if (state is SharpLinkMultiClusterState.Draining or SharpLinkMultiClusterState.Stopped or SharpLinkMultiClusterState.Faulted ||
            _slotMutationInProgress || _assemblyWindow is not null || _assemblyPublicationFailed || _assemblySealed ||
            !_snapshot.Clusters.TryGetValue(cluster, out var currentSlot) ||
            !ReferenceEquals(currentSlot, slot))
            return false;
        foreach (var current in _dynamicRegistrations)
            if (ReferenceEquals(current, registration)) return true;
        return false;
    }

    private static ValueTask<SharpLinkAssemblyReplacementResult> RejectChangedAssemblyReplacement(Assembly newAssembly)
        => ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(Error(
            SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
            "The captured assembly registration or cluster changed before replacement publication.", newAssembly)));

    private async Task ConnectCoreAsync()
    {
        Volatile.Write(ref _state, (int)SharpLinkMultiClusterState.Connecting);
        using var snapshotLease = AcquireSnapshot();
        var capturedSlots = snapshotLease.Slots;
        using var attempts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        try
        {
            await Parallel.ForEachAsync(
                capturedSlots,
                new ParallelOptions { CancellationToken = attempts.Token, MaxDegreeOfParallelism = _options.MaxConcurrentClusterConnects },
                static async (slot, token) =>
                {
                    // The child owns its physical connect attempt. The coordinator owns only
                    // the cancellable wait, so an uncooperative child cannot hold coordinator
                    // shutdown or its supervised initial-connect operation indefinitely.
                    await slot.Client.ConnectAsync(token).AsTask().WaitAsync(token).ConfigureAwait(false);
                }).ConfigureAwait(false);
            _ = Interlocked.CompareExchange(
                ref _state,
                (int)SharpLinkMultiClusterState.Ready,
                (int)SharpLinkMultiClusterState.Connecting);
        }
        catch (Exception connectException)
        {
            attempts.Cancel();
            await RethrowInitialConnectFailureAsync(connectException, capturedSlots).ConfigureAwait(false);
        }
    }

    private Task StopCoreAsync() => StopAssemblyCoordinatorAsync();

    private static async Task StopSlotsAsync(IEnumerable<SharpLinkClusterSlot> slots, List<Exception> failures)
    {
        foreach (var slot in slots)
        {
            try { await slot.Client.StopAsync().ConfigureAwait(false); }
            catch (Exception exception) { failures.Add(exception); }
            finally { slot.ReleaseMetadata(); }
        }
    }

    private static ValueTask<T> WaitForOperationAsync<T>(Task<T> operation, CancellationToken cancellationToken)
        => cancellationToken.CanBeCanceled
            ? new ValueTask<T>(operation.WaitAsync(cancellationToken))
            : new ValueTask<T>(operation);

    private void TrackFrameworkTask(
        Task task,
        string operation,
        TaskObservationMode observationMode = TaskObservationMode.FrameworkOwned)
        => _frameworkTasks.Track(task, operation, observationMode, IsExpectedStopException);

    private static bool IsExpectedStopException(Exception exception)
        => exception is OperationCanceledException or ObjectDisposedException or IOException or SocketException or
            SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed or SharpLinkErrorCode.Unavailable };

    internal CancellationToken ShutdownTokenForDiagnostics => _shutdown.Token;

    internal FrameworkTaskSupervisorSnapshot FrameworkTaskSnapshotForDiagnostics
        => _frameworkTasks.CaptureSnapshot();

    private SharpLinkClusterSlot GetSlot(SharpLinkClusterKey cluster)
    {
        if (!SharpLinkClusterKey.IsValid(cluster.Value))
            throw new ArgumentException("A valid non-default SharpLinkClusterKey is required.", nameof(cluster));
        using var snapshotLease = AcquireSnapshot();
        if (snapshotLease.Clusters.TryGetValue(cluster, out var slot))
            return slot;
        throw new ArgumentException($"Cluster '{cluster}' is not configured.", nameof(cluster));
    }

    private static SharpLinkAssemblyRegistrationResult Failure(
        SharpLinkAssemblyRegistrationErrorCode code,
        string message,
        Assembly assembly)
        => SharpLinkAssemblyRegistrationResult.Failure(Error(code, message, assembly));

    private static SharpLinkAssemblyRegistrationError Error(
        SharpLinkAssemblyRegistrationErrorCode code,
        string message,
        Assembly assembly)
        => new(code, message, IncomingAssembly: assembly.FullName);

    private TimeProvider GetTimeProvider(ISharpLinkClient client)
        => client is ISharpLinkClientTimeProvider inspector
            ? inspector.TimeProvider
            : _timeProvider;
}

internal sealed record SharpLinkClusterSlot(
    SharpLinkClusterKey Key,
    ISharpLinkClient Client,
    bool AllowDynamicContracts,
    int ConfiguredConnectionBudget = 1,
    IReadOnlyList<ISharpLinkGeneratedAssemblyManifest>? StaticManifests = null)
{
    public IReadOnlyList<ISharpLinkGeneratedAssemblyManifest>? StaticManifests { get; set; } = StaticManifests;
    internal IDisposable? MetadataOwner;
    internal void ReleaseMetadata()
    {
        StaticManifests = null;
        Interlocked.Exchange(ref MetadataOwner, null)?.Dispose();
    }
}

internal sealed record SharpLinkClusterRouteRegistration(
    Type ContractType,
    long ContractId,
    string Fingerprint,
    SharpLinkClusterSlot Slot,
    Assembly OwnerAssembly);

internal sealed record DynamicAssemblyRegistration : IDisposable
{
    private ISharpLinkGeneratedAssemblyManifest? _manifest;
    private IDisposable? _metadata;
    private IDisposable? _failedMetadata;
    private IDisposable? _failedCloneMetadata;
    private Exception? _cloneFailure;
    private SharpLinkMultiClusterClient? _custodyOwner;
    internal DynamicAssemblyRegistration? FailedNext;
    internal bool CustodyRetained;
    internal bool DisposalReportedByOuter;
    internal MultiClusterSnapshot? FailedSnapshot;
    internal Exception? SnapshotFailure;
    internal Exception? DisposalFailure => _disposeFailure ?? _cloneFailure;
    private bool _disposeAttempted;
    private Exception? _disposeFailure;
    internal CoordinatorAssemblyCapacity? Capacity;
    internal Exception? StopFailure;
    internal bool HasUncertifiedClone => _cloneFailure is not null || _failedCloneMetadata is not null;
    internal DynamicAssemblyRegistration(SharpLinkClusterSlot slot, Assembly assembly,
        ISharpLinkGeneratedAssemblyManifest manifest, SharpLinkMultiClusterClient? custodyOwner = null)
    {
        _custodyOwner = custodyOwner;
        Slot = slot; Assembly = assembly; _manifest = manifest;
        _metadata = (manifest as PreparedGeneratedManifest)?.Preparation.Retain() ??
            throw new InvalidOperationException("Coordinator registrations require prepared metadata ownership.");
    }
    private DynamicAssemblyRegistration(DynamicAssemblyRegistration original)
    {
        if (original._cloneFailure is { } prior) ExceptionDispatchInfo.Capture(prior).Throw();
        _custodyOwner = original._custodyOwner;
        Slot = original.Slot; Assembly = original.Assembly; _manifest = original.Manifest;
        _metadata = ((PreparedGeneratedManifest)_manifest).Preparation.Retain();
        try { Capacity = original.Capacity?.RetainForClone(); }
        catch (Exception primary)
        {
            var acquired = _metadata;
            _metadata = null;
            try { acquired?.Dispose(); }
            catch (Exception cleanup)
            {
                // The original registration remains the durable root when copy
                // construction fails. Never lose this actually acquired lease or
                // overwrite a prior failed clone with another attempt.
                original._failedCloneMetadata = acquired;
                original._cloneFailure = new AggregateException(primary, cleanup);
                original._custodyOwner?.RetainFailedAssemblyRegistration(original);
                throw original._cloneFailure;
            }
            throw;
        }
    }
    internal SharpLinkClusterSlot Slot { get; init; }
    internal Assembly Assembly { get; }
    internal ISharpLinkGeneratedAssemblyManifest Manifest => _manifest ??
        throw new ObjectDisposedException(nameof(DynamicAssemblyRegistration));
    internal void AttachCustodyOwner(SharpLinkMultiClusterClient owner)
    {
        if (_custodyOwner is not null && !ReferenceEquals(_custodyOwner, owner))
            throw new InvalidOperationException("Registration custody belongs to another coordinator.");
        _custodyOwner = owner;
    }
    internal void RecordSnapshotFailure(MultiClusterSnapshot snapshot, Exception failure)
    {
        // One original candidate/retired snapshot cleanup per registration.
        FailedSnapshot = snapshot;
        SnapshotFailure = failure;
        _custodyOwner?.RetainFailedAssemblyRegistration(this);
    }
    public void Dispose()
    {
        if (_disposeAttempted)
        {
            if (_disposeFailure is { } prior) ExceptionDispatchInfo.Capture(prior).Throw();
            return;
        }
        _disposeAttempted = true;
        Exception? failure = _cloneFailure;
        _failedMetadata = _metadata;
        _metadata = null;
        try { _failedMetadata?.Dispose(); _failedMetadata = null; }
        catch (Exception exception) { failure = failure is null ? exception : new AggregateException(failure, exception); }
        // The fixed96 custody cannot refund while any of its actual metadata or
        // snapshot cleanup remains uncertified. Successful clone decref is not a
        // final refund; its sibling still owns the exact shared unit.
        if (failure is null && SnapshotFailure is null)
        {
            try { Capacity?.Dispose(); }
            catch (Exception exception) { failure = exception; }
        }
        _disposeFailure = failure;
        if (failure is not null || SnapshotFailure is not null)
            _custodyOwner?.RetainFailedAssemblyRegistration(this);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        if (SnapshotFailure is not null) return;
        _manifest = null;
    }

}

internal interface IDynamicAssemblyRegistrationInspector
{
    bool IsDynamicAssemblyRegistered(Assembly assembly);
}

internal interface ISharpLinkClientDrainInspector
{
    int ActiveCallCount { get; }

    int ActiveStreamCount { get; }
}

internal interface ISharpLinkClientTimeProvider
{
    TimeProvider TimeProvider { get; }
}
