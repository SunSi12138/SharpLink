namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    internal SharpLinkRuntimeConfigurationUpdateResult TryReplaceInterceptorsCore(
        IEnumerable<ISharpLinkClientInterceptor> interceptors)
    {
        var candidate = ClientInterceptorGeneration.Create(CreateInterceptorSnapshot(interceptors));
        lock (_stateGate)
        {
            Volatile.Read(ref _replacementStateGateEnteredForTesting)?.Invoke();
            lock (_readinessGate)
            {
                var state = State;
                if (IsRuntimeConfigurationPublicationClosed(state))
                    return LifecycleClosed($"Client state '{state}' does not accept runtime interceptor replacement.");

                Volatile.Write(ref _clientInterceptorGeneration, candidate);
                return SharpLinkRuntimeConfigurationUpdateResult.Success();
            }
        }
    }

    internal SharpLinkRuntimeConfigurationUpdateResult TryUpdateRequestTimeoutCore(TimeSpan timeout)
        => TryPublishRequestTimeoutPolicy(ClientRequestTimeoutPolicy.Custom(timeout));

    internal SharpLinkRuntimeConfigurationUpdateResult TryDisableRequestTimeoutCore()
        => TryPublishRequestTimeoutPolicy(ClientRequestTimeoutPolicy.Disabled);

    private SharpLinkRuntimeConfigurationUpdateResult TryPublishRequestTimeoutPolicy(
        ClientRequestTimeoutPolicy policy)
    {
        lock (_stateGate)
        {
            var state = State;
            if (IsRuntimeConfigurationPublicationClosed(state))
                return LifecycleClosed($"Request-timeout policy cannot be updated while the client is {state}.");

            var current = CaptureRequestTimeoutGeneration();
            if (current.Policy == policy)
                return SharpLinkRuntimeConfigurationUpdateResult.Success();
            if (current.Generation == ulong.MaxValue)
                throw new InvalidOperationException("Request-timeout policy generation is exhausted.");

            Volatile.Write(
                ref _requestTimeoutGeneration,
                new ClientRequestTimeoutGeneration(current.Generation + 1, policy));
            return SharpLinkRuntimeConfigurationUpdateResult.Success();
        }
    }

    internal SharpLinkRuntimeConfigurationUpdateResult TryUpdateRetryPolicyCore(ISharpLinkRetryOptions options)
    {
        var settings = ClientRetrySettings.CopyValidated(options);
        return TryPublishRetryGeneration(SharpLinkRetryPolicyKind.BuiltIn, settings, policy: null);
    }

    internal SharpLinkRuntimeConfigurationUpdateResult TryUpdateRetryPolicyCore(ISharpLinkRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return TryPublishRetryGeneration(
            SharpLinkRetryPolicyKind.Custom,
            ClientRetrySettings.Default,
            policy);
    }

    internal SharpLinkRuntimeConfigurationUpdateResult TryUpdateRetryPolicyCore(
        ISharpLinkRetryPolicy policy,
        ISharpLinkRetryOptions limits)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var settings = ClientRetrySettings.CopyValidated(limits);
        return TryPublishRetryGeneration(SharpLinkRetryPolicyKind.Custom, settings, policy);
    }

    internal SharpLinkRuntimeConfigurationUpdateResult TryDisableRetryCore()
        => TryPublishRetryGeneration(SharpLinkRetryPolicyKind.Disabled, default, policy: null);

    private SharpLinkRuntimeConfigurationUpdateResult TryPublishRetryGeneration(
        SharpLinkRetryPolicyKind kind,
        ClientRetrySettings settings,
        ISharpLinkRetryPolicy? policy)
    {
        lock (_stateGate)
        {
            var state = State;
            if (IsRuntimeConfigurationPublicationClosed(state))
                return LifecycleClosed($"Retry policy cannot be updated while the client is {state}.");

            var current = CaptureRetryGeneration();
            if (current.Kind == kind && current.Settings == settings && ReferenceEquals(current.Policy, policy))
                return SharpLinkRuntimeConfigurationUpdateResult.Success();
            if (current.Generation == ulong.MaxValue)
                throw new InvalidOperationException("Retry policy generation is exhausted.");

            Volatile.Write(
                ref _retryGeneration,
                new ClientRetryGeneration(current.Generation + 1, kind, settings, policy));
            return SharpLinkRuntimeConfigurationUpdateResult.Success();
        }
    }

    internal SharpLinkRuntimeConfigurationUpdateResult TryUpdateHeartbeatCore(
        TimeSpan interval,
        TimeSpan timeout)
    {
        ValidateHeartbeatConfiguration(interval, timeout);
        HeartbeatConfigurationGeneration? previous;
        lock (_stateGate)
        {
            var state = State;
            if (IsRuntimeConfigurationPublicationClosed(state))
                return LifecycleClosed($"Client state '{state}' does not accept heartbeat configuration updates.");
            previous = PublishHeartbeatConfigurationLocked(interval, timeout);
        }
        previous?.SignalChanged();
        return SharpLinkRuntimeConfigurationUpdateResult.Success();
    }

    internal SharpLinkRuntimeConfigurationUpdateResult TryUpdateHeartbeatIntervalCore(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        HeartbeatConfigurationGeneration? previous;
        lock (_stateGate)
        {
            var state = State;
            if (IsRuntimeConfigurationPublicationClosed(state))
                return LifecycleClosed($"Client state '{state}' does not accept heartbeat configuration updates.");
            var current = CaptureHeartbeatConfiguration();
            ValidateHeartbeatConfiguration(interval, current.Timeout);
            previous = PublishHeartbeatConfigurationLocked(interval, current.Timeout);
        }
        previous?.SignalChanged();
        return SharpLinkRuntimeConfigurationUpdateResult.Success();
    }

    internal SharpLinkRuntimeConfigurationUpdateResult TryUpdateHeartbeatTimeoutCore(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        HeartbeatConfigurationGeneration? previous;
        lock (_stateGate)
        {
            var state = State;
            if (IsRuntimeConfigurationPublicationClosed(state))
                return LifecycleClosed($"Client state '{state}' does not accept heartbeat configuration updates.");
            var current = CaptureHeartbeatConfiguration();
            ValidateHeartbeatConfiguration(current.Interval, timeout);
            previous = PublishHeartbeatConfigurationLocked(current.Interval, timeout);
        }
        previous?.SignalChanged();
        return SharpLinkRuntimeConfigurationUpdateResult.Success();
    }

    internal SharpLinkRuntimeConfigurationUpdateResult TryUpdateReconnectPolicyCore(SharpLinkReconnectPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ReconnectPolicyGeneration? previous;
        lock (_stateGate)
        {
            var state = State;
            if (IsRuntimeConfigurationPublicationClosed(state))
                return LifecycleClosed($"Client state '{state}' does not accept reconnect policy updates.");
            var current = CaptureReconnectPolicy();
            if (current.Policy == policy)
                return SharpLinkRuntimeConfigurationUpdateResult.Success();
            if (current.Generation == ulong.MaxValue)
                throw new InvalidOperationException("The reconnect policy generation is exhausted.");

            var candidate = new ReconnectPolicyGeneration(current.Generation + 1, policy);
            current.SetSuccessor(candidate);
            Volatile.Write(ref _reconnectPolicyConfiguration, candidate);
            previous = current;
        }
        previous.SignalChanged();
        return SharpLinkRuntimeConfigurationUpdateResult.Success();
    }

    internal SharpLinkRuntimeConfigurationUpdateResult TryUpdateEndpointAdmissionPolicyCore(
        ISharpLinkEndpointAdmissionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy is SharpLinkCircuitBreaker)
        {
            throw new ArgumentException(
                "The built-in circuit breaker cannot be published through the custom endpoint admission API.",
                nameof(policy));
        }

        lock (_stateGate)
        {
            var state = State;
            if (IsRuntimeConfigurationPublicationClosed(state))
                return LifecycleClosed($"Client state '{state}' does not accept endpoint admission policy updates.");
            var current = _endpointAdmissionPolicy;
            if (current is SharpLinkCircuitBreaker)
            {
                return ModeConflict(
                    "Custom endpoint admission and the built-in circuit breaker are mutually exclusive. Disable the circuit breaker first.");
            }
            if (ReferenceEquals(current, policy))
                return SharpLinkRuntimeConfigurationUpdateResult.Success();
            if (_endpointAdmissionPolicyGeneration == ulong.MaxValue)
                throw new InvalidOperationException("The endpoint admission policy generation is exhausted.");

            _endpointAdmissionPolicyGeneration++;
            _endpointAdmissionPolicy = policy;
            return SharpLinkRuntimeConfigurationUpdateResult.Success();
        }
    }

    internal SharpLinkRuntimeConfigurationUpdateResult TryDisableEndpointAdmissionPolicyCore()
    {
        lock (_stateGate)
        {
            var state = State;
            if (IsRuntimeConfigurationPublicationClosed(state))
                return LifecycleClosed($"Client state '{state}' does not accept endpoint admission policy updates.");
            var current = _endpointAdmissionPolicy;
            if (current is SharpLinkCircuitBreaker)
            {
                return ModeConflict(
                    "The built-in circuit breaker must be disabled through the circuit-breaker runtime configuration API.");
            }
            if (current is null)
                return SharpLinkRuntimeConfigurationUpdateResult.Success();
            if (_endpointAdmissionPolicyGeneration == ulong.MaxValue)
                throw new InvalidOperationException("The endpoint admission policy generation is exhausted.");

            _endpointAdmissionPolicyGeneration++;
            _endpointAdmissionPolicy = null;
            return SharpLinkRuntimeConfigurationUpdateResult.Success();
        }
    }

    internal SharpLinkRuntimeConfigurationUpdateResult TryUpdateCircuitBreakerCore(
        ISharpLinkCircuitBreakerOptions options)
    {
        var candidate = SharpLinkCircuitBreakerOptions.CopyValidated(options);
        lock (_stateGate)
        {
            var state = State;
            if (IsRuntimeConfigurationPublicationClosed(state))
                return LifecycleClosed($"Client state '{state}' does not accept endpoint admission policy updates.");
            var current = _endpointAdmissionPolicy;
            if (current is not null && current is not SharpLinkCircuitBreaker)
            {
                return ModeConflict(
                    "The built-in circuit breaker and custom endpoint admission are mutually exclusive. Disable custom endpoint admission first.");
            }

            if (current is SharpLinkCircuitBreaker breaker)
            {
                var existing = breaker.CaptureConfiguration();
                if (Matches(existing, candidate))
                    return SharpLinkRuntimeConfigurationUpdateResult.Success();
                EnsureEndpointAdmissionGenerationAvailable();
                breaker.UpdateConfiguration(candidate);
                _endpointAdmissionPolicyGeneration++;
                return SharpLinkRuntimeConfigurationUpdateResult.Success();
            }

            EnsureEndpointAdmissionGenerationAvailable();
            _endpointAdmissionPolicy = new SharpLinkCircuitBreaker(candidate, _runtimeContext.TimeProvider);
            _endpointAdmissionPolicyGeneration++;
            return SharpLinkRuntimeConfigurationUpdateResult.Success();
        }
    }

    internal SharpLinkRuntimeConfigurationUpdateResult TryDisableCircuitBreakerCore()
    {
        lock (_stateGate)
        {
            var state = State;
            if (IsRuntimeConfigurationPublicationClosed(state))
                return LifecycleClosed($"Client state '{state}' does not accept endpoint admission policy updates.");
            var current = _endpointAdmissionPolicy;
            if (current is not null && current is not SharpLinkCircuitBreaker)
            {
                return ModeConflict(
                    "Custom endpoint admission is active; it must be disabled through the custom endpoint admission runtime API.");
            }
            if (current is null)
                return SharpLinkRuntimeConfigurationUpdateResult.Success();

            EnsureEndpointAdmissionGenerationAvailable();
            _endpointAdmissionPolicy = null;
            _endpointAdmissionPolicyGeneration++;
            return SharpLinkRuntimeConfigurationUpdateResult.Success();
        }
    }

    internal SharpLinkRuntimeConfigurationUpdateResult TryUpdateRequestCompressionPolicyCore(
        SharpLinkCompressionSendPolicy policy)
    {
        lock (_stateGate)
        {
            var state = State;
            if (IsRuntimeConfigurationPublicationClosed(state))
                return LifecycleClosed($"Client state '{state}' does not accept request compression policy updates.");
            _requestCompressionPolicy.Update(policy);
            return SharpLinkRuntimeConfigurationUpdateResult.Success();
        }
    }

    internal async ValueTask<SharpLinkRuntimeConfigurationUpdateResult> TrySetResponseCompressionPreferenceCoreAsync(
        bool allowResponseCompression,
        CancellationToken cancellationToken)
    {
        ResponseCompressionPreferenceSnapshot desired;
        lock (_stateGate)
        {
            var state = State;
            if (IsRuntimeConfigurationPublicationClosed(state))
                return LifecycleClosed($"Client state '{state}' does not accept response compression preference updates.");

            var current = Volatile.Read(ref _responseCompressionPreference);
            if (current.Allowed == allowResponseCompression)
            {
                desired = current;
            }
            else
            {
                if (current.Generation == ulong.MaxValue)
                    throw new InvalidOperationException("The response compression preference generation is exhausted.");
                desired = new ResponseCompressionPreferenceSnapshot(current.Generation + 1, allowResponseCompression);
                Volatile.Write(ref _responseCompressionPreference, desired);
            }
        }

        var cohort = CaptureResponseCompressionPreferenceCohort();
        await ApplyResponseCompressionPreferenceToCohortAsync(cohort, desired, cancellationToken).ConfigureAwait(false);
        return SharpLinkRuntimeConfigurationUpdateResult.Success();
    }

    private bool IsRuntimeConfigurationPublicationClosed(SharpLinkConnectionState state)
        => Volatile.Read(ref _stopStarted) != 0 ||
           state is SharpLinkConnectionState.Draining or
               SharpLinkConnectionState.Stopped or
               SharpLinkConnectionState.Faulted;

    private static SharpLinkRuntimeConfigurationUpdateResult LifecycleClosed(string message)
        => SharpLinkRuntimeConfigurationUpdateResult.Failure(
            SharpLinkRuntimeConfigurationUpdateFailureCode.LifecycleClosed,
            message);

    private static SharpLinkRuntimeConfigurationUpdateResult ModeConflict(string message)
        => SharpLinkRuntimeConfigurationUpdateResult.Failure(
            SharpLinkRuntimeConfigurationUpdateFailureCode.ModeConflict,
            message);
}
