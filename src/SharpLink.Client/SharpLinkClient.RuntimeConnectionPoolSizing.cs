using System.Runtime.CompilerServices;

namespace SharpLink.Client;

internal sealed partial class SharpLinkClient : ISharpLinkConnectionPoolSizingRuntime
{
    private ulong _connectionPoolSizingGeneration;
    private Task? _connectionPoolSizingReconcileTask;

    public SharpLinkConnectionPoolSizingSnapshot GetConnectionPoolSizingSnapshot()
    {
        lock (_stateGate)
        {
            if (_cluster is not null)
            {
                var options = GetClusterOptions(_cluster);
                return new SharpLinkConnectionPoolSizingSnapshot(
                    _connectionPoolSizingGeneration,
                    SharpLinkConnectionPoolSizingKind.EndpointCluster,
                    MinConnections: 0,
                    options.MaxConnections,
                    options.MaxConnectionsPerEndpoint);
            }

            return new SharpLinkConnectionPoolSizingSnapshot(
                _connectionPoolSizingGeneration,
                SharpLinkConnectionPoolSizingKind.FixedEndpoint,
                _connectionPoolOptions.MinConnections,
                _connectionPoolOptions.MaxConnections,
                MaxConnectionsPerEndpoint: 0);
        }
    }

    public void UpdateFixedConnectionPoolSizing(int minConnections, int maxConnections)
    {
        SharpLinkConnectionPoolSizingExtensions.ValidateFixed(minConnections, maxConnections);
        lock (_stateGate)
        {
            EnsureConnectionPoolSizingPublicationAllowed();
            if (_cluster is not null)
            {
                throw new InvalidOperationException(
                    "The active Client uses an endpoint cluster. Use UpdateClusterConnectionPoolSizing instead.");
            }
            if (_connectionPoolOptions.MinConnections == minConnections &&
                _connectionPoolOptions.MaxConnections == maxConnections)
            {
                return;
            }

            AdvanceConnectionPoolSizingGenerationLocked();
            _connectionPoolOptions.PublishRuntimeSizing(minConnections, maxConnections);
        }

        if (ReadyConnectionCount < minConnections)
            EnsureReconnectLoop();
        ScheduleConnectionPoolSizingReconciliation();
    }

    public void UpdateClusterConnectionPoolSizing(int maxConnections, int maxConnectionsPerEndpoint)
    {
        SharpLinkConnectionPoolSizingExtensions.ValidateCluster(maxConnections, maxConnectionsPerEndpoint);
        lock (_stateGate)
        {
            EnsureConnectionPoolSizingPublicationAllowed();
            if (_cluster is null)
            {
                throw new InvalidOperationException(
                    "The active Client uses a fixed endpoint pool. Use UpdateFixedConnectionPoolSizing instead.");
            }
            if (maxConnections < _maximumReadinessWaitThreshold)
            {
                throw new ArgumentException(
                    "The cluster MaxConnections cannot be smaller than its configured ready-endpoint target.",
                    nameof(maxConnections));
            }

            var options = GetClusterOptions(_cluster);
            if (options.MaxConnections == maxConnections &&
                options.MaxConnectionsPerEndpoint == maxConnectionsPerEndpoint)
            {
                return;
            }

            AdvanceConnectionPoolSizingGenerationLocked();
            options.PublishRuntimeConnectionLimits(maxConnections, maxConnectionsPerEndpoint);
        }

        ScheduleConnectionPoolSizingReconciliation();
    }

    private void EnsureConnectionPoolSizingPublicationAllowed()
    {
        var state = State;
        if (Volatile.Read(ref _stopStarted) != 0 ||
            state is SharpLinkConnectionState.Draining or
                SharpLinkConnectionState.Stopped or
                SharpLinkConnectionState.Faulted)
        {
            throw new InvalidOperationException(
                $"Connection-pool sizing cannot be updated while the client is {state}.");
        }
    }

    private void AdvanceConnectionPoolSizingGenerationLocked()
    {
        if (_connectionPoolSizingGeneration == ulong.MaxValue)
            throw new InvalidOperationException("Connection-pool sizing generation is exhausted.");
        _connectionPoolSizingGeneration++;
    }

    private void ScheduleConnectionPoolSizingReconciliation()
    {
        lock (_stateGate)
        {
            if (Volatile.Read(ref _stopStarted) != 0 || _shutdownCts.IsCancellationRequested)
                return;
            if (_connectionPoolSizingReconcileTask is { IsCompleted: false })
                return;

            _connectionPoolSizingReconcileTask = ReconcileConnectionPoolSizingAsync();
            TrackFrameworkTask(
                _connectionPoolSizingReconcileTask,
                "ConnectionPoolSizingReconciliation");
        }
    }

    private async Task ReconcileConnectionPoolSizingAsync()
    {
        while (!_shutdownCts.IsCancellationRequested)
        {
            var needsMore = _cluster is null
                ? ReconcileFixedPoolSizing()
                : ReconcileClusterPoolSizing(_cluster);
            if (!needsMore)
                return;

            try
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(25),
                    _runtimeContext.TimeProvider,
                    _shutdownCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private bool ReconcileFixedPoolSizing()
    {
        List<ClientConnection>? marked = null;
        lock (_poolGate)
        {
            if (_poolStopping)
                return false;

            // Planned sources stay physically Ready to finish admitted work. They are
            // outside selectable capacity and must not make their replacement look surplus.
            var ready = _connections
                .Where(static connection => connection.CanAcceptCalls)
                .OrderBy(static connection => connection.ActiveCallCount == 0 ? 0 : 1)
                .ThenBy(static connection => connection.ActiveCallCount)
                .ToArray();
            var surplus = ready.Length - _connectionPoolOptions.MaxConnections;
            for (var index = 0; surplus > 0 && index < ready.Length; index++)
            {
                if (!ready[index].MarkDraining())
                    continue;
                (marked ??= []).Add(ready[index]);
                surplus--;
            }

            if (marked is not null)
                PublishReadySnapshotLocked();
        }

        if (marked is not null)
        {
            for (var index = 0; index < marked.Count; index++)
                RetireDrainingConnectionIfIdle(marked[index]);
        }

        if (ReadyConnectionCount < _connectionPoolOptions.MinConnections)
            EnsureReconnectLoop();

        return ReadyConnectionCount > _connectionPoolOptions.MaxConnections ||
               ReadyConnectionCount < _connectionPoolOptions.MinConnections ||
               _expansionTask is { IsCompleted: false } ||
               _connectTask is { IsCompleted: false };
    }

    private bool ReconcileClusterPoolSizing(IEndpointClusterRuntime cluster)
    {
        var options = GetClusterOptions(cluster);
        var ready = cluster.CaptureReadyConnections();
        if (ready.Length == 0)
            return HasInFlightClusterConnections(cluster);

        var remaining = new List<ClientConnection>(ready);
        var groups = remaining
            .GroupBy(static connection => (connection.EndpointId, connection.EndpointGeneration))
            .ToArray();

        for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            var group = groups[groupIndex]
                .Where(static connection => connection.CanAcceptCalls)
                .OrderBy(static connection => connection.ActiveCallCount == 0 ? 0 : 1)
                .ThenBy(static connection => connection.ActiveCallCount)
                .ToArray();
            var surplus = group.Length - options.MaxConnectionsPerEndpoint;
            for (var index = 0; surplus > 0 && index < group.Length; index++)
            {
                if (!TryBeginResizeRetirement(cluster, group[index], options.MaxRetiringConnections))
                    continue;
                remaining.Remove(group[index]);
                surplus--;
            }
        }

        var totalSurplus = remaining.Count - options.MaxConnections;
        if (totalSurplus > 0)
        {
            var endpointCounts = remaining
                .GroupBy(static connection => (connection.EndpointId, connection.EndpointGeneration))
                .ToDictionary(static group => group.Key, static group => group.Count());
            var readyEndpointCount = endpointCounts.Count;
            var candidates = remaining
                .OrderBy(static connection => connection.ActiveCallCount == 0 ? 0 : 1)
                .ThenBy(static connection => connection.ActiveCallCount)
                .ToArray();

            for (var index = 0; totalSurplus > 0 && index < candidates.Length; index++)
            {
                var candidate = candidates[index];
                var key = (candidate.EndpointId, candidate.EndpointGeneration);
                var count = endpointCounts[key];
                if (count == 1 && readyEndpointCount <= _maximumReadinessWaitThreshold)
                    continue;
                if (!TryBeginResizeRetirement(cluster, candidate, options.MaxRetiringConnections))
                    continue;

                endpointCounts[key] = count - 1;
                if (count == 1)
                    readyEndpointCount--;
                remaining.Remove(candidate);
                totalSurplus--;
            }
        }

        var after = cluster.CaptureReadyConnections();
        var perEndpointExceeded = after
            .GroupBy(static connection => (connection.EndpointId, connection.EndpointGeneration))
            .Any(group => group.Count() > options.MaxConnectionsPerEndpoint);
        return after.Length > options.MaxConnections ||
               perEndpointExceeded ||
               HasInFlightClusterConnections(cluster);
    }

    private static bool TryBeginResizeRetirement(
        IEndpointClusterRuntime cluster,
        ClientConnection connection,
        int maxRetiringConnections)
    {
        if (!connection.CanAcceptCalls)
            return false;

        // Serialize the capacity check and the normal cluster retirement transition under the
        // cluster's own gate. MarkConnectionDraining is deliberately re-entered while that gate is
        // held so its existing retiring collection becomes the single reservation domain for both
        // resize and GoAway/topology retirement. No second resize-owned retiring set exists.
        var gate = GetClusterGate(cluster);
        lock (gate)
        {
            if (!connection.CanAcceptCalls)
                return false;
            if (connection.ActiveCallCount != 0 &&
                GetClusterRetiringConnectionCountLocked(cluster) >= maxRetiringConnections)
            {
                return false;
            }

            cluster.MarkConnectionDraining(connection);
            return connection.State != ClientConnectionState.Ready;
        }
    }

    private static int GetClusterRetiringConnectionCountLocked(IEndpointClusterRuntime cluster)
        => cluster switch
        {
            StaticClusterRuntime staticCluster => GetStaticClusterRetiringConnections(staticCluster).Count,
            DynamicClusterRuntime dynamicCluster => GetDynamicClusterConnections(dynamicCluster).RetiringConnectionCount,
            _ => 0
        };

    private static Lock GetClusterGate(IEndpointClusterRuntime cluster)
        => cluster switch
        {
            StaticClusterRuntime staticCluster => GetStaticClusterGate(staticCluster),
            DynamicClusterRuntime dynamicCluster => GetDynamicClusterGate(dynamicCluster),
            _ => throw new UnreachableException()
        };

    private static bool HasInFlightClusterConnections(IEndpointClusterRuntime cluster)
        => cluster switch
        {
            StaticClusterRuntime staticCluster => GetStaticClusterEndpoints(staticCluster)
                .Any(static endpoint => endpoint.ConnectingCount != 0),
            DynamicClusterRuntime dynamicCluster => GetDynamicClusterTopology(dynamicCluster).States
                .Any(static endpoint => endpoint.ConnectingCount != 0),
            _ => false
        };

    private static SharpLinkClusterOptions GetClusterOptions(IEndpointClusterRuntime cluster)
        => cluster switch
        {
            StaticClusterRuntime staticCluster => GetStaticClusterOptions(staticCluster),
            DynamicClusterRuntime dynamicCluster => GetDynamicClusterOptions(dynamicCluster),
            _ => throw new UnreachableException()
        };

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_options")]
    private static extern ref SharpLinkClusterOptions GetStaticClusterOptions(StaticClusterRuntime cluster);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_options")]
    private static extern ref SharpLinkClusterOptions GetDynamicClusterOptions(DynamicClusterRuntime cluster);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_gate")]
    private static extern ref Lock GetStaticClusterGate(StaticClusterRuntime cluster);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_gate")]
    private static extern ref Lock GetDynamicClusterGate(DynamicClusterRuntime cluster);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_retiringConnections")]
    private static extern ref HashSet<ClientConnection> GetStaticClusterRetiringConnections(StaticClusterRuntime cluster);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_connections")]
    private static extern ref DynamicClusterConnectionState GetDynamicClusterConnections(DynamicClusterRuntime cluster);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_endpoints")]
    private static extern ref StaticClientRuntimeEndpointState[] GetStaticClusterEndpoints(StaticClusterRuntime cluster);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_current")]
    private static extern ref DynamicClusterTopologyState GetDynamicClusterTopology(DynamicClusterRuntime cluster);
}
