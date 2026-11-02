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
    private readonly Dictionary<DynamicAssemblyRegistration, Task<SharpLinkAssemblyUnregisterResult>>
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
            if (state is SharpLinkMultiClusterState.Draining or SharpLinkMultiClusterState.Stopped or SharpLinkMultiClusterState.Faulted)
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
            if (state is SharpLinkMultiClusterState.Draining or SharpLinkMultiClusterState.Stopped or SharpLinkMultiClusterState.Faulted)
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

            if (_slotMutationInProgress)
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
                nextRegistration = new DynamicAssemblyRegistration(slot, assembly, manifest);
                var registrationCapacity = checked(_dynamicRegistrations.Count + 1);
                preparation.ReserveCollection(registrationCapacity);
                if (_dynamicRegistrations.Capacity < registrationCapacity)
                    _dynamicRegistrations.Capacity = registrationCapacity;
                candidateSnapshot = MultiClusterSnapshot.Create(snapshot.Clusters, frozenRoutes,
                    snapshot.ConfiguredConnectionBudget, preparation, snapshot, manifest);
                var childResult = commit();
                if (!childResult.Succeeded) return childResult;
                PublishSnapshotLocked(candidateSnapshot);
                _dynamicRegistrations.Add(nextRegistration);
                routePublished = true;
                return SharpLinkAssemblyRegistrationResult.Success();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                return Failure(SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                    $"Cluster route candidate publication failed: {exception.GetType().Name}: {exception.Message}", assembly);
            }
            finally
            {
                if (!routePublished) { candidateSnapshot?.Dispose(); nextRegistration?.Dispose(); }
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
            if (state is SharpLinkMultiClusterState.Draining or SharpLinkMultiClusterState.Stopped or SharpLinkMultiClusterState.Faulted)
                return ValueTask.FromResult(new SharpLinkAssemblyUnregisterResult { ReferencesReleased = false });

            slot = GetSlot(cluster);
            registration = _dynamicRegistrations.FirstOrDefault(candidate =>
                ReferenceEquals(candidate.Slot, slot) && ReferenceEquals(candidate.Assembly, assembly));
            if (registration is null)
                return ValueTask.FromResult(new SharpLinkAssemblyUnregisterResult { ReferencesReleased = false });
            if (_unregisterOperations.TryGetValue(registration, out var activeOperation))
                return WaitForOperationAsync(activeOperation, cancellationToken);

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
                return WaitForOperationAsync(activeOperation, cancellationToken);
            using var metadata = CreateMetadataStorage();
            var snapshot = Volatile.Read(ref _snapshot);
            metadata.ReserveCollection(snapshot.Routes.Count);
            metadata.ReserveCollection(snapshot.Routes.Count);
            var nextRoutes = snapshot.Routes
                .Where(pair => !ReferenceEquals(pair.Value.OwnerAssembly, assembly))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value)
                .ToFrozenDictionary();
            PublishSnapshotLocked(MultiClusterSnapshot.Create(snapshot.Clusters, nextRoutes,
                snapshot.ConfiguredConnectionBudget, metadata, snapshot));

            var completion = new TaskCompletionSource<SharpLinkAssemblyUnregisterResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = completion.Task;
            _unregisterOperations.Add(registration, operation);
            _drainingRegistrations.Add(registration);
            _ = CompleteSharedUnregisterAsync(
                slot,
                registration,
                budget,
                completion);
            TrackFrameworkTask(
                operation,
                "MultiClusterAssemblyUnregister",
                TaskObservationMode.ExternallyObserved);
            return WaitForOperationAsync(operation, cancellationToken);
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
            if (state is SharpLinkMultiClusterState.Draining or SharpLinkMultiClusterState.Stopped or SharpLinkMultiClusterState.Faulted)
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
            if (state is SharpLinkMultiClusterState.Draining or SharpLinkMultiClusterState.Stopped or SharpLinkMultiClusterState.Faulted)
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
            _activeAssemblyReplacements++;
        }

        var operation = CompleteTrackedReplacementAsync(
            slot, registration!, newAssembly, newManifest!, budget, preparation.Retain());
        TrackFrameworkTask(
            operation,
            "MultiClusterAssemblyReplacement",
            TaskObservationMode.ExternallyObserved);
        return WaitForOperationAsync(operation, cancellationToken);
    }

    // No metadata getter, retained lease or callback occurs in this identity check.
    // The caller holds _gate; immutable identity locals need no metadata lease.
    private bool CapturedAssemblyRegistrationIsCurrentLocked(
        SharpLinkClusterKey cluster, SharpLinkClusterSlot slot, DynamicAssemblyRegistration registration)
    {
        var state = (SharpLinkMultiClusterState)_state;
        if (state is SharpLinkMultiClusterState.Draining or SharpLinkMultiClusterState.Stopped or SharpLinkMultiClusterState.Faulted ||
            _slotMutationInProgress || !_snapshot.Clusters.TryGetValue(cluster, out var currentSlot) ||
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

    private async Task StopCoreAsync()
    {
        Volatile.Write(ref _state, (int)SharpLinkMultiClusterState.Draining);
        _frameworkTasks.Seal();
        var failures = new List<Exception>();
        // Seal under the already-held publication gate; joining below never waits under it.
        var metadataJoined = _metadataDomain.SealAndJoinAsync();
        await Task.Yield();
        // Signal outside the publication gate before joining admitted work: a metadata
        // callback may itself be waiting for shutdown cancellation to leave preparation.
        // Cancellation callback failures do not skip that join or child/provider cleanup.
        try { await _shutdown.CancelAsync().ConfigureAwait(false); }
        catch (Exception exception) { failures.Add(exception); }
        try { await metadataJoined.ConfigureAwait(false); }
        catch (Exception exception) { failures.Add(exception); }
        await _mutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var snapshotLease = AcquireSnapshot();
            var slots = snapshotLease.Slots;
            await StopSlotsAsync(slots, failures).ConfigureAwait(false);
            try { await _frameworkTasks.DrainAsync().ConfigureAwait(false); }
            catch (Exception exception) { failures.Add(exception); }
            lock (_gate)
            {
                PublishSnapshotLocked(MultiClusterSnapshot.Empty);
                foreach (var registration in _dynamicRegistrations) registration.Dispose();
                _dynamicRegistrations.Clear();
                _drainingRegistrations.Clear();
                _unregisterOperations.Clear();
                _transitionConnectionBudget = 0;
            }
        }
        finally
        {
            _mutationGate.Release();
        }
        Interlocked.Exchange(ref _metadataOwner, null)?.Dispose();
        _shutdown.Dispose();
        Volatile.Write(ref _state, (int)SharpLinkMultiClusterState.Stopped);
        if (failures.Count == 1)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1)
            throw new AggregateException(failures);
    }

    private static async Task StopSlotsAsync(IEnumerable<SharpLinkClusterSlot> slots, List<Exception> failures)
    {
        foreach (var slot in slots)
        {
            try { await slot.Client.StopAsync().ConfigureAwait(false); }
            catch (Exception exception) { failures.Add(exception); }
            finally { slot.ReleaseMetadata(); }
        }
    }

    private async Task<SharpLinkAssemblyUnregisterResult> CompleteUnregisterAsync(
        SharpLinkClusterSlot slot,
        DynamicAssemblyRegistration registration,
        ClientRetirementBudget budget)
    {
        SharpLinkAssemblyUnregisterResult result;
        try
        {
            result = await ((ISharpLinkPreparedAssemblyClient)slot.Client).UnregisterAssemblyAsync(
                registration.Assembly, budget).ConfigureAwait(false);
        }
        catch
        {
            RestoreRoutesAfterRejectedUnregister(registration);
            lock (_gate)
                _drainingRegistrations.Remove(registration);
            throw;
        }
        if (result.ReferencesReleased)
        {
            lock (_gate)
            {
                _dynamicRegistrations.Remove(registration);
                _drainingRegistrations.Remove(registration);
                registration.Dispose();
            }
        }
        else
        {
            TrackFrameworkTask(
                CompleteDeferredUnregisterAsync(slot, registration),
                "MultiClusterDeferredAssemblyUnregister");
        }
        return result;
    }

    private async Task CompleteSharedUnregisterAsync(
        SharpLinkClusterSlot slot,
        DynamicAssemblyRegistration registration,
        ClientRetirementBudget budget,
        TaskCompletionSource<SharpLinkAssemblyUnregisterResult> completion)
    {
        // The caller publishes tracking under _gate; child lifecycle callbacks run after it leaves.
        await Task.Yield();
        try
        {
            completion.TrySetResult(await CompleteUnregisterAsync(
                slot,
                registration,
                budget).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            lock (_gate)
                _unregisterOperations.Remove(registration);
        }
    }

    private void RestoreRoutesAfterRejectedUnregister(DynamicAssemblyRegistration registration)
    {
        using var metadata = CreateMetadataStorage();
        if (registration.Slot.Client is not IDynamicAssemblyRegistrationInspector inspector ||
            !inspector.IsDynamicAssemblyRegistered(registration.Assembly))
        {
            return;
        }

        lock (_gate)
        {
            var state = (SharpLinkMultiClusterState)_state;
            if (state is SharpLinkMultiClusterState.Draining or SharpLinkMultiClusterState.Stopped ||
                !_dynamicRegistrations.Contains(registration))
            {
                return;
            }

            var snapshot = Volatile.Read(ref _snapshot);
            var capacity = checked(snapshot.Routes.Count + registration.Manifest.Contracts.Count);
            for (var copy = 0; copy < 3; copy++) metadata.ReserveCollection(capacity);
            var nextRoutes = new Dictionary<Type, SharpLinkClusterRouteRegistration>(capacity);
            var routeIds = new HashSet<long>(capacity);
            foreach (var pair in snapshot.Routes)
            { nextRoutes.Add(pair.Key, pair.Value); routeIds.Add(pair.Value.ContractId); }
            foreach (var contract in registration.Manifest.Contracts)
            {
                if (nextRoutes.ContainsKey(contract.ContractType) || !routeIds.Add(contract.ContractId))
                {
                    throw new InvalidOperationException(
                        $"Cannot restore contract '{contract.ContractName}' ({contract.ContractId}) because its route is already owned by another assembly or cluster.");
                }
                nextRoutes.Add(contract.ContractType, new SharpLinkClusterRouteRegistration(
                    contract.ContractType,
                    contract.ContractId,
                    contract.Fingerprint,
                    registration.Slot,
                    registration.Assembly));
            }
            PublishSnapshotLocked(MultiClusterSnapshot.Create(snapshot.Clusters, nextRoutes.ToFrozenDictionary(),
                snapshot.ConfiguredConnectionBudget, metadata, snapshot, (PreparedGeneratedManifest)registration.Manifest));
        }
    }

    private async Task CompleteDeferredUnregisterAsync(
        SharpLinkClusterSlot slot,
        DynamicAssemblyRegistration registration)
    {
        while ((SharpLinkMultiClusterState)Volatile.Read(ref _state) is not SharpLinkMultiClusterState.Stopped)
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(100),
                GetTimeProvider(slot.Client),
                _shutdown.Token).ConfigureAwait(false);
            if (slot.Client is IDynamicAssemblyRegistrationInspector inspector)
            {
                if (!inspector.IsDynamicAssemblyRegistered(registration.Assembly))
                {
                    lock (_gate)
                    {
                        _dynamicRegistrations.Remove(registration);
                        _drainingRegistrations.Remove(registration);
                        registration.Dispose();
                    }
                    return;
                }
                continue;
            }

            try
            {
                var result = await slot.Client.UnregisterAssemblyAsync(
                    registration.Assembly, TimeSpan.Zero).ConfigureAwait(false);
                if (!result.ReferencesReleased && IsDynamicAssemblyStillRegistered(slot, registration.Assembly))
                    continue;
                lock (_gate)
                {
                    _dynamicRegistrations.Remove(registration);
                    _drainingRegistrations.Remove(registration);
                    registration.Dispose();
                }
                return;
            }
            catch
            {
                // The owning child performs its own final module cleanup during StopAsync.
                return;
            }
        }
    }

    private async Task<SharpLinkAssemblyReplacementResult> CompleteTrackedReplacementAsync(
        SharpLinkClusterSlot slot,
        DynamicAssemblyRegistration registration,
        Assembly newAssembly,
        ISharpLinkGeneratedAssemblyManifest newManifest,
        ClientRetirementBudget budget,
        IDisposable metadataOwner)
    {
        try
        {
            return await CompleteReplacementAsync(
                slot, registration, newAssembly, newManifest, budget).ConfigureAwait(false);
        }
        finally
        {
            metadataOwner.Dispose();
            lock (_gate)
                _activeAssemblyReplacements--;
        }
    }

    private async Task<SharpLinkAssemblyReplacementResult> CompleteReplacementAsync(
        SharpLinkClusterSlot slot,
        DynamicAssemblyRegistration registration,
        Assembly newAssembly,
        ISharpLinkGeneratedAssemblyManifest newManifest,
        ClientRetirementBudget budget)
    {
        if (slot.Client is not ISharpLinkPreparedAssemblyClient preparedChild ||
            newManifest is not PreparedGeneratedManifest prepared)
            return SharpLinkAssemblyReplacementResult.Failure(Error(
                SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                "The internal child does not implement the prepared metadata transaction.", newAssembly));
        var childOperation = preparedChild.ReplacePreparedAssemblyAsync(
            registration.Assembly, prepared, budget);

        try
        {
            if (childOperation.IsCompleted)
            {
                var completedResult = await childOperation.ConfigureAwait(false);
                if (completedResult.Succeeded)
                    PublishReplacement(registration, newAssembly, newManifest);
                return completedResult;
            }

            // SharpLinkClient publishes the replacement before returning its pending drain operation.
            // Keep the coordinator route in the same state while old calls drain.
            PublishReplacement(registration, newAssembly, newManifest);
            return await childOperation.ConfigureAwait(false);
        }
        catch (Exception childException)
        {
            try
            {
                if (slot.Client is IDynamicAssemblyRegistrationInspector inspector &&
                    inspector.IsDynamicAssemblyRegistered(newAssembly))
                {
                    PublishReplacement(registration, newAssembly, newManifest);
                }
            }
            catch (Exception reconciliationException)
            {
                throw new AggregateException(childException, reconciliationException);
            }
            ExceptionDispatchInfo.Capture(childException).Throw();
            throw;
        }
    }

    private void PublishReplacement(
        DynamicAssemblyRegistration registration,
        Assembly newAssembly,
        ISharpLinkGeneratedAssemblyManifest newManifest)
    {
        using var metadata = CreateMetadataStorage();
        lock (_gate)
        {
            var state = (SharpLinkMultiClusterState)_state;
            if (state is SharpLinkMultiClusterState.Draining or SharpLinkMultiClusterState.Stopped)
                return;
            var index = _dynamicRegistrations.IndexOf(registration);
            if (index < 0) return;
            var snapshot = _snapshot;
            var capacity = checked(snapshot.Routes.Count + newManifest.Contracts.Count);
            metadata.ReserveCollection(capacity);
            metadata.ReserveCollection(capacity);
            var nextRoutes = new Dictionary<Type, SharpLinkClusterRouteRegistration>(capacity);
            foreach (var pair in snapshot.Routes) nextRoutes.Add(pair.Key, pair.Value);
            foreach (var contract in registration.Manifest.Contracts) nextRoutes.Remove(contract.ContractType);
            foreach (var contract in newManifest.Contracts)
                nextRoutes[contract.ContractType] = new SharpLinkClusterRouteRegistration(
                    contract.ContractType, contract.ContractId, contract.Fingerprint, registration.Slot, newAssembly);
            var replacement = new DynamicAssemblyRegistration(registration.Slot, newAssembly, newManifest);
            MultiClusterSnapshot? nextSnapshot = null;
            var published = false;
            try
            {
                nextSnapshot = MultiClusterSnapshot.Create(snapshot.Clusters, nextRoutes.ToFrozenDictionary(),
                    snapshot.ConfiguredConnectionBudget, metadata, snapshot, (PreparedGeneratedManifest)newManifest);
                _dynamicRegistrations[index] = replacement;
                PublishSnapshotLocked(nextSnapshot);
                published = true;
                registration.Dispose();
            }
            finally { if (!published) { nextSnapshot?.Dispose(); replacement.Dispose(); } }
        }
    }

    private static bool IsDynamicAssemblyStillRegistered(SharpLinkClusterSlot slot, Assembly assembly)
        => slot.Client is not IDynamicAssemblyRegistrationInspector client ||
           client.IsDynamicAssemblyRegistered(assembly);

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
    internal DynamicAssemblyRegistration(SharpLinkClusterSlot slot, Assembly assembly,
        ISharpLinkGeneratedAssemblyManifest manifest)
    {
        Slot = slot; Assembly = assembly; _manifest = manifest;
        _metadata = (manifest as PreparedGeneratedManifest)?.Preparation.Retain() ??
            throw new InvalidOperationException("Coordinator registrations require prepared metadata ownership.");
    }
    private DynamicAssemblyRegistration(DynamicAssemblyRegistration original)
    {
        Slot = original.Slot; Assembly = original.Assembly; _manifest = original.Manifest;
        _metadata = ((PreparedGeneratedManifest)_manifest).Preparation.Retain();
    }
    internal SharpLinkClusterSlot Slot { get; init; }
    internal Assembly Assembly { get; }
    internal ISharpLinkGeneratedAssemblyManifest Manifest => _manifest ??
        throw new ObjectDisposedException(nameof(DynamicAssemblyRegistration));
    public void Dispose()
    {
        _manifest = null;
        Interlocked.Exchange(ref _metadata, null)?.Dispose();
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
