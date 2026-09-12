namespace SharpLink.Client;

/// <summary>Non-throwing expected-rejection paths for Client runtime configuration publication.</summary>
/// <remarks>
/// These methods return a structured result for lifecycle and mode rejections. Invalid arguments,
/// cancellation, generation exhaustion, internal invariant failures, and fatal runtime failures remain exceptions.
/// Existing throwing members on <see cref="ISharpLinkClient"/> remain available for compatibility.
/// </remarks>
public static class SharpLinkClientRuntimeConfigurationExtensions
{
    /// <summary>Attempts to replace the runtime interceptor generation.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryReplaceInterceptors(
        this ISharpLinkClient client,
        IEnumerable<ISharpLinkClientInterceptor> interceptors)
        => GetRuntime(client) is { } runtime
            ? runtime.TryReplaceInterceptorsCore(interceptors)
            : Unsupported(nameof(TryReplaceInterceptors));

    /// <summary>Attempts to publish a custom request timeout.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateRequestTimeout(
        this ISharpLinkClient client,
        TimeSpan timeout)
        => GetRuntime(client) is { } runtime
            ? runtime.TryUpdateRequestTimeoutCore(timeout)
            : Unsupported(nameof(TryUpdateRequestTimeout));

    /// <summary>Attempts to disable the published request-timeout policy.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryDisableRequestTimeout(this ISharpLinkClient client)
        => GetRuntime(client) is { } runtime
            ? runtime.TryDisableRequestTimeoutCore()
            : Unsupported(nameof(TryDisableRequestTimeout));

    /// <summary>Attempts to publish the built-in retry policy.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateRetryPolicy(
        this ISharpLinkClient client,
        ISharpLinkRetryOptions options)
        => GetRuntime(client) is { } runtime
            ? runtime.TryUpdateRetryPolicyCore(options)
            : Unsupported(nameof(TryUpdateRetryPolicy));

    /// <summary>Attempts to publish a custom retry policy with default limits.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateRetryPolicy(
        this ISharpLinkClient client,
        ISharpLinkRetryPolicy policy)
        => GetRuntime(client) is { } runtime
            ? runtime.TryUpdateRetryPolicyCore(policy)
            : Unsupported(nameof(TryUpdateRetryPolicy));

    /// <summary>Attempts to publish a custom retry policy with explicit limits.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateRetryPolicy(
        this ISharpLinkClient client,
        ISharpLinkRetryPolicy policy,
        ISharpLinkRetryOptions limits)
        => GetRuntime(client) is { } runtime
            ? runtime.TryUpdateRetryPolicyCore(policy, limits)
            : Unsupported(nameof(TryUpdateRetryPolicy));

    /// <summary>Attempts to disable retry publication.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryDisableRetry(this ISharpLinkClient client)
        => GetRuntime(client) is { } runtime
            ? runtime.TryDisableRetryCore()
            : Unsupported(nameof(TryDisableRetry));

    /// <summary>Attempts to publish the complete heartbeat configuration.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateHeartbeat(
        this ISharpLinkClient client,
        TimeSpan interval,
        TimeSpan timeout)
        => GetRuntime(client) is { } runtime
            ? runtime.TryUpdateHeartbeatCore(interval, timeout)
            : Unsupported(nameof(TryUpdateHeartbeat));

    /// <summary>Attempts to publish a new heartbeat interval while retaining the current timeout.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateHeartbeatInterval(
        this ISharpLinkClient client,
        TimeSpan interval)
        => GetRuntime(client) is { } runtime
            ? runtime.TryUpdateHeartbeatIntervalCore(interval)
            : Unsupported(nameof(TryUpdateHeartbeatInterval));

    /// <summary>Attempts to publish a new heartbeat timeout while retaining the current interval.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateHeartbeatTimeout(
        this ISharpLinkClient client,
        TimeSpan timeout)
        => GetRuntime(client) is { } runtime
            ? runtime.TryUpdateHeartbeatTimeoutCore(timeout)
            : Unsupported(nameof(TryUpdateHeartbeatTimeout));

    /// <summary>Attempts to publish the reconnect policy.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateReconnectPolicy(
        this ISharpLinkClient client,
        SharpLinkReconnectPolicy policy)
        => GetRuntime(client) is { } runtime
            ? runtime.TryUpdateReconnectPolicyCore(policy)
            : Unsupported(nameof(TryUpdateReconnectPolicy));

    /// <summary>Attempts to publish a custom endpoint-admission policy.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateEndpointAdmissionPolicy(
        this ISharpLinkClient client,
        ISharpLinkEndpointAdmissionPolicy policy)
        => GetRuntime(client) is { } runtime
            ? runtime.TryUpdateEndpointAdmissionPolicyCore(policy)
            : Unsupported(nameof(TryUpdateEndpointAdmissionPolicy));

    /// <summary>Attempts to disable custom endpoint admission.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryDisableEndpointAdmissionPolicy(this ISharpLinkClient client)
        => GetRuntime(client) is { } runtime
            ? runtime.TryDisableEndpointAdmissionPolicyCore()
            : Unsupported(nameof(TryDisableEndpointAdmissionPolicy));

    /// <summary>Attempts to publish the built-in circuit-breaker configuration.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateCircuitBreaker(
        this ISharpLinkClient client,
        ISharpLinkCircuitBreakerOptions options)
        => GetRuntime(client) is { } runtime
            ? runtime.TryUpdateCircuitBreakerCore(options)
            : Unsupported(nameof(TryUpdateCircuitBreaker));

    /// <summary>Attempts to disable the built-in circuit breaker.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryDisableCircuitBreaker(this ISharpLinkClient client)
        => GetRuntime(client) is { } runtime
            ? runtime.TryDisableCircuitBreakerCore()
            : Unsupported(nameof(TryDisableCircuitBreaker));

    /// <summary>Attempts to publish the request-compression send policy.</summary>
    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateRequestCompressionPolicy(
        this ISharpLinkClient client,
        SharpLinkCompressionSendPolicy policy)
        => GetRuntime(client) is { } runtime
            ? runtime.TryUpdateRequestCompressionPolicyCore(policy)
            : Unsupported(nameof(TryUpdateRequestCompressionPolicy));

    /// <summary>Attempts to publish and reconcile the response-compression preference.</summary>
    public static ValueTask<SharpLinkRuntimeConfigurationUpdateResult> TrySetResponseCompressionPreferenceAsync(
        this ISharpLinkClient client,
        bool allowResponseCompression,
        CancellationToken cancellationToken = default)
    {
        var runtime = GetRuntime(client);
        return runtime is null
            ? ValueTask.FromResult(Unsupported(nameof(TrySetResponseCompressionPreferenceAsync)))
            : runtime.TrySetResponseCompressionPreferenceCoreAsync(allowResponseCompression, cancellationToken);
    }

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
