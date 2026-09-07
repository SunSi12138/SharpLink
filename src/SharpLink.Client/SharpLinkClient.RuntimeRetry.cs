namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private ClientRetryGeneration? _retryGeneration;

    public SharpLinkRetryPolicySnapshot GetRetryPolicySnapshot()
    {
        var current = CaptureRetryGeneration();
        return current.Kind == SharpLinkRetryPolicyKind.Disabled
            ? new SharpLinkRetryPolicySnapshot(
                current.Generation,
                SharpLinkRetryPolicyKind.Disabled,
                0,
                TimeSpan.Zero,
                TimeSpan.Zero,
                0)
            : new SharpLinkRetryPolicySnapshot(
                current.Generation,
                current.Kind,
                current.Settings.MaxAttempts,
                current.Settings.InitialBackoff,
                current.Settings.MaxBackoff,
                current.Settings.JitterRatio);
    }

    public void UpdateRetryPolicy(ISharpLinkRetryOptions options)
    {
        var settings = ClientRetrySettings.CopyValidated(options);
        PublishRetryGeneration(SharpLinkRetryPolicyKind.BuiltIn, settings, policy: null);
    }

    public void UpdateRetryPolicy(ISharpLinkRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        PublishRetryGeneration(
            SharpLinkRetryPolicyKind.Custom,
            ClientRetrySettings.Default,
            policy);
    }

    public void UpdateRetryPolicy(ISharpLinkRetryPolicy policy, ISharpLinkRetryOptions limits)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var settings = ClientRetrySettings.CopyValidated(limits);
        PublishRetryGeneration(SharpLinkRetryPolicyKind.Custom, settings, policy);
    }

    public void DisableRetry()
        => PublishRetryGeneration(
            SharpLinkRetryPolicyKind.Disabled,
            default,
            policy: null);

    private void PublishRetryGeneration(
        SharpLinkRetryPolicyKind kind,
        ClientRetrySettings settings,
        ISharpLinkRetryPolicy? policy)
    {
        lock (_stateGate)
        {
            var state = State;
            if (Volatile.Read(ref _stopStarted) != 0 ||
                state is SharpLinkConnectionState.Draining or
                    SharpLinkConnectionState.Stopped or
                    SharpLinkConnectionState.Faulted)
            {
                throw new InvalidOperationException(
                    $"Retry policy cannot be updated while the client is {state}.");
            }

            var current = CaptureRetryGeneration();
            if (current.Kind == kind &&
                current.Settings == settings &&
                ReferenceEquals(current.Policy, policy))
            {
                return;
            }
            if (current.Generation == ulong.MaxValue)
                throw new InvalidOperationException("Retry policy generation is exhausted.");

            Volatile.Write(
                ref _retryGeneration,
                new ClientRetryGeneration(
                    current.Generation + 1,
                    kind,
                    settings,
                    policy));
        }
    }

    private ClientRetryGeneration CaptureRetryGeneration()
    {
        var current = Volatile.Read(ref _retryGeneration);
        if (current is not null)
            return current;

        var initial = _retryOptions is null
            ? new ClientRetryGeneration(
                0,
                SharpLinkRetryPolicyKind.Disabled,
                default,
                policy: null)
            : new ClientRetryGeneration(
                0,
                _retryPolicy is null
                    ? SharpLinkRetryPolicyKind.BuiltIn
                    : SharpLinkRetryPolicyKind.Custom,
                ClientRetrySettings.CopyValidated(_retryOptions),
                _retryPolicy);
        return Interlocked.CompareExchange(ref _retryGeneration, initial, null) ?? initial;
    }

    private ResolvedCallControl CaptureRetryGenerationForInvocation(
        RpcMethodDescriptor method,
        in ResolvedCallControl control)
    {
        if (method.Kind != RpcMethodKind.Unary || !method.IsIdempotent)
            return control;

        var generation = CaptureRetryGeneration();
        if (control.LogicalCall is { } logicalCall)
        {
            logicalCall.AttachRetryGeneration(generation);
            return control;
        }

        return control with { LogicalCall = generation.SharedLogicalCall };
    }

    internal readonly record struct ClientRetrySettings(
        int MaxAttempts,
        TimeSpan InitialBackoff,
        TimeSpan MaxBackoff,
        double JitterRatio)
    {
        internal static ClientRetrySettings Default { get; } = new(
            3,
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(200),
            0.2);

        internal static ClientRetrySettings CopyValidated(ISharpLinkRetryOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            var maxAttempts = options.MaxAttempts;
            var initialBackoff = options.InitialBackoff;
            var maxBackoff = options.MaxBackoff;
            var jitterRatio = options.JitterRatio;

            if (maxAttempts is < 1 or > 10)
                throw new ArgumentOutOfRangeException(nameof(options), "MaxAttempts must be from one through ten.");
            if (initialBackoff < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(options), "InitialBackoff must be non-negative.");
            if (maxBackoff < TimeSpan.Zero || maxBackoff < initialBackoff)
                throw new ArgumentOutOfRangeException(nameof(options), "MaxBackoff must be non-negative and not smaller than InitialBackoff.");
            if (jitterRatio is < 0 or > 1 || double.IsNaN(jitterRatio))
                throw new ArgumentOutOfRangeException(nameof(options), "JitterRatio must be from zero through one.");

            return new ClientRetrySettings(
                maxAttempts,
                initialBackoff,
                maxBackoff,
                jitterRatio);
        }
    }

    internal sealed class ClientRetryGeneration
    {
        internal ClientRetryGeneration(
            ulong generation,
            SharpLinkRetryPolicyKind kind,
            ClientRetrySettings settings,
            ISharpLinkRetryPolicy? policy)
        {
            Generation = generation;
            Kind = kind;
            Settings = settings;
            Policy = policy;
            SharedLogicalCall = new ClientLogicalCallState(this);
        }

        internal ulong Generation { get; }
        internal SharpLinkRetryPolicyKind Kind { get; }
        internal ClientRetrySettings Settings { get; }
        internal ISharpLinkRetryPolicy? Policy { get; }
        internal ClientLogicalCallState SharedLogicalCall { get; }
        internal bool Enabled => Kind != SharpLinkRetryPolicyKind.Disabled;
    }
}
