using System.Reflection;
using System.Runtime.ExceptionServices;

namespace SharpLink.Client;

internal sealed partial class SharpLinkMultiClusterClient : ISharpLinkMultiClusterClient, ISharpLinkMultiClusterLifecycleControl
{
    private readonly SharpLinkMultiClusterOptions _options;
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
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(routeManifestSnapshot);
        _options = options;
        _snapshot = new MultiClusterSnapshot(
            clusters,
            routes,
            configuredConnectionBudget > 0
                ? configuredConnectionBudget
                : clusters.Values.Sum(static slot => slot.ConfiguredConnectionBudget));
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<SharpLinkMultiClusterClient>();
        _timeProvider = clusters.Values
            .Select(static slot => slot.Client)
            .OfType<ISharpLinkClientTimeProvider>()
            .FirstOrDefault()?.TimeProvider ?? TimeProvider.System;
        _frameworkTasks = new FrameworkTaskSupervisor((operation, exception) =>
            LogMultiClusterFrameworkTaskFailure(_logger, operation, exception));
    }

    public SharpLinkMultiClusterState State
    {
        get
        {
            var state = (SharpLinkMultiClusterState)Volatile.Read(ref _state);
            if (state is not SharpLinkMultiClusterState.Ready and not SharpLinkMultiClusterState.Degraded)
                return state;

            var slots = Volatile.Read(ref _snapshot).Slots;
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

    public TContract Get<TContract>() where TContract : IService
    {
        var state = State;
        if (state is SharpLinkMultiClusterState.Draining or SharpLinkMultiClusterState.Stopped or SharpLinkMultiClusterState.Faulted)
            throw new InvalidOperationException($"Multi-cluster client state '{state}' does not create proxies.");

        if (Volatile.Read(ref _snapshot).Routes.TryGetValue(typeof(TContract), out var route))
            return route.Slot.Client.Get<TContract>();

        throw new InvalidOperationException($"Proxy for service interface {typeof(TContract).FullName} is not routed to a cluster.");
    }

    public TContract GetWithMetadata<TContract>(SharpLinkMetadata metadata) where TContract : IService
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var state = State;
        if (state is SharpLinkMultiClusterState.Draining or SharpLinkMultiClusterState.Stopped or SharpLinkMultiClusterState.Faulted)
            throw new InvalidOperationException($"Multi-cluster client state '{state}' does not create proxies.");

        if (Volatile.Read(ref _snapshot).Routes.TryGetValue(typeof(TContract), out var route))
            return route.Slot.Client.GetWithMetadata<TContract>(metadata);

        throw new InvalidOperationException($"Proxy for service interface {typeof(TContract).FullName} is not routed to a cluster.");
    }

    public SharpLinkConnectionState GetClusterState(SharpLinkClusterKey cluster)
        => GetSlot(cluster).Client.State;

    public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(
        SharpLinkClusterKey cluster,
        CancellationToken cancellationToken = default)
        => GetSlot(cluster).Client.CheckHealthAsync(cancellationToken);

    public SharpLinkAssemblyRegistrationResult RegisterAssembly(SharpLinkClusterKey cluster, Assembly assembly)
    {
        if (assembly is null)
            return SharpLinkAssemblyManifestLoader.TryLoad(null, out _);

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

        var loaded = SharpLinkAssemblyManifestLoader.TryLoad(assembly, out var manifest);
        if (!loaded.Succeeded)
            return loaded;

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

            var childResult = slot.Client.RegisterAssembly(assembly);
            if (!childResult.Succeeded)
                return childResult;

            try
            {
                var nextRoutes = currentRoutes.ToDictionary(static pair => pair.Key, static pair => pair.Value);
                foreach (var contract in manifest.Contracts)
                {
                    nextRoutes.Add(contract.ContractType, new SharpLinkClusterRouteRegistration(
                        contract.ContractType, contract.ContractId, contract.Fingerprint, slot, assembly));
                }
                Volatile.Write(ref _snapshot, snapshot with { Routes = nextRoutes.ToFrozenDictionary() });
                _dynamicRegistrations.Add(new DynamicAssemblyRegistration(slot, assembly, manifest));
                return SharpLinkAssemblyRegistrationResult.Success();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                TrackFrameworkTask(
                    slot.Client.UnregisterAssemblyAsync(assembly, TimeSpan.Zero).AsTask(),
                    "MultiClusterRegistrationRollback");
                return Failure(SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                    $"Cluster route publication failed after child registration: {exception.GetType().Name}: {exception.Message}", assembly);
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

            var snapshot = Volatile.Read(ref _snapshot);
            var nextRoutes = snapshot.Routes
                .Where(pair => !ReferenceEquals(pair.Value.OwnerAssembly, assembly))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value)
                .ToFrozenDictionary();
            Volatile.Write(ref _snapshot, snapshot with { Routes = nextRoutes });

            var completion = new TaskCompletionSource<SharpLinkAssemblyUnregisterResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = completion.Task;
            _unregisterOperations.Add(registration, operation);
            _drainingRegistrations.Add(registration);
            _ = CompleteSharedUnregisterAsync(
                slot,
                registration,
                gracefulTimeout,
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
        }
        var loaded = SharpLinkAssemblyManifestLoader.TryLoad(newAssembly, out var newManifest);
        if (!loaded.Succeeded)
            return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(loaded.Error!));

        DynamicAssemblyRegistration? registration;
        lock (_gate)
        {
            var state = (SharpLinkMultiClusterState)_state;
            if (state is SharpLinkMultiClusterState.Draining or SharpLinkMultiClusterState.Stopped or SharpLinkMultiClusterState.Faulted)
            {
                return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(Error(
                    SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                    $"Multi-cluster client state '{state}' does not accept runtime assembly replacement.", newAssembly)));
            }

            registration = _dynamicRegistrations.FirstOrDefault(candidate =>
                ReferenceEquals(candidate.Slot, slot) && ReferenceEquals(candidate.Assembly, oldAssembly));
            if (registration is null)
            {
                return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(Error(
                    SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                    $"Assembly '{oldAssembly.FullName}' is not registered in cluster '{cluster}'.", newAssembly)));
            }

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
            slot, registration!, newAssembly, newManifest!, gracefulTimeout);
        TrackFrameworkTask(
            operation,
            "MultiClusterAssemblyReplacement",
            TaskObservationMode.ExternallyObserved);
        return WaitForOperationAsync(operation, cancellationToken);
    }

    private async Task ConnectCoreAsync()
    {
        Volatile.Write(ref _state, (int)SharpLinkMultiClusterState.Connecting);
        using var attempts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        try
        {
            await Parallel.ForEachAsync(
                Volatile.Read(ref _snapshot).Clusters.Values,
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
            var failures = new List<Exception> { connectException };
            await StopSlotsAsync(Volatile.Read(ref _snapshot).Clusters.Values, failures).ConfigureAwait(false);
            // StopAsync owns the terminal transition. A connect completion may only replace the
            // original Connecting state, never Draining or Stopped.
            _ = Interlocked.CompareExchange(
                ref _state,
                (int)SharpLinkMultiClusterState.Faulted,
                (int)SharpLinkMultiClusterState.Connecting);
            if (failures.Count == 1)
                ExceptionDispatchInfo.Capture(connectException).Throw();
            throw new AggregateException(failures);
        }
    }

    private async Task StopCoreAsync()
    {
        Volatile.Write(ref _state, (int)SharpLinkMultiClusterState.Draining);
        _frameworkTasks.Seal();
        var failures = new List<Exception>();
        try { await _shutdown.CancelAsync().ConfigureAwait(false); }
        catch (Exception exception) { failures.Add(exception); }
        await _mutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var slots = Volatile.Read(ref _snapshot).Clusters.Values.ToArray();
            await StopSlotsAsync(slots, failures).ConfigureAwait(false);
            try { await _frameworkTasks.DrainAsync().ConfigureAwait(false); }
            catch (Exception exception) { failures.Add(exception); }
            lock (_gate)
            {
                Volatile.Write(ref _snapshot, MultiClusterSnapshot.Empty);
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
        }
    }

    private async Task<SharpLinkAssemblyUnregisterResult> CompleteUnregisterAsync(
        SharpLinkClusterSlot slot,
        DynamicAssemblyRegistration registration,
        TimeSpan gracefulTimeout)
    {
        SharpLinkAssemblyUnregisterResult result;
        try
        {
            result = await slot.Client.UnregisterAssemblyAsync(
                registration.Assembly, gracefulTimeout).ConfigureAwait(false);
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
        TimeSpan gracefulTimeout,
        TaskCompletionSource<SharpLinkAssemblyUnregisterResult> completion)
    {
        try
        {
            completion.TrySetResult(await CompleteUnregisterAsync(
                slot,
                registration,
                gracefulTimeout).ConfigureAwait(false));
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
            var nextRoutes = snapshot.Routes
                .ToDictionary(static pair => pair.Key, static pair => pair.Value);
            var routeIds = nextRoutes.Values
                .Select(static route => route.ContractId)
                .ToHashSet();
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
            Volatile.Write(ref _snapshot, snapshot with { Routes = nextRoutes.ToFrozenDictionary() });
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
        TimeSpan gracefulTimeout)
    {
        try
        {
            return await CompleteReplacementAsync(
                slot, registration, newAssembly, newManifest, gracefulTimeout).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
                _activeAssemblyReplacements--;
        }
    }

    private async Task<SharpLinkAssemblyReplacementResult> CompleteReplacementAsync(
        SharpLinkClusterSlot slot,
        DynamicAssemblyRegistration registration,
        Assembly newAssembly,
        ISharpLinkGeneratedAssemblyManifest newManifest,
        TimeSpan gracefulTimeout)
    {
        var childOperation = slot.Client.ReplaceAssemblyAsync(
            registration.Assembly, newAssembly, gracefulTimeout);

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
        lock (_gate)
        {
            // A successful child replacement may race coordinator shutdown. StopAsync owns the
            // child cleanup in that case; do not republish routes or retain the new assembly.
            var state = (SharpLinkMultiClusterState)_state;
            if (state is SharpLinkMultiClusterState.Draining or SharpLinkMultiClusterState.Stopped)
                return;

            if (!_dynamicRegistrations.Remove(registration))
                return;
            _dynamicRegistrations.Add(new DynamicAssemblyRegistration(registration.Slot, newAssembly, newManifest));
            var snapshot = Volatile.Read(ref _snapshot);
            var nextRoutes = snapshot.Routes.ToDictionary(static pair => pair.Key, static pair => pair.Value);
            foreach (var contract in registration.Manifest.Contracts)
                nextRoutes.Remove(contract.ContractType);
            foreach (var contract in newManifest.Contracts)
            {
                nextRoutes[contract.ContractType] = new SharpLinkClusterRouteRegistration(
                    contract.ContractType, contract.ContractId, contract.Fingerprint, registration.Slot, newAssembly);
            }
            Volatile.Write(ref _snapshot, snapshot with { Routes = nextRoutes.ToFrozenDictionary() });
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

    internal FrameworkTaskSupervisorSnapshot FrameworkTaskSnapshotForDiagnostics
        => _frameworkTasks.CaptureSnapshot();

    private SharpLinkClusterSlot GetSlot(SharpLinkClusterKey cluster)
    {
        if (!SharpLinkClusterKey.IsValid(cluster.Value))
            throw new ArgumentException("A valid non-default SharpLinkClusterKey is required.", nameof(cluster));
        if (Volatile.Read(ref _snapshot).Clusters.TryGetValue(cluster, out var slot))
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
    IReadOnlyList<ISharpLinkGeneratedAssemblyManifest>? StaticManifests = null);

internal sealed record SharpLinkClusterRouteRegistration(
    Type ContractType,
    long ContractId,
    string Fingerprint,
    SharpLinkClusterSlot Slot,
    Assembly OwnerAssembly);

internal sealed record DynamicAssemblyRegistration(
    SharpLinkClusterSlot Slot,
    Assembly Assembly,
    ISharpLinkGeneratedAssemblyManifest Manifest);

internal sealed record MultiClusterSnapshot(
    FrozenDictionary<SharpLinkClusterKey, SharpLinkClusterSlot> Clusters,
    FrozenDictionary<Type, SharpLinkClusterRouteRegistration> Routes,
    int ConfiguredConnectionBudget)
{
    internal SharpLinkClusterSlot[] Slots { get; } = [.. Clusters.Values];

    internal static MultiClusterSnapshot Empty { get; } = new(
        FrozenDictionary<SharpLinkClusterKey, SharpLinkClusterSlot>.Empty,
        FrozenDictionary<Type, SharpLinkClusterRouteRegistration>.Empty,
        0);
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
