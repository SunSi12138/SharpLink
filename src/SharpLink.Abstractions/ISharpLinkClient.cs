namespace SharpLink.Abstractions;

/// <summary>Owns SharpLink client connections and generated contract proxies.</summary>
public interface ISharpLinkClient : ISharpLinkAssemblyRegistry, IAsyncDisposable
{
    /// <summary>Gets the current atomic client lifecycle state.</summary>
    SharpLinkConnectionState State { get; }

    /// <summary>
    /// Gets an immutable point-in-time observation of the active endpoint topology without waiting,
    /// locking, or traversing endpoint collections.
    /// </summary>
    /// <returns>The latest published topology readiness snapshot.</returns>
    /// <exception cref="NotSupportedException">
    /// This implementation does not expose endpoint readiness details.
    /// </exception>
    SharpLinkClientReadinessSnapshot GetReadinessSnapshot()
        => throw new NotSupportedException(
            "This ISharpLinkClient implementation does not expose endpoint readiness details.");

    /// <summary>
    /// Starts or joins the topology's existing connectivity lifecycle when necessary, then waits
    /// until a point-in-time Ready-state observation contains at least the requested number of ready
    /// endpoints. A successful result is not a lease or a guarantee that topology readiness will be
    /// retained after the method returns. The wait does not raise the configured convergence target.
    /// </summary>
    /// <param name="minimumReadyEndpoints">The minimum number of active ready endpoints to observe.</param>
    /// <param name="cancellationToken">Cancels only this caller's wait.</param>
    /// <returns>The snapshot that satisfied the requested threshold.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="minimumReadyEndpoints"/> is less than one or exceeds this topology's configured
    /// readiness limit.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// This implementation does not support endpoint readiness waits.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was canceled. The Client-owned connectivity lifecycle continues.
    /// </exception>
    /// <exception cref="SharpLinkException">
    /// The joined initial connectivity attempt failed, the observed attempt entered Faulted, or the
    /// Client began draining or stopped.
    /// </exception>
    ValueTask<SharpLinkClientReadinessSnapshot> WaitForReadinessAsync(
        int minimumReadyEndpoints,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumReadyEndpoints, 1);
        return ValueTask.FromException<SharpLinkClientReadinessSnapshot>(
            new NotSupportedException(
                "This ISharpLinkClient implementation does not support endpoint readiness waits."));
    }

    /// <summary>
    /// Atomically replaces the client interceptor pipeline for logical RPCs that start after this call returns.
    /// Calls already in progress retain the interceptor generation captured at their invocation boundary.
    /// </summary>
    /// <param name="interceptors">The complete interceptor pipeline in execution order. The sequence is copied before publication.</param>
    /// <exception cref="ArgumentNullException"><paramref name="interceptors"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="interceptors"/> contains a null element.</exception>
    /// <exception cref="InvalidOperationException">The client is draining, stopped, or faulted.</exception>
    /// <exception cref="NotSupportedException">This implementation does not support runtime interceptor replacement.</exception>
    void ReplaceInterceptors(IEnumerable<ISharpLinkClientInterceptor> interceptors)
    {
        ArgumentNullException.ThrowIfNull(interceptors);
        throw new NotSupportedException(
            "This ISharpLinkClient implementation does not support runtime interceptor replacement.");
    }

    /// <summary>
    /// Gets the currently published client-wide request-timeout fallback generation.
    /// Method-level timeout policy and inherited deadlines can still impose a different effective call lifetime.
    /// </summary>
    SharpLinkRequestTimeoutPolicySnapshot GetRequestTimeoutPolicySnapshot()
        => throw new NotSupportedException(
            "This ISharpLinkClient implementation does not expose runtime request-timeout policy state.");

    /// <summary>
    /// Atomically publishes a custom client-wide request-timeout fallback for future logical RPCs.
    /// A logical RPC that already captured an earlier generation keeps its frozen deadline across
    /// interceptor suspension, retry attempts, and streaming lifetime.
    /// </summary>
    /// <param name="timeout">The positive timeout to publish.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException">The client is draining, stopped, or faulted.</exception>
    /// <exception cref="NotSupportedException">This implementation does not support runtime request-timeout updates.</exception>
    void UpdateRequestTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        throw new NotSupportedException(
            "This ISharpLinkClient implementation does not support runtime request-timeout updates.");
    }

    /// <summary>
    /// Atomically disables the client-wide request-timeout fallback for future logical RPCs.
    /// Calls that already captured a timeout generation keep their existing deadline.
    /// </summary>
    /// <exception cref="InvalidOperationException">The client is draining, stopped, or faulted.</exception>
    /// <exception cref="NotSupportedException">This implementation does not support runtime request-timeout updates.</exception>
    void DisableRequestTimeout()
        => throw new NotSupportedException(
            "This ISharpLinkClient implementation does not support runtime request-timeout updates.");

    /// <summary>Gets the currently published retry-policy generation.</summary>
    SharpLinkRetryPolicySnapshot GetRetryPolicySnapshot()
        => throw new NotSupportedException(
            "This ISharpLinkClient implementation does not expose runtime retry policy state.");

    /// <summary>
    /// Atomically publishes a built-in retry-policy generation for future logical RPCs.
    /// The supplied values are copied and validated before publication.
    /// </summary>
    /// <param name="options">The complete bounded built-in retry settings.</param>
    void UpdateRetryPolicy(ISharpLinkRetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        throw new NotSupportedException(
            "This ISharpLinkClient implementation does not support runtime retry policy updates.");
    }

    /// <summary>
    /// Atomically publishes a custom retry-policy generation for future logical RPCs using the same
    /// default attempt bounds as <c>UseRetry(ISharpLinkRetryPolicy)</c>.
    /// </summary>
    /// <param name="policy">The application-owned synchronous retry decision policy.</param>
    void UpdateRetryPolicy(ISharpLinkRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        throw new NotSupportedException(
            "This ISharpLinkClient implementation does not support runtime retry policy updates.");
    }

    /// <summary>
    /// Atomically publishes a custom retry-policy generation and explicit bounded attempt settings
    /// for future logical RPCs. The options are copied before publication.
    /// </summary>
    /// <param name="policy">The application-owned synchronous retry decision policy.</param>
    /// <param name="limits">The complete bounded attempt settings captured with the custom policy.</param>
    void UpdateRetryPolicy(ISharpLinkRetryPolicy policy, ISharpLinkRetryOptions limits)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(limits);
        throw new NotSupportedException(
            "This ISharpLinkClient implementation does not support runtime retry policy updates.");
    }

    /// <summary>
    /// Atomically disables retries for future logical RPCs. Calls already in progress retain their
    /// captured retry generation through interceptor suspension, backoff and subsequent attempts.
    /// </summary>
    void DisableRetry()
        => throw new NotSupportedException(
            "This ISharpLinkClient implementation does not support runtime retry policy updates.");

    /// <summary>Gets the currently published endpoint-admission generation and mode.</summary>
    SharpLinkEndpointAdmissionPolicySnapshot GetEndpointAdmissionPolicySnapshot()
        => throw new NotSupportedException(
            "This ISharpLinkClient implementation does not expose runtime endpoint admission policy state.");

    /// <summary>
    /// Atomically publishes an application-owned endpoint admission policy for future endpoint attempts.
    /// An attempt that has already been admitted remains permanently paired with the exact policy and
    /// opaque token that admitted it until its terminal report completes.
    /// </summary>
    /// <param name="policy">The application-owned synchronous endpoint admission policy.</param>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="policy"/> is the built-in circuit-breaker implementation.</exception>
    /// <exception cref="InvalidOperationException">The built-in circuit breaker is active, or the client is draining, stopped, or faulted.</exception>
    /// <exception cref="NotSupportedException">This implementation does not support runtime endpoint admission updates.</exception>
    void UpdateEndpointAdmissionPolicy(ISharpLinkEndpointAdmissionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        throw new NotSupportedException(
            "This ISharpLinkClient implementation does not support runtime endpoint admission updates.");
    }

    /// <summary>
    /// Disables the application-owned endpoint admission policy for future endpoint attempts.
    /// Already admitted attempts retain their exact policy/token lease through terminal reporting.
    /// </summary>
    /// <exception cref="InvalidOperationException">The built-in circuit breaker is active, or the client is draining, stopped, or faulted.</exception>
    /// <exception cref="NotSupportedException">This implementation does not support runtime endpoint admission updates.</exception>
    void DisableEndpointAdmissionPolicy()
        => throw new NotSupportedException(
            "This ISharpLinkClient implementation does not support runtime endpoint admission updates.");

    /// <summary>Gets the currently published built-in endpoint circuit-breaker configuration.</summary>
    SharpLinkCircuitBreakerPolicySnapshot GetCircuitBreakerPolicySnapshot()
        => throw new NotSupportedException(
            "This ISharpLinkClient implementation does not expose runtime circuit-breaker state.");

    /// <summary>
    /// Enables or atomically replaces the built-in endpoint-generation circuit-breaker settings.
    /// Ordinary option updates preserve live Closed/Open/HalfOpen state and retained sample history;
    /// enabling from disabled starts with a fresh Closed breaker.
    /// </summary>
    /// <param name="options">The complete circuit-breaker settings copied before publication.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">One or more option values are invalid.</exception>
    /// <exception cref="InvalidOperationException">Custom endpoint admission is active, or the client is draining, stopped, or faulted.</exception>
    /// <exception cref="NotSupportedException">This implementation does not support runtime circuit-breaker updates.</exception>
    void UpdateCircuitBreaker(ISharpLinkCircuitBreakerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        throw new NotSupportedException(
            "This ISharpLinkClient implementation does not support runtime circuit-breaker updates.");
    }

    /// <summary>
    /// Disables the built-in circuit breaker for future endpoint attempts. Re-enabling later creates
    /// fresh endpoint-generation breaker state rather than reviving retired Open/HalfOpen history.
    /// </summary>
    /// <exception cref="InvalidOperationException">Custom endpoint admission is active, or the client is draining, stopped, or faulted.</exception>
    /// <exception cref="NotSupportedException">This implementation does not support runtime circuit-breaker updates.</exception>
    void DisableCircuitBreaker()
        => throw new NotSupportedException(
            "This ISharpLinkClient implementation does not support runtime circuit-breaker updates.");

    /// <summary>
    /// Atomically replaces the client-local Request compression policy. The next Request or
    /// client-to-server StreamData frame captures the new policy at its compression decision point.
    /// </summary>
    /// <param name="policy">The complete replacement policy.</param>
    void UpdateRequestCompressionPolicy(SharpLinkCompressionSendPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        throw new NotSupportedException(
            "This ISharpLinkClient implementation does not support runtime request compression policy updates.");
    }

    /// <summary>
    /// Publishes the desired Server-to-Client response compression preference and waits for the
    /// fixed cohort of currently eligible Ready sessions to converge to at least that generation.
    /// </summary>
    /// <param name="allowResponseCompression">Whether response-direction compression is allowed.</param>
    /// <param name="cancellationToken">Cancels only this caller's convergence wait; the desired state remains published.</param>
    ValueTask SetResponseCompressionPreferenceAsync(
        bool allowResponseCompression,
        CancellationToken cancellationToken = default)
        => ValueTask.FromException(new NotSupportedException(
            "This ISharpLinkClient implementation does not support response compression preference updates."));

    /// <summary>
    /// Starts the topology-specific connectivity lifecycle and completes according to its existing
    /// connectivity boundary. This method does not wait for multi-endpoint convergence.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancels only this caller's wait; the shared client-owned connection attempt continues.
    /// </param>
    /// <exception cref="SharpLinkException">The transport or handshake failed.</exception>
    ValueTask ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops reconnecting, fails pending work, and releases all owned resources.</summary>
    /// <param name="cancellationToken">Cancels only this caller's wait for the shared stop operation.</param>
    ValueTask StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Queries the selected ready connection using the protocol health control frame.</summary>
    /// <param name="cancellationToken">Cancels the local health request.</param>
    /// <returns>The remote server readiness state.</returns>
    ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Creates the generated proxy for a registered RPC contract.</summary>
    /// <typeparam name="TContract">The generated RPC contract interface.</typeparam>
    TContract Get<TContract>() where TContract : IService;

    /// <summary>Creates a generated proxy that attaches one immutable metadata snapshot to every invocation.</summary>
    /// <typeparam name="TContract">The generated RPC contract interface.</typeparam>
    /// <param name="metadata">Envelope metadata attached without adding a business-contract parameter.</param>
    TContract GetWithMetadata<TContract>(SharpLinkMetadata metadata) where TContract : IService;
}
