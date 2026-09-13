namespace SharpLink.Runtime;

internal sealed class RpcSessionFlushPolicyGeneration
{
    internal RpcSessionFlushPolicyGeneration(
        ulong generation,
        int flushSizeThreshold,
        TimeSpan maxLatency,
        bool explicitBatchWindowEnabled,
        bool flushEveryFrame)
    {
        Generation = generation;
        FlushSizeThreshold = flushSizeThreshold;
        MaxLatency = maxLatency;
        ExplicitBatchWindowEnabled = explicitBatchWindowEnabled;
        FlushEveryFrame = flushEveryFrame;
    }

    internal ulong Generation { get; }
    internal int FlushSizeThreshold { get; }
    internal TimeSpan MaxLatency { get; }
    internal bool ExplicitBatchWindowEnabled { get; }
    internal bool FlushEveryFrame { get; }
}

internal sealed class RpcSessionFlushPolicyState
{
    private readonly Lock _gate = new();
    private RpcSessionFlushPolicyGeneration _current;
    private Action? _changed;

    private RpcSessionFlushPolicyState(RpcSessionFlushPolicyGeneration initial)
        => _current = initial ?? throw new ArgumentNullException(nameof(initial));

    internal static RpcSessionFlushPolicyState Create(
        RpcSessionFlushOptions? flushOptions,
        SharpLinkPerformanceProfile performanceProfile)
    {
        if (flushOptions is { } configured)
        {
            RpcSessionFlushOptions.Validate(configured.FlushSizeThreshold, configured.MaxLatency);
            return new RpcSessionFlushPolicyState(new RpcSessionFlushPolicyGeneration(
                generation: 0,
                configured.FlushSizeThreshold,
                configured.MaxLatency,
                explicitBatchWindowEnabled: true,
                flushEveryFrame: false));
        }

        return performanceProfile switch
        {
            SharpLinkPerformanceProfile.LowLatency => new RpcSessionFlushPolicyState(
                new RpcSessionFlushPolicyGeneration(
                    generation: 0,
                    flushSizeThreshold: 1,
                    maxLatency: TimeSpan.Zero,
                    explicitBatchWindowEnabled: false,
                    flushEveryFrame: true)),
            SharpLinkPerformanceProfile.Throughput => new RpcSessionFlushPolicyState(
                new RpcSessionFlushPolicyGeneration(
                    generation: 0,
                    flushSizeThreshold: 64 * 1024,
                    maxLatency: TimeSpan.Zero,
                    explicitBatchWindowEnabled: false,
                    flushEveryFrame: false)),
            _ => new RpcSessionFlushPolicyState(
                new RpcSessionFlushPolicyGeneration(
                    generation: 0,
                    flushSizeThreshold: 16 * 1024,
                    maxLatency: TimeSpan.Zero,
                    explicitBatchWindowEnabled: false,
                    flushEveryFrame: false))
        };
    }

    internal RpcSessionFlushPolicyGeneration Capture() => Volatile.Read(ref _current);

    internal bool Publish(int flushSizeThreshold, TimeSpan maxLatency)
    {
        RpcSessionFlushOptions.Validate(flushSizeThreshold, maxLatency);
        Action? changed;
        lock (_gate)
        {
            var current = Capture();
            if (current.FlushSizeThreshold == flushSizeThreshold &&
                current.MaxLatency == maxLatency &&
                current.ExplicitBatchWindowEnabled &&
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
                    explicitBatchWindowEnabled: true,
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
