namespace SharpLink.Client;

/// <summary>Non-throwing expected-rejection paths for Client runtime configuration publication.</summary>
/// <remarks>
/// These methods return a structured result for lifecycle and mode rejections. Invalid arguments,
/// cancellation, generation exhaustion, internal invariant failures, and fatal runtime failures remain exceptions.
/// Existing throwing members on <see cref="ISharpLinkClient"/> remain available for compatibility.
/// </remarks>
public static class SharpLinkClientRuntimeConfigurationExtensions
{
    public static SharpLinkRuntimeConfigurationUpdateResult TryReplaceInterceptors(
        this ISharpLinkClient client,
        IEnumerable<ISharpLinkClientInterceptor> interceptors)
        => GetRuntime(client, nameof(TryReplaceInterceptors)) is { } runtime
            ? runtime.TryReplaceInterceptorsCore(interceptors)
            : Unsupported(nameof(TryReplaceInterceptors));

    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateRequestTimeout(
        this ISharpLinkClient client,
        TimeSpan timeout)
        => GetRuntime(client, nameof(TryUpdateRequestTimeout)) is { } runtime
            ? runtime.TryUpdateRequestTimeoutCore(timeout)
            : Unsupported(nameof(TryUpdateRequestTimeout));

    public static SharpLinkRuntimeConfigurationUpdateResult TryDisableRequestTimeout(this ISharpLinkClient client)
        => GetRuntime(client, nameof(TryDisableRequestTimeout)) is { } runtime
            ? runtime.TryDisableRequestTimeoutCore()
            : Unsupported(nameof(TryDisableRequestTimeout));

    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateRetryPolicy(
        this ISharpLinkClient client,
        ISharpLinkRetryOptions options)
        => GetRuntime(client, nameof(TryUpdateRetryPolicy)) is { } runtime
            ? runtime.TryUpdateRetryPolicyCore(options)
            : Unsupported(nameof(TryUpdateRetryPolicy));

    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateRetryPolicy(
        this ISharpLinkClient client,
        ISharpLinkRetryPolicy policy)
        => GetRuntime(client, nameof(TryUpdateRetryPolicy)) is { } runtime
            ? runtime.TryUpdateRetryPolicyCore(policy)
            : Unsupported(nameof(TryUpdateRetryPolicy));

    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateRetryPolicy(
        this ISharpLinkClient client,
        ISharpLinkRetryPolicy policy,
        ISharpLinkRetryOptions limits)
        => GetRuntime(client, nameof(TryUpdateRetryPolicy)) is { } runtime
            ? runtime.TryUpdateRetryPolicyCore(policy, limits)
            : Unsupported(nameof(TryUpdateRetryPolicy));

    public static SharpLinkRuntimeConfigurationUpdateResult TryDisableRetry(this ISharpLinkClient client)
        => GetRuntime(client, nameof(TryDisableRetry)) is { } runtime
            ? runtime.TryDisableRetryCore()
            : Unsupported(nameof(TryDisableRetry));

    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateHeartbeat(
        this ISharpLinkClient client,
        TimeSpan interval,
        TimeSpan timeout)
        => GetRuntime(client, nameof(TryUpdateHeartbeat)) is { } runtime
            ? runtime.TryUpdateHeartbeatCore(interval, timeout)
            : Unsupported(nameof(TryUpdateHeartbeat));

    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateHeartbeatInterval(
        this ISharpLinkClient client,
        TimeSpan interval)
        => GetRuntime(client, nameof(TryUpdateHeartbeatInterval)) is { } runtime
            ? runtime.TryUpdateHeartbeatIntervalCore(interval)
            : Unsupported(nameof(TryUpdateHeartbeatInterval));

    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateHeartbeatTimeout(
        this ISharpLinkClient client,
        TimeSpan timeout)
        => GetRuntime(client, nameof(TryUpdateHeartbeatTimeout)) is { } runtime
            ? runtime.TryUpdateHeartbeatTimeoutCore(timeout)
            : Unsupported(nameof(TryUpdateHeartbeatTimeout));

    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateReconnectPolicy(
        this ISharpLinkClient client,
        SharpLinkReconnectPolicy policy)
        => GetRuntime(client, nameof(TryUpdateReconnectPolicy)) is { } runtime
            ? runtime.TryUpdateReconnectPolicyCore(policy)
            : Unsupported(nameof(TryUpdateReconnectPolicy));

    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateEndpointAdmissionPolicy(
        this ISharpLinkClient client,
        ISharpLinkEndpointAdmissionPolicy policy)
        => GetRuntime(client, nameof(TryUpdateEndpointAdmissionPolicy)) is { } runtime
            ? runtime.TryUpdateEndpointAdmissionPolicyCore(policy)
            : Unsupported(nameof(TryUpdateEndpointAdmissionPolicy));

    public static SharpLinkRuntimeConfigurationUpdateResult TryDisableEndpointAdmissionPolicy(this ISharpLinkClient client)
        => GetRuntime(client, nameof(TryDisableEndpointAdmissionPolicy)) is { } runtime
            ? runtime.TryDisableEndpointAdmissionPolicyCore()
            : Unsupported(nameof(TryDisableEndpointAdmissionPolicy));

    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateCircuitBreaker(
        this ISharpLinkClient client,
        ISharpLinkCircuitBreakerOptions options)
        => GetRuntime(client, nameof(TryUpdateCircuitBreaker)) is { } runtime
            ? runtime.TryUpdateCircuitBreakerCore(options)
            : Unsupported(nameof(TryUpdateCircuitBreaker));

    public static SharpLinkRuntimeConfigurationUpdateResult TryDisableCircuitBreaker(this ISharpLinkClient client)
        => GetRuntime(client, nameof(TryDisableCircuitBreaker)) is { } runtime
            ? runtime.TryDisableCircuitBreakerCore()
            : Unsupported(nameof(TryDisableCircuitBreaker));

    public static SharpLinkRuntimeConfigurationUpdateResult TryUpdateRequestCompressionPolicy(
        this ISharpLinkClient client,
        SharpLinkCompressionSendPolicy policy)
        => GetRuntime(client, nameof(TryUpdateRequestCompressionPolicy)) is { } runtime
            ? runtime.TryUpdateRequestCompressionPolicyCore(policy)
            : Unsupported(nameof(TryUpdateRequestCompressionPolicy));

    public static ValueTask<SharpLinkRuntimeConfigurationUpdateResult> TrySetResponseCompressionPreferenceAsync(
        this ISharpLinkClient client,
        bool allowResponseCompression,
        CancellationToken cancellationToken = default)
    {
        var runtime = GetRuntime(client, nameof(TrySetResponseCompressionPreferenceAsync));
        return runtime is null
            ? ValueTask.FromResult(Unsupported(nameof(TrySetResponseCompressionPreferenceAsync)))
            : runtime.TrySetResponseCompressionPreferenceCoreAsync(allowResponseCompression, cancellationToken);
    }

    private static SharpLinkClient? GetRuntime(ISharpLinkClient client, string operation)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client as SharpLinkClient;
    }

    private static SharpLinkRuntimeConfigurationUpdateResult Unsupported(string operation)
        => SharpLinkRuntimeConfigurationUpdateResult.Failure(
            SharpLinkRuntimeConfigurationUpdateFailureCode.UnsupportedByImplementation,
            $"This ISharpLinkClient implementation does not expose the structured runtime configuration operation '{operation}'.");
}
