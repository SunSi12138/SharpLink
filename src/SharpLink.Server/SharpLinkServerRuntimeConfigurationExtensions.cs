namespace SharpLink.Server;

/// <summary>Non-throwing expected-rejection paths for Server runtime configuration publication.</summary>
/// <remarks>
/// Lifecycle, mode, and publication conflicts are returned as structured results. Invalid arguments,
/// application callbacks, cancellation, internal invariants, and fatal runtime failures remain exceptions.
/// Existing throwing runtime-control APIs remain available for compatibility.
/// </remarks>
public static class SharpLinkServerRuntimeConfigurationExtensions
{
    /// <summary>Attempts to replace the server interceptor generation.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryReplaceInterceptors(
        this ISharpLinkServer server,
        IEnumerable<ISharpLinkServerInterceptor> interceptors)
        => GetRuntime(server) is { } runtime
            ? runtime.TryReplaceInterceptorsCore(interceptors)
            : Unsupported(nameof(TryReplaceInterceptors));

    /// <summary>Attempts to publish the response-compression send policy.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateResponseCompressionPolicy(
        this ISharpLinkServer server,
        SharpLinkCompressionSendPolicy policy)
        => GetRuntime(server) is { } runtime
            ? runtime.TryUpdateResponseCompressionPolicyCore(policy)
            : Unsupported(nameof(TryUpdateResponseCompressionPolicy));

    /// <summary>Attempts to publish server call-capacity limits.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateCallCapacity(
        this ISharpLinkServer server,
        int maxConcurrentCallsPerConnection,
        int maxConcurrentCallsPerServer)
        => GetRuntime(server) is { } runtime
            ? runtime.TryUpdateCallCapacityCore(maxConcurrentCallsPerConnection, maxConcurrentCallsPerServer)
            : Unsupported(nameof(TryUpdateCallCapacity));

    /// <summary>Attempts to publish pre-session connection-admission targets.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateConnectionAdmission(
        this ISharpLinkServer server,
        Action<SharpLinkConnectionAdmissionOptions> configure)
        => GetRuntime(server) is { } runtime
            ? runtime.TryUpdateConnectionAdmissionCore(configure)
            : Unsupported(nameof(TryUpdateConnectionAdmission));

    /// <summary>Attempts to enable admission control.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryEnableAdmissionControl(
        this ISharpLinkServer server,
        Action<SharpLinkAdmissionControlOptions> configure)
        => GetRuntime(server) is { } runtime
            ? runtime.TryEnableAdmissionControlCore(configure)
            : Unsupported(nameof(TryEnableAdmissionControl));

    /// <summary>Attempts to replace the enabled admission-control generation.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateAdmissionControl(
        this ISharpLinkServer server,
        Action<SharpLinkAdmissionControlOptions> configure)
        => GetRuntime(server) is { } runtime
            ? runtime.TryUpdateAdmissionControlCore(configure)
            : Unsupported(nameof(TryUpdateAdmissionControl));

    /// <summary>Attempts to disable admission control.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryDisableAdmissionControl(this ISharpLinkServer server)
        => GetRuntime(server) is { } runtime
            ? runtime.TryDisableAdmissionControlCore()
            : Unsupported(nameof(TryDisableAdmissionControl));

    /// <summary>Attempts to publish the server telemetry detail policy.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateTelemetryDetailPolicy(
        this ISharpLinkServer server,
        SharpLinkTelemetryDetailMode mode)
        => GetRuntime(server) is { } runtime
            ? runtime.TryUpdateTelemetryDetailPolicyCore(mode)
            : Unsupported(nameof(TryUpdateTelemetryDetailPolicy));

    /// <summary>
    /// Attempts to publish the immutable desired configuration captured by future sessions and,
    /// when requested, waits for the server-owned rolling-refresh scan.
    /// </summary>
    /// <remarks>
    /// Lifecycle closure and unsupported custom implementations are structured outcomes. Invalid
    /// configuration, caller cancellation, generation exhaustion, and internal invariants remain exceptions.
    /// Caller cancellation stops only this wait; it does not cancel an already-started server-owned rollout.
    /// </remarks>
    public static ValueTask<SharpLinkServerDesiredSessionPublicationResult> TryPublishDesiredSessionAsync(
        this ISharpLinkServer server,
        SharpLinkServerDesiredSessionConfiguration configuration,
        SharpLinkSessionRolloutMode rolloutMode = SharpLinkSessionRolloutMode.FutureOnly,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (rolloutMode is not SharpLinkSessionRolloutMode.FutureOnly and
            not SharpLinkSessionRolloutMode.RollingRefresh)
        {
            throw new ArgumentOutOfRangeException(nameof(rolloutMode));
        }
        if (configuration.MaxFramePayloadBytes is < SharpLinkProtocolOptions.MinMaxFramePayloadBytes or
            > SharpLinkProtocolOptions.MaxMaxFramePayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(configuration));
        }

        return GetRuntime(server) is { } runtime
            ? runtime.TryPublishDesiredSessionCoreAsync(configuration, rolloutMode, cancellationToken)
            : ValueTask.FromResult(SharpLinkServerDesiredSessionPublicationResult.Failure(
                SharpLinkRuntimeConfigurationUpdateFailureCode.UnsupportedByImplementation,
                "This ISharpLinkServer implementation does not expose structured desired-session publication."));
    }

    private static SharpLinkServer? GetRuntime(ISharpLinkServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server as SharpLinkServer;
    }

    private static SharpLinkRuntimeConfigurationUpdateResult Unsupported(string operation)
        => SharpLinkRuntimeConfigurationUpdateResult.Failure(
            SharpLinkRuntimeConfigurationUpdateFailureCode.UnsupportedByImplementation,
            $"This ISharpLinkServer implementation does not expose the structured runtime configuration operation '{operation}'.");
}
