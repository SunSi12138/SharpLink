using System.Reflection;
using System.Runtime.ExceptionServices;

namespace SharpLink.Client;

internal sealed class SharpLinkMultiClusterClient : ISharpLinkMultiClusterClient
{
    private readonly SharpLinkMultiClusterOptions _options;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private FrozenDictionary<SharpLinkClusterKey, SharpLinkClusterSlot> _clusters;
    private FrozenDictionary<Type, SharpLinkClusterRouteRegistration> _routes;
    private readonly List<DynamicAssemblyRegistration> _dynamicRegistrations = [];
    private Task? _connectTask;
    private Task? _stopTask;
    private int _state = (int)SharpLinkMultiClusterState.Created;

    internal SharpLinkMultiClusterClient(
        SharpLinkMultiClusterOptions options,
        FrozenDictionary<SharpLinkClusterKey, SharpLinkClusterSlot> clusters,
        FrozenDictionary<Type, SharpLinkClusterRouteRegistration> routes,
        IReadOnlyList<ISharpLinkGeneratedClusterRouteManifest> routeManifestSnapshot)
    {
        _ = routeManifestSnapshot;
        _options = options;
        _clusters = clusters;
        _routes = routes;
    }

    public SharpLinkMultiClusterState State
    {
        get
        {
            var state = (SharpLinkMultiClusterState)Volatile.Read(ref _state);
            if (state is not SharpLinkMultiClusterState.Ready and not SharpLinkMultiClusterState.Degraded)
                return state;

            var slots = Volatile.Read(ref _clusters);
            if (slots.Count == 0)
                return state;
            var ready = slots.Values.Count(static slot => slot.Client.State == SharpLinkConnectionState.Ready);
            return ready == slots.Count ? SharpLinkMultiClusterState.Ready : SharpLinkMultiClusterState.Degraded;
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

            _connectTask ??= ConnectCoreAsync();
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

        if (Volatile.Read(ref _routes).TryGetValue(typeof(TContract), out var route))
            return route.Slot.Client.Get<TContract>();

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

        var slot = GetSlot(cluster);
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

            var currentRoutes = Volatile.Read(ref _routes);
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
                Volatile.Write(ref _routes, nextRoutes.ToFrozenDictionary());
                _dynamicRegistrations.Add(new DynamicAssemblyRegistration(slot, assembly, manifest));
                return SharpLinkAssemblyRegistrationResult.Success();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                _ = slot.Client.UnregisterAssemblyAsync(assembly, TimeSpan.Zero);
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
        var slot = GetSlot(cluster);
        DynamicAssemblyRegistration? registration;
        lock (_gate)
        {
            registration = _dynamicRegistrations.FirstOrDefault(candidate =>
                ReferenceEquals(candidate.Slot, slot) && ReferenceEquals(candidate.Assembly, assembly));
            if (registration is null)
                return ValueTask.FromResult(new SharpLinkAssemblyUnregisterResult { ReferencesReleased = false });

            var nextRoutes = Volatile.Read(ref _routes)
                .Where(pair => !ReferenceEquals(pair.Value.OwnerAssembly, assembly))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value)
                .ToFrozenDictionary();
            Volatile.Write(ref _routes, nextRoutes);
        }

        var operation = CompleteUnregisterAsync(slot, registration!, gracefulTimeout);
        ObserveBackgroundFailure(operation);
        return WaitForOperationAsync(operation, cancellationToken);
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
        var slot = GetSlot(cluster);
        var loaded = SharpLinkAssemblyManifestLoader.TryLoad(newAssembly, out var newManifest);
        if (!loaded.Succeeded)
            return ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(loaded.Error!));

        DynamicAssemblyRegistration? registration;
        lock (_gate)
        {
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

        }

        var operation = CompleteReplacementAsync(
            slot, registration!, newAssembly, newManifest!, gracefulTimeout);
        ObserveBackgroundFailure(operation);
        return WaitForOperationAsync(operation, cancellationToken);
    }

    private async Task ConnectCoreAsync()
    {
        Volatile.Write(ref _state, (int)SharpLinkMultiClusterState.Connecting);
        using var attempts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        try
        {
            await Parallel.ForEachAsync(
                Volatile.Read(ref _clusters).Values,
                new ParallelOptions { CancellationToken = attempts.Token, MaxDegreeOfParallelism = _options.MaxConcurrentClusterConnects },
                static async (slot, token) => await slot.Client.ConnectAsync(token).ConfigureAwait(false)).ConfigureAwait(false);
            Volatile.Write(ref _state, (int)SharpLinkMultiClusterState.Ready);
        }
        catch (Exception connectException)
        {
            attempts.Cancel();
            var failures = new List<Exception> { connectException };
            await StopSlotsAsync(Volatile.Read(ref _clusters).Values, failures).ConfigureAwait(false);
            // StopAsync can race the first shared connect. It owns the final Stopped transition,
            // so a cancelled connect must never overwrite it with Faulted after cleanup completes.
            var state = (SharpLinkMultiClusterState)Volatile.Read(ref _state);
            if (state is not SharpLinkMultiClusterState.Draining and not SharpLinkMultiClusterState.Stopped)
                Volatile.Write(ref _state, (int)SharpLinkMultiClusterState.Faulted);
            if (failures.Count == 1)
                ExceptionDispatchInfo.Capture(connectException).Throw();
            throw new AggregateException(failures);
        }
    }

    private async Task StopCoreAsync()
    {
        Volatile.Write(ref _state, (int)SharpLinkMultiClusterState.Draining);
        _shutdown.Cancel();
        var slots = Volatile.Read(ref _clusters).Values.ToArray();
        var failures = new List<Exception>();
        await StopSlotsAsync(slots, failures).ConfigureAwait(false);
        lock (_gate)
        {
            Volatile.Write(ref _routes, FrozenDictionary<Type, SharpLinkClusterRouteRegistration>.Empty);
            Volatile.Write(ref _clusters, FrozenDictionary<SharpLinkClusterKey, SharpLinkClusterSlot>.Empty);
            _dynamicRegistrations.Clear();
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
            throw;
        }
        if (result.ReferencesReleased)
        {
            lock (_gate)
                _dynamicRegistrations.Remove(registration);
        }
        else
        {
            ObserveBackgroundFailure(CompleteDeferredUnregisterAsync(slot, registration));
        }
        return result;
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

            var nextRoutes = Volatile.Read(ref _routes)
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
            Volatile.Write(ref _routes, nextRoutes.ToFrozenDictionary());
        }
    }

    private async Task CompleteDeferredUnregisterAsync(
        SharpLinkClusterSlot slot,
        DynamicAssemblyRegistration registration)
    {
        while ((SharpLinkMultiClusterState)Volatile.Read(ref _state) is not SharpLinkMultiClusterState.Stopped)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
            if (slot.Client is IDynamicAssemblyRegistrationInspector inspector)
            {
                if (!inspector.IsDynamicAssemblyRegistered(registration.Assembly))
                {
                    lock (_gate)
                        _dynamicRegistrations.Remove(registration);
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
                    _dynamicRegistrations.Remove(registration);
                return;
            }
            catch
            {
                // The owning child performs its own final module cleanup during StopAsync.
                return;
            }
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

            _dynamicRegistrations.Remove(registration);
            _dynamicRegistrations.Add(new DynamicAssemblyRegistration(registration.Slot, newAssembly, newManifest));
            var nextRoutes = Volatile.Read(ref _routes).ToDictionary(static pair => pair.Key, static pair => pair.Value);
            foreach (var contract in registration.Manifest.Contracts)
                nextRoutes.Remove(contract.ContractType);
            foreach (var contract in newManifest.Contracts)
            {
                nextRoutes[contract.ContractType] = new SharpLinkClusterRouteRegistration(
                    contract.ContractType, contract.ContractId, contract.Fingerprint, registration.Slot, newAssembly);
            }
            Volatile.Write(ref _routes, nextRoutes.ToFrozenDictionary());
        }
    }

    private static bool IsDynamicAssemblyStillRegistered(SharpLinkClusterSlot slot, Assembly assembly)
        => slot.Client is not IDynamicAssemblyRegistrationInspector client ||
           client.IsDynamicAssemblyRegistered(assembly);

    private static ValueTask<T> WaitForOperationAsync<T>(Task<T> operation, CancellationToken cancellationToken)
        => cancellationToken.CanBeCanceled
            ? new ValueTask<T>(operation.WaitAsync(cancellationToken))
            : new ValueTask<T>(operation);

    private static void ObserveBackgroundFailure(Task task)
    {
        _ = task.ContinueWith(
            static completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private SharpLinkClusterSlot GetSlot(SharpLinkClusterKey cluster)
    {
        if (!SharpLinkClusterKey.IsValid(cluster.Value))
            throw new ArgumentException("A valid non-default SharpLinkClusterKey is required.", nameof(cluster));
        if (Volatile.Read(ref _clusters).TryGetValue(cluster, out var slot))
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
}

internal sealed record SharpLinkClusterSlot(
    SharpLinkClusterKey Key,
    ISharpLinkClient Client,
    bool AllowDynamicContracts);

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

internal interface IDynamicAssemblyRegistrationInspector
{
    bool IsDynamicAssemblyRegistered(Assembly assembly);
}
