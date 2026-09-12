namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    internal SharpLinkRuntimeConfigurationUpdateResult TryUpdateFixedConnectionPoolSizingCore(
        int minConnections,
        int maxConnections)
    {
        SharpLinkConnectionPoolSizingExtensions.ValidateFixed(minConnections, maxConnections);
        lock (_stateGate)
        {
            var state = State;
            if (IsRuntimeConfigurationPublicationClosed(state))
                return LifecycleClosed($"Connection-pool sizing cannot be updated while the client is {state}.");
            if (_cluster is not null)
            {
                return ModeConflict(
                    "The active Client uses an endpoint cluster. Use TryUpdateClusterConnectionPoolSizing instead.");
            }
            if (_connectionPoolOptions.MinConnections == minConnections &&
                _connectionPoolOptions.MaxConnections == maxConnections)
            {
                return SharpLinkRuntimeConfigurationUpdateResult.Success();
            }

            AdvanceConnectionPoolSizingGenerationLocked();
            _connectionPoolOptions.PublishRuntimeSizing(minConnections, maxConnections);
        }

        if (ReadyConnectionCount < minConnections)
            EnsureReconnectLoop();
        ScheduleConnectionPoolSizingReconciliation();
        return SharpLinkRuntimeConfigurationUpdateResult.Success();
    }

    internal SharpLinkRuntimeConfigurationUpdateResult TryUpdateClusterConnectionPoolSizingCore(
        int maxConnections,
        int maxConnectionsPerEndpoint)
    {
        SharpLinkConnectionPoolSizingExtensions.ValidateCluster(maxConnections, maxConnectionsPerEndpoint);
        lock (_stateGate)
        {
            var state = State;
            if (IsRuntimeConfigurationPublicationClosed(state))
                return LifecycleClosed($"Connection-pool sizing cannot be updated while the client is {state}.");
            if (_cluster is null)
            {
                return ModeConflict(
                    "The active Client uses a fixed endpoint pool. Use TryUpdateFixedConnectionPoolSizing instead.");
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
                return SharpLinkRuntimeConfigurationUpdateResult.Success();
            }

            AdvanceConnectionPoolSizingGenerationLocked();
            options.PublishRuntimeConnectionLimits(maxConnections, maxConnectionsPerEndpoint);
        }

        ScheduleConnectionPoolSizingReconciliation();
        return SharpLinkRuntimeConfigurationUpdateResult.Success();
    }

    internal SharpLinkRuntimeConfigurationUpdateResult TryUpdateLoadBalancingCore(
        SharpLinkLoadBalancingStrategy strategy)
    {
        if (!Enum.IsDefined(strategy))
            throw new ArgumentOutOfRangeException(nameof(strategy));

        lock (_stateGate)
        {
            var state = State;
            if (IsRuntimeConfigurationPublicationClosed(state))
                return LifecycleClosed($"Endpoint selection policy cannot be updated while the client is {state}.");
            if (_cluster is null)
                return ModeConflict("Fixed-endpoint clients do not support endpoint-selection policy updates.");

            _cluster.UpdateLoadBalancing(strategy);
            return SharpLinkRuntimeConfigurationUpdateResult.Success();
        }
    }

    internal SharpLinkRuntimeConfigurationUpdateResult TryUpdateEndpointSelectorCore(
        ISharpLinkEndpointSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);

        lock (_stateGate)
        {
            var state = State;
            if (IsRuntimeConfigurationPublicationClosed(state))
                return LifecycleClosed($"Endpoint selection policy cannot be updated while the client is {state}.");
            if (_cluster is null)
                return ModeConflict("Fixed-endpoint clients do not support endpoint-selection policy updates.");

            _cluster.UpdateEndpointSelector(selector);
            return SharpLinkRuntimeConfigurationUpdateResult.Success();
        }
    }

    internal SharpLinkRuntimeConfigurationUpdateResult TryUpdateTelemetryDetailPolicyCore(
        SharpLinkTelemetryDetailMode mode)
    {
        SharpLinkTelemetryDetailExtensions.Validate(mode);
        lock (_stateGate)
        {
            var state = State;
            if (IsRuntimeConfigurationPublicationClosed(state))
                return LifecycleClosed($"Telemetry detail policy cannot be updated while the client is {state}.");

            var current = CaptureTelemetryDetailGeneration();
            if (current.Mode == mode)
                return SharpLinkRuntimeConfigurationUpdateResult.Success();
            if (current.Generation == ulong.MaxValue)
                throw new InvalidOperationException("Telemetry detail policy generation is exhausted.");

            Volatile.Write(
                ref _telemetryDetailGeneration,
                new SharpLinkTelemetryDetailGeneration(current.Generation + 1, mode));
            return SharpLinkRuntimeConfigurationUpdateResult.Success();
        }
    }
}

/// <summary>Structured expected-rejection paths for Client topology and telemetry runtime controls.</summary>
public static class SharpLinkClientTopologyRuntimeConfigurationExtensions
{
    /// <summary>Attempts to publish fixed-endpoint pool sizing without throwing for lifecycle or topology-mode rejection.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateFixedConnectionPoolSizing(
        this ISharpLinkClient client,
        int minConnections,
        int maxConnections)
        => GetRuntime(client) is { } runtime
            ? runtime.TryUpdateFixedConnectionPoolSizingCore(minConnections, maxConnections)
            : Unsupported(nameof(TryUpdateFixedConnectionPoolSizing));

    /// <summary>Attempts to publish endpoint-cluster pool sizing without throwing for lifecycle or topology-mode rejection.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateClusterConnectionPoolSizing(
        this ISharpLinkClient client,
        int maxConnections,
        int maxConnectionsPerEndpoint)
        => GetRuntime(client) is { } runtime
            ? runtime.TryUpdateClusterConnectionPoolSizingCore(maxConnections, maxConnectionsPerEndpoint)
            : Unsupported(nameof(TryUpdateClusterConnectionPoolSizing));

    /// <summary>Attempts to publish a built-in endpoint-selection strategy.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateLoadBalancing(
        this ISharpLinkClient client,
        SharpLinkLoadBalancingStrategy strategy)
        => GetRuntime(client) is { } runtime
            ? runtime.TryUpdateLoadBalancingCore(strategy)
            : Unsupported(nameof(TryUpdateLoadBalancing));

    /// <summary>Attempts to publish a custom endpoint selector.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateEndpointSelector(
        this ISharpLinkClient client,
        ISharpLinkEndpointSelector selector)
        => GetRuntime(client) is { } runtime
            ? runtime.TryUpdateEndpointSelectorCore(selector)
            : Unsupported(nameof(TryUpdateEndpointSelector));

    /// <summary>Attempts to publish the telemetry detail policy.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateTelemetryDetailPolicy(
        this ISharpLinkClient client,
        SharpLinkTelemetryDetailMode mode)
        => GetRuntime(client) is { } runtime
            ? runtime.TryUpdateTelemetryDetailPolicyCore(mode)
            : Unsupported(nameof(TryUpdateTelemetryDetailPolicy));

    private static SharpLinkClient? GetRuntime(ISharpLinkClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client as SharpLinkClient;
    }

    private static SharpLinkRuntimeConfigurationUpdateResult Unsupported(string operation)
        => SharpLinkRuntimeConfigurationUpdateResult.Failure(
            SharpLinkRuntimeConfigurationUpdateFailureCode.UnsupportedByImplementation,
            $"This ISharpLinkClient implementation does not expose the structured runtime configuration operation '{operation}'.");
}
