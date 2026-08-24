namespace SharpLink.Server;

/// <summary>
/// Stable server-scoped owner for mutable admission accounting and limiter state. Programs are
/// immutable publications that hold references into this kernel; ordinary program retirement does
/// not cancel queued or active work.
/// </summary>
internal sealed class AdmissionStateKernel : IAsyncDisposable
{
    private readonly Lock _accountingGate = new();
    private readonly Lock _registryGate = new();
    private readonly Dictionary<AdmissionRuleStateKey, RuleStateEntry> _ruleStates = [];
    private readonly Dictionary<AdmissionPartitionStateKey, PartitionStateEntry> _partitionStates = [];
    private readonly HashSet<AdmissionProgram> _programs = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<AdmissionProgram> _retiredPrograms = new(ReferenceEqualityComparer.Instance);
    private readonly CancellationTokenSource _draining = new();
    private readonly TimeProvider _timeProvider;
    private TaskCompletionSource<bool> _queueDrained = CompletedSignal();
    private TaskCompletionSource<bool> _permitsDrained = CompletedSignal();
    private TaskCompletionSource<bool> _programsDrained = CompletedSignal();
    private int _queuedCalls;
    private long _queuedBytes;
    private int _activePermits;
    private int _disposed;

    internal AdmissionStateKernel(TimeProvider timeProvider)
        => _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    internal TimeProvider TimeProvider => _timeProvider;

    internal CancellationToken DrainingToken => _draining.Token;

    internal bool IsDraining => _draining.IsCancellationRequested || Volatile.Read(ref _disposed) != 0;

    internal int QueuedCalls => Volatile.Read(ref _queuedCalls);

    internal long QueuedBytes => Volatile.Read(ref _queuedBytes);

    internal int ActivePermits => Volatile.Read(ref _activePermits);

    internal int RetiredProgramCount
    {
        get
        {
            lock (_registryGate)
                return _retiredPrograms.Count;
        }
    }

    internal int LiveProgramCount
    {
        get
        {
            lock (_registryGate)
                return _programs.Count;
        }
    }

    internal int RuleStateCount
    {
        get
        {
            lock (_registryGate)
                return _ruleStates.Count;
        }
    }

    internal int PartitionStateCount
    {
        get
        {
            lock (_registryGate)
                return _partitionStates.Count;
        }
    }

    internal AdmissionProgram CreateProgram(
        SharpLinkAdmissionControlOptions options,
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> manifests)
    {
        if (IsDraining)
            throw new InvalidOperationException("Admission state is sealed for shutdown.");
        var controller = SharpLinkAdmissionController.Create(
            this,
            options,
            manifests,
            _timeProvider,
            ownsKernel: false);
        try
        {
            return new AdmissionProgram(controller);
        }
        catch
        {
            ReleaseUnpublishedBindings(controller);
            throw;
        }
    }

    internal AdmissionRuleStateBinding AcquireRuleState(
        AdmissionRuleStateKey key,
        SharpLinkAdmissionRuleOptions options,
        int queueLimit,
        string scope)
    {
        lock (_registryGate)
        {
            ThrowIfDisposed();
            if (_ruleStates.TryGetValue(key, out var existing))
            {
                existing.ProgramReferences++;
                return new AdmissionRuleStateBinding(key, existing.Runtime);
            }

            var runtime = AdmissionRuleRuntime.Create(options, queueLimit, scope);
            _ruleStates.Add(key, new RuleStateEntry(runtime, 1));
            return new AdmissionRuleStateBinding(key, runtime);
        }
    }

    internal AdmissionPartitionStateBinding AcquirePartitionState(
        AdmissionPartitionStateKey key,
        Func<SharpLinkAdmissionContext, string?> selector,
        SharpLinkPartitionAdmissionOptions options,
        int queueLimit)
    {
        lock (_registryGate)
        {
            ThrowIfDisposed();
            if (_partitionStates.TryGetValue(key, out var existing))
            {
                existing.ProgramReferences++;
                return new AdmissionPartitionStateBinding(key, existing.Pool);
            }

            var pool = new AdmissionPartitionPool(selector, options, queueLimit, _timeProvider);
            _partitionStates.Add(key, new PartitionStateEntry(pool, 1));
            return new AdmissionPartitionStateBinding(key, pool);
        }
    }

    internal void RegisterProgram(AdmissionProgram program)
    {
        lock (_registryGate)
        {
            ThrowIfDisposed();
            if (!_programs.Add(program))
                throw new InvalidOperationException("Admission program was registered twice.");
            if (_programs.Count == 1)
            {
                _programsDrained = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    internal void OnProgramRetired(AdmissionProgram program)
    {
        lock (_registryGate)
        {
            if (_programs.Contains(program))
                _retiredPrograms.Add(program);
        }
    }

    internal void TryReclaimProgram(AdmissionProgram program)
    {
        if (!program.TryMarkReclaimed())
            return;

        List<IDisposable>? dispose = null;
        TaskCompletionSource<bool>? programsDrained = null;
        lock (_registryGate)
        {
            if (!_programs.Remove(program))
                return;
            _retiredPrograms.Remove(program);
            ReleaseBindingsLocked(program.Controller, ref dispose);
            if (_programs.Count == 0)
                programsDrained = _programsDrained;
        }
        programsDrained?.TrySetResult(true);
        DisposeStates(dispose);
    }

    internal void ReleaseUnpublishedBindings(SharpLinkAdmissionController controller)
    {
        List<IDisposable>? dispose = null;
        lock (_registryGate)
            ReleaseBindingsLocked(controller, ref dispose);
        DisposeStates(dispose);
    }

    internal bool TryReserveQueue(
        int retainedBytes,
        int maxQueuedCalls,
        long maxQueuedBytes,
        out string reason)
    {
        lock (_accountingGate)
        {
            if (IsDraining)
            {
                reason = "draining";
                return false;
            }
            if (_queuedCalls >= maxQueuedCalls)
            {
                reason = "queue_count";
                return false;
            }
            if (retainedBytes > maxQueuedBytes - _queuedBytes)
            {
                reason = "queue_bytes";
                return false;
            }
            if (_queuedCalls++ == 0)
            {
                _queueDrained = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
            _queuedBytes += retainedBytes;
        }
        SharpLinkTelemetry.AddAdmissionQueuedCalls(1);
        reason = string.Empty;
        return true;
    }

    internal void ReleaseQueue(int retainedBytes)
    {
        TaskCompletionSource<bool>? drained = null;
        lock (_accountingGate)
        {
            if (--_queuedCalls < 0)
                throw new InvalidOperationException("Admission queued call accounting underflowed.");
            _queuedBytes -= retainedBytes;
            if (_queuedBytes < 0)
                throw new InvalidOperationException("Admission queued byte accounting underflowed.");
            if (_queuedCalls == 0)
                drained = _queueDrained;
        }
        drained?.TrySetResult(true);
        SharpLinkTelemetry.AddAdmissionQueuedCalls(-1);
    }

    internal bool TryReserveAdditionalQueuedBytes(int retainedBytes, long maxQueuedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainedBytes);
        lock (_accountingGate)
        {
            if (IsDraining || retainedBytes > maxQueuedBytes - _queuedBytes)
                return false;
            _queuedBytes += retainedBytes;
            return true;
        }
    }

    internal void ReleaseAdditionalQueuedBytes(int retainedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainedBytes);
        lock (_accountingGate)
        {
            _queuedBytes -= retainedBytes;
            if (_queuedBytes < 0)
                throw new InvalidOperationException("Admission queued byte accounting underflowed.");
        }
    }

    internal void OnLeaseCreated()
    {
        lock (_accountingGate)
        {
            if (_activePermits++ == 0)
            {
                _permitsDrained = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        SharpLinkTelemetry.AddAdmissionActivePermits(1);
    }

    internal void OnLeaseDisposed()
    {
        TaskCompletionSource<bool>? drained = null;
        lock (_accountingGate)
        {
            if (--_activePermits < 0)
                throw new InvalidOperationException("Admission active permit accounting underflowed.");
            if (_activePermits == 0)
                drained = _permitsDrained;
        }
        drained?.TrySetResult(true);
        SharpLinkTelemetry.AddAdmissionActivePermits(-1);
    }

    /// <summary>Shutdown-only cancellation. Ordinary program retirement never calls this method.</summary>
    internal void StopAccepting()
    {
        try
        {
            _draining.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        AdmissionProgram[] programs;
        lock (_registryGate)
            programs = [.. _programs];
        foreach (var program in programs)
            program.Retire();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        StopAccepting();
        while (true)
        {
            Task queueDrained;
            Task permitsDrained;
            Task programsDrained;
            lock (_accountingGate)
            {
                queueDrained = _queueDrained.Task;
                permitsDrained = _permitsDrained.Task;
            }
            lock (_registryGate)
                programsDrained = _programsDrained.Task;

            await Task.WhenAll(queueDrained, permitsDrained, programsDrained).ConfigureAwait(false);

            lock (_accountingGate)
            {
                if (_queuedCalls != 0 || _activePermits != 0)
                    continue;
            }
            lock (_registryGate)
            {
                if (_programs.Count == 0)
                    break;
            }
        }

        List<IDisposable> dispose = [];
        lock (_registryGate)
        {
            foreach (var entry in _ruleStates.Values)
                dispose.Add(entry.Runtime);
            foreach (var entry in _partitionStates.Values)
                dispose.Add(entry.Pool);
            _ruleStates.Clear();
            _partitionStates.Clear();
            _retiredPrograms.Clear();
        }
        DisposeStates(dispose);
        _draining.Dispose();
    }

    private void ReleaseBindingsLocked(
        SharpLinkAdmissionController controller,
        ref List<IDisposable>? dispose)
    {
        foreach (var binding in controller.RuleStateBindings)
        {
            if (!_ruleStates.TryGetValue(binding.Key, out var entry) ||
                !ReferenceEquals(entry.Runtime, binding.Runtime))
            {
                continue;
            }
            if (--entry.ProgramReferences < 0)
                throw new InvalidOperationException("Admission rule state reference count underflowed.");
            if (entry.ProgramReferences == 0)
            {
                _ruleStates.Remove(binding.Key);
                (dispose ??= []).Add(entry.Runtime);
            }
        }

        if (controller.PartitionStateBinding is { } partitionBinding &&
            _partitionStates.TryGetValue(partitionBinding.Key, out var partitionEntry) &&
            ReferenceEquals(partitionEntry.Pool, partitionBinding.Pool))
        {
            if (--partitionEntry.ProgramReferences < 0)
                throw new InvalidOperationException("Admission partition state reference count underflowed.");
            if (partitionEntry.ProgramReferences == 0)
            {
                _partitionStates.Remove(partitionBinding.Key);
                (dispose ??= []).Add(partitionEntry.Pool);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(AdmissionStateKernel));
    }

    private static void DisposeStates(List<IDisposable>? states)
    {
        if (states is null)
            return;
        foreach (var state in states)
            state.Dispose();
    }

    private static TaskCompletionSource<bool> CompletedSignal()
    {
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult(true);
        return signal;
    }

    private sealed class RuleStateEntry(AdmissionRuleRuntime runtime, int programReferences)
    {
        internal AdmissionRuleRuntime Runtime { get; } = runtime;
        internal int ProgramReferences = programReferences;
    }

    private sealed class PartitionStateEntry(AdmissionPartitionPool pool, int programReferences)
    {
        internal AdmissionPartitionPool Pool { get; } = pool;
        internal int ProgramReferences = programReferences;
    }
}

internal enum AdmissionRuleStateScope : byte
{
    Global,
    Contract,
    Method
}

internal enum AdmissionRateStateKind : byte
{
    None,
    TokenBucket,
    FixedWindow,
    SlidingWindow
}

internal readonly record struct AdmissionRateStateDefinition(
    AdmissionRateStateKind Kind,
    int Limit,
    int Secondary,
    long PeriodTicks,
    int Segments)
{
    internal static AdmissionRateStateDefinition Create(object? options)
        => options switch
        {
            SharpLinkTokenBucketLimitOptions value => new(
                AdmissionRateStateKind.TokenBucket,
                value.TokenLimit,
                value.TokensPerPeriod,
                value.ReplenishmentPeriod.Ticks,
                0),
            SharpLinkFixedWindowLimitOptions value => new(
                AdmissionRateStateKind.FixedWindow,
                value.PermitLimit,
                0,
                value.Window.Ticks,
                0),
            SharpLinkSlidingWindowLimitOptions value => new(
                AdmissionRateStateKind.SlidingWindow,
                value.PermitLimit,
                0,
                value.Window.Ticks,
                value.SegmentsPerWindow),
            _ => default
        };
}

internal readonly record struct AdmissionRuleStateDefinition(
    int ConcurrencyPermitLimit,
    AdmissionRateStateDefinition Rate,
    int QueueLimit)
{
    internal static AdmissionRuleStateDefinition Create(
        SharpLinkAdmissionRuleOptions options,
        int queueLimit)
        => new(
            options.Concurrency?.PermitLimit ?? 0,
            AdmissionRateStateDefinition.Create(options.RateLimit),
            queueLimit);
}

internal readonly record struct AdmissionRuleStateKey(
    AdmissionRuleStateScope Scope,
    long ContractId,
    long MethodId,
    AdmissionRuleStateDefinition Definition)
{
    internal static AdmissionRuleStateKey Global(SharpLinkAdmissionRuleOptions options, int queueLimit)
        => new(AdmissionRuleStateScope.Global, 0, 0, AdmissionRuleStateDefinition.Create(options, queueLimit));

    internal static AdmissionRuleStateKey Contract(
        long contractId,
        SharpLinkAdmissionRuleOptions options,
        int queueLimit)
        => new(AdmissionRuleStateScope.Contract, contractId, 0, AdmissionRuleStateDefinition.Create(options, queueLimit));

    internal static AdmissionRuleStateKey Method(
        long contractId,
        long methodId,
        SharpLinkAdmissionRuleOptions options,
        int queueLimit)
        => new(AdmissionRuleStateScope.Method, contractId, methodId, AdmissionRuleStateDefinition.Create(options, queueLimit));
}

internal readonly record struct AdmissionPartitionStateKey(
    Func<SharpLinkAdmissionContext, string?> Selector,
    AdmissionRuleStateDefinition Definition,
    int MaxPartitions,
    long IdleTimeoutTicks)
{
    internal static AdmissionPartitionStateKey Create(
        Func<SharpLinkAdmissionContext, string?> selector,
        SharpLinkPartitionAdmissionOptions options,
        int queueLimit)
        => new(
            selector,
            AdmissionRuleStateDefinition.Create(options, queueLimit),
            options.MaxPartitions,
            options.IdleTimeout.Ticks);
}

internal readonly record struct AdmissionRuleStateBinding(
    AdmissionRuleStateKey Key,
    AdmissionRuleRuntime Runtime);

internal readonly record struct AdmissionPartitionStateBinding(
    AdmissionPartitionStateKey Key,
    AdmissionPartitionPool Pool);
