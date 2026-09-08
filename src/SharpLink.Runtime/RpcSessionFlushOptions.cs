namespace SharpLink.Runtime;

/// <summary>Overrides the profile-derived session batching threshold and maximum batching delay.</summary>
public readonly record struct RpcSessionFlushOptions
{
    private readonly RpcSessionFlushPolicyState? _policyState;

    /// <summary>Creates an explicit latency-bounded batching policy.</summary>
    public RpcSessionFlushOptions(int flushSizeThreshold, TimeSpan maxLatency)
        : this(
            flushSizeThreshold,
            maxLatency,
            deadlineBatchingEnabled: true,
            flushEveryFrame: false)
    {
    }

    private RpcSessionFlushOptions(
        int flushSizeThreshold,
        TimeSpan maxLatency,
        bool deadlineBatchingEnabled,
        bool flushEveryFrame)
    {
        FlushSizeThreshold = flushSizeThreshold;
        MaxLatency = maxLatency;
        _policyState = new RpcSessionFlushPolicyState(
            new RpcSessionFlushPolicyGeneration(
                generation: 0,
                flushSizeThreshold,
                maxLatency,
                deadlineBatchingEnabled,
                flushEveryFrame));
    }

    /// <summary>Gets the byte threshold that makes the current batch eligible to flush.</summary>
    public int FlushSizeThreshold { get; }

    /// <summary>Gets the maximum batching latency for an explicitly timed policy.</summary>
    public TimeSpan MaxLatency { get; }

    /// <summary>Gets the compatibility default used by explicitly configured sessions.</summary>
    public static RpcSessionFlushOptions Default => Create(16 * 1024, TimeSpan.FromMilliseconds(1));

    /// <summary>Creates and validates an explicit timed batching policy.</summary>
    public static RpcSessionFlushOptions Create(int flushSizeThreshold, TimeSpan maxLatency)
    {
        Validate(flushSizeThreshold, maxLatency);
        return new RpcSessionFlushOptions(flushSizeThreshold, maxLatency);
    }

    /// <summary>Validates an explicit timed batching policy.</summary>
    public static void Validate(int flushSizeThreshold, TimeSpan maxLatency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(flushSizeThreshold, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxLatency, TimeSpan.Zero);
    }

    internal static RpcSessionFlushOptions CreateProfileDefault(SharpLinkPerformanceProfile performanceProfile)
        => performanceProfile switch
        {
            SharpLinkPerformanceProfile.LowLatency => new RpcSessionFlushOptions(
                flushSizeThreshold: 1,
                maxLatency: TimeSpan.Zero,
                deadlineBatchingEnabled: false,
                flushEveryFrame: true),
            SharpLinkPerformanceProfile.Throughput => new RpcSessionFlushOptions(
                flushSizeThreshold: 64 * 1024,
                maxLatency: TimeSpan.Zero,
                deadlineBatchingEnabled: false,
                flushEveryFrame: false),
            _ => new RpcSessionFlushOptions(
                flushSizeThreshold: 16 * 1024,
                maxLatency: TimeSpan.Zero,
                deadlineBatchingEnabled: false,
                flushEveryFrame: false)
        };

    internal RpcSessionFlushPolicyGeneration CaptureGeneration()
        => (_policyState ?? throw new InvalidOperationException("The RPC flush policy is not initialized."))
            .Capture();

    internal bool PublishRuntimePolicy(int flushSizeThreshold, TimeSpan maxLatency)
    {
        Validate(flushSizeThreshold, maxLatency);
        return (_policyState ?? throw new InvalidOperationException("The RPC flush policy is not initialized."))
            .Publish(flushSizeThreshold, maxLatency);
    }

    internal void RegisterChanged(Action handler)
        => (_policyState ?? throw new InvalidOperationException("The RPC flush policy is not initialized."))
            .RegisterChanged(handler);

    internal void UnregisterChanged(Action handler)
        => _policyState?.UnregisterChanged(handler);
}

internal sealed class RpcSessionFlushPolicyGeneration
{
    internal RpcSessionFlushPolicyGeneration(
        ulong generation,
        int flushSizeThreshold,
        TimeSpan maxLatency,
        bool deadlineBatchingEnabled,
        bool flushEveryFrame)
    {
        Generation = generation;
        FlushSizeThreshold = flushSizeThreshold;
        MaxLatency = maxLatency;
        DeadlineBatchingEnabled = deadlineBatchingEnabled;
        FlushEveryFrame = flushEveryFrame;
    }

    internal ulong Generation { get; }
    internal int FlushSizeThreshold { get; }
    internal TimeSpan MaxLatency { get; }
    internal bool DeadlineBatchingEnabled { get; }
    internal bool FlushEveryFrame { get; }
}

internal sealed class RpcSessionFlushPolicyState
{
    private readonly Lock _gate = new();
    private RpcSessionFlushPolicyGeneration _current;
    private Action? _changed;

    internal RpcSessionFlushPolicyState(RpcSessionFlushPolicyGeneration initial)
        => _current = initial ?? throw new ArgumentNullException(nameof(initial));

    internal RpcSessionFlushPolicyGeneration Capture() => Volatile.Read(ref _current);

    internal bool Publish(int flushSizeThreshold, TimeSpan maxLatency)
    {
        Action? changed;
        lock (_gate)
        {
            var current = Capture();
            if (current.FlushSizeThreshold == flushSizeThreshold &&
                current.MaxLatency == maxLatency &&
                current.DeadlineBatchingEnabled &&
                !current.FlushEveryFrame)
            {
                return false;
            }
            if (current.Generation == ulong.MaxValue)
                throw new InvalidOperationException("The RPC flush policy generation is exhausted.");

            Volatile.Write(
                ref _current,
                new RpcSessionFlushPolicyGeneration(
                    current.Generation + 1,
                    flushSizeThreshold,
                    maxLatency,
                    deadlineBatchingEnabled: true,
                    flushEveryFrame: false));
            changed = _changed;
        }

        changed?.Invoke();
        return true;
    }

    internal void RegisterChanged(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
            _changed += handler;
    }

    internal void UnregisterChanged(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
            _changed -= handler;
    }
}
