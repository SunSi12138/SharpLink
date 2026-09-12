namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    internal SharpLinkRuntimeConfigurationUpdateResult TryReplaceInterceptorsCore(
        IEnumerable<ISharpLinkServerInterceptor> interceptors)
    {
        var candidate = ServerInterceptorGeneration.Create(CreateInterceptorSnapshot(interceptors));
        lock (_stateGate)
        {
            Volatile.Read(ref _replacementStateGateEnteredForTesting)?.Invoke();
            var state = CurrentState;
            if (_lifecycle.HasStopStarted || IsRuntimeConfigurationPublicationClosed(state))
                return LifecycleClosed($"Server state '{state}' does not accept runtime interceptor replacement.");

            Volatile.Write(ref _serverInterceptorGeneration, candidate);
            return SharpLinkRuntimeConfigurationUpdateResult.Success();
        }
    }

    internal SharpLinkRuntimeConfigurationUpdateResult TryUpdateResponseCompressionPolicyCore(
        SharpLinkCompressionSendPolicy policy)
    {
        lock (_stateGate)
        {
            var state = CurrentState;
            if (_lifecycle.HasStopStarted || IsRuntimeConfigurationPublicationClosed(state))
                return LifecycleClosed($"Server state '{state}' does not accept response compression policy updates.");
            _responseCompressionPolicy.Update(policy);
            return SharpLinkRuntimeConfigurationUpdateResult.Success();
        }
    }

    internal SharpLinkRuntimeConfigurationUpdateResult TryUpdateCallCapacityCore(
        int maxConcurrentCallsPerConnection,
        int maxConcurrentCallsPerServer)
    {
        var candidate = ServerCallCapacityLimits.CreateValidated(
            maxConcurrentCallsPerConnection,
            maxConcurrentCallsPerServer);

        lock (_stateGate)
        {
            var state = CurrentState;
            if (_lifecycle.HasStopStarted || IsRuntimeConfigurationPublicationClosed(state))
                return LifecycleClosed("Call-capacity publication is sealed because the server is stopping.");

            _callAdmission.UpdateLimits(candidate);
            return SharpLinkRuntimeConfigurationUpdateResult.Success();
        }
    }

    internal SharpLinkRuntimeConfigurationUpdateResult TryUpdateConnectionAdmissionCore(
        Action<SharpLinkConnectionAdmissionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new SharpLinkConnectionAdmissionOptions();
        configure(options);
        var candidate = options.CloneValidated();

        lock (_registryGate)
        {
            var state = CurrentState;
            if (IsRuntimeConfigurationPublicationClosed(state))
                return LifecycleClosed("Connection admission publication is sealed because the server is stopping.");

            _connectionAdmission.UpdateTargets(
                candidate.MaxConcurrentConnections,
                candidate.MaxConcurrentHandshakes);
            return SharpLinkRuntimeConfigurationUpdateResult.Success();
        }
    }

    internal SharpLinkRuntimeConfigurationUpdateResult TryEnableAdmissionControlCore(
        Action<SharpLinkAdmissionControlOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var candidate = CreateAdmissionProgram(configure);
        Volatile.Read(ref s_afterAdmissionCandidateBuiltForTests)?.Invoke(this, candidate);

        lock (_registryGate)
        {
            var state = CurrentState;
            if (IsRuntimeConfigurationPublicationClosed(state))
            {
                candidate.Retire();
                return LifecycleClosed("Admission publication is sealed because the server is stopping.");
            }

            if (ReadAdmissionPublication().IsEnabled)
            {
                candidate.Retire();
                return PublicationConflict("Admission control is already enabled.");
            }

            try
            {
                PublishAdmissionProgram(candidate, AdmissionPublicationIntent.Enable);
                return SharpLinkRuntimeConfigurationUpdateResult.Success();
            }
            catch
            {
                candidate.Retire();
                throw;
            }
        }
    }

    internal SharpLinkRuntimeConfigurationUpdateResult TryUpdateAdmissionControlCore(
        Action<SharpLinkAdmissionControlOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var source = ReadAdmissionPublication();
        if (!source.IsEnabled)
            return ModeConflict("Admission control must be enabled before it can be updated.");
        if (!source.TryAcquireUse())
        {
            if (_admissionController?.Kernel.IsDraining == true)
                return LifecycleClosed("Admission publication is sealed because the server is stopping.");
            throw new InvalidOperationException("The current admission publication could not be acquired.");
        }

        AdmissionProgram? candidate = null;
        try
        {
            candidate = CreateAdmissionUpdateProgram(source, configure, out var updatePlan);
            Volatile.Read(ref s_afterAdmissionCandidateBuiltForTests)?.Invoke(this, candidate);

            lock (_registryGate)
            {
                var state = CurrentState;
                if (IsRuntimeConfigurationPublicationClosed(state))
                {
                    candidate.Retire();
                    return LifecycleClosed("Admission publication is sealed because the server is stopping.");
                }

                var current = ReadAdmissionPublication();
                if (!current.IsEnabled)
                {
                    candidate.Retire();
                    return ModeConflict("Admission control was disabled while the update candidate was being prepared.");
                }
                if (!ReferenceEquals(current, source))
                {
                    candidate.Retire();
                    return PublicationConflict("Admission control changed while the update candidate was being prepared.");
                }

                PublishAdmissionProgram(
                    candidate,
                    AdmissionPublicationIntent.Update,
                    expectedSource: source,
                    updatePlan);
                return SharpLinkRuntimeConfigurationUpdateResult.Success();
            }
        }
        catch
        {
            candidate?.Retire();
            throw;
        }
        finally
        {
            source.ReleaseUse();
        }
    }

    internal SharpLinkRuntimeConfigurationUpdateResult TryDisableAdmissionControlCore()
    {
        lock (_registryGate)
        {
            var state = CurrentState;
            if (IsRuntimeConfigurationPublicationClosed(state))
                return LifecycleClosed("Admission publication is sealed because the server is stopping.");
            if (!ReadAdmissionPublication().IsEnabled)
                return SharpLinkRuntimeConfigurationUpdateResult.Success();

            PublishAdmissionProgram(null, AdmissionPublicationIntent.Disable);
            return SharpLinkRuntimeConfigurationUpdateResult.Success();
        }
    }

    internal SharpLinkRuntimeConfigurationUpdateResult TryUpdateTelemetryDetailPolicyCore(
        SharpLinkTelemetryDetailMode mode)
    {
        SharpLinkTelemetryDetailExtensions.Validate(mode);
        lock (_telemetryDetailGate)
        {
            var state = (ServerState)Volatile.Read(ref _state);
            if (IsRuntimeConfigurationPublicationClosed(state))
                return LifecycleClosed($"Telemetry detail policy cannot be updated while the server is {state}.");

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

    private static bool IsRuntimeConfigurationPublicationClosed(ServerState state)
        => state is ServerState.Draining or ServerState.Stopped or ServerState.Faulted;

    private static SharpLinkRuntimeConfigurationUpdateResult LifecycleClosed(string message)
        => SharpLinkRuntimeConfigurationUpdateResult.Failure(
            SharpLinkRuntimeConfigurationUpdateFailureCode.LifecycleClosed,
            message);

    private static SharpLinkRuntimeConfigurationUpdateResult ModeConflict(string message)
        => SharpLinkRuntimeConfigurationUpdateResult.Failure(
            SharpLinkRuntimeConfigurationUpdateFailureCode.ModeConflict,
            message);

    private static SharpLinkRuntimeConfigurationUpdateResult PublicationConflict(string message)
        => SharpLinkRuntimeConfigurationUpdateResult.Failure(
            SharpLinkRuntimeConfigurationUpdateFailureCode.PublicationConflict,
            message);
}
