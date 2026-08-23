using System.Threading.RateLimiting;

namespace SharpLink.Server;

internal sealed class SharpLinkAdmissionController : IAsyncDisposable
{
    private readonly AdmissionRuleRuntime? _global;
    private readonly FrozenDictionary<long, AdmissionRuleRuntime> _contracts;
    private readonly FrozenDictionary<(long ContractId, long MethodId), AdmissionRuleRuntime> _methods;
    private readonly int _maxQueuedCalls;
    private readonly long _maxQueuedBytes;
    private readonly TimeSpan _maxQueueDelay;
    private readonly bool _queueOneWayCalls;
    private readonly TimeProvider _timeProvider;
    private readonly AdmissionPartitionPool? _partitions;
    private readonly CancellationTokenSource _draining = new();
    private readonly Lock _queueGate = new();
    private int _queuedCalls;
    private long _queuedBytes;
    private int _activePermits;
    private int _disposed;
    private TaskCompletionSource<bool> _queueDrained = CompletedSignal();
    private TaskCompletionSource<bool> _permitsDrained = CompletedSignal();

    private SharpLinkAdmissionController(
        SharpLinkAdmissionControlOptions options,
        AdmissionRuleRuntime? global,
        FrozenDictionary<long, AdmissionRuleRuntime> contracts,
        FrozenDictionary<(long ContractId, long MethodId), AdmissionRuleRuntime> methods,
        AdmissionPartitionPool? partitions,
        TimeProvider timeProvider)
    {
        _maxQueuedCalls = options.MaxQueuedCalls;
        _maxQueuedBytes = options.MaxQueuedBytes;
        _maxQueueDelay = options.MaxQueueDelay;
        _queueOneWayCalls = options.QueueOneWayCalls;
        _timeProvider = timeProvider;
        _global = global;
        _contracts = contracts;
        _methods = methods;
        _partitions = partitions;
    }

    internal static SharpLinkAdmissionController Create(
        SharpLinkAdmissionControlOptions options,
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> manifests,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(manifests);
        timeProvider ??= TimeProvider.System;
        options.Validate();
        var contractsByType = new Dictionary<Type, SharpLinkGeneratedContractDescriptor>();
        foreach (var manifest in manifests)
        {
            foreach (var contract in manifest.Contracts)
                contractsByType.TryAdd(contract.ContractType, contract);
        }

        var contractOptions = new Dictionary<long, SharpLinkAdmissionRuleOptions>();
        var methodOptions = new Dictionary<(long, long), SharpLinkAdmissionRuleOptions>();
        foreach (var registration in options.Rules)
        {
            var contractId = registration.ContractId;
            SharpLinkGeneratedContractDescriptor? contract = null;
            if (registration.ContractType is { } contractType)
            {
                if (!contractsByType.TryGetValue(contractType, out contract))
                    throw new InvalidOperationException(
                        $"Generated contract '{contractType.FullName}' required by admission control was not found.");
                contractId = contract.ContractId;
            }
            if (contractId is null or 0)
                throw new InvalidOperationException("Admission contract identity was not resolved.");

            if (registration.MethodName is null && registration.MethodId is null)
            {
                if (!contractOptions.TryAdd(contractId.Value, registration.Rule))
                {
                    throw new InvalidOperationException(
                        $"Admission control has duplicate rules for contract {contractId.Value}.");
                }
                continue;
            }

            var methodId = registration.MethodId;
            if (registration.MethodName is { } methodName)
            {
                contract ??= contractsByType.Values.FirstOrDefault(candidate => candidate.ContractId == contractId.Value);
                if (contract is null)
                    throw new InvalidOperationException(
                        $"Generated contract {contractId.Value} required to resolve method '{methodName}' was not found.");
                var matches = contract.Methods.Where(method =>
                    string.Equals(method.Name, methodName, StringComparison.Ordinal)).ToArray();
                if (matches.Length != 1)
                {
                    throw new InvalidOperationException(matches.Length == 0
                        ? $"Generated method '{contract.ContractName}.{methodName}' was not found."
                        : $"Generated method name '{contract.ContractName}.{methodName}' is ambiguous; configure stable IDs instead.");
                }
                methodId = matches[0].MethodId;
            }
            if (methodId is null or 0)
                throw new InvalidOperationException("Admission method identity was not resolved.");
            var key = (contractId.Value, methodId.Value);
            if (!methodOptions.TryAdd(key, registration.Rule))
            {
                throw new InvalidOperationException(
                    $"Admission control has duplicate rules for method {key.Item1}/{key.Item2}.");
            }
        }

        AdmissionRuleRuntime? global = null;
        var contractRules = new Dictionary<long, AdmissionRuleRuntime>(contractOptions.Count);
        var methodRules = new Dictionary<(long, long), AdmissionRuleRuntime>(methodOptions.Count);
        try
        {
            global = options.Global.HasLimit
                ? AdmissionRuleRuntime.Create(options.Global, options.MaxQueuedCalls, "global")
                : null;
            foreach (var pair in contractOptions)
            {
                contractRules.Add(
                    pair.Key,
                    AdmissionRuleRuntime.Create(pair.Value, options.MaxQueuedCalls, "contract"));
            }
            foreach (var pair in methodOptions)
            {
                methodRules.Add(
                    pair.Key,
                    AdmissionRuleRuntime.Create(pair.Value, options.MaxQueuedCalls, "method"));
            }
            var partitions = options.Partition is { } partition
                ? new AdmissionPartitionPool(
                    options.PartitionSelector!,
                    partition,
                    options.MaxQueuedCalls,
                    timeProvider)
                : null;
            return new SharpLinkAdmissionController(
                options,
                global,
                contractRules.ToFrozenDictionary(),
                methodRules.ToFrozenDictionary(),
                partitions,
                timeProvider);
        }
        catch
        {
            global?.Dispose();
            foreach (var rule in contractRules.Values)
                rule.Dispose();
            foreach (var rule in methodRules.Values)
                rule.Dispose();
            throw;
        }
    }

    internal ValueTask<AdmissionDecision> AcquireAsync(
        SharpLinkAdmissionContext context,
        int retainedBytes,
        bool allowQueue,
        CancellationToken cancellationToken)
        => AcquireAsync(
            context,
            retainedBytes,
            allowQueue,
            context.Deadline is { } deadline
                ? RpcDeadline.Create(deadline, _timeProvider)
                : default,
            cancellationToken);

    internal ValueTask<AdmissionDecision> AcquireAsync(
        SharpLinkAdmissionContext context,
        int retainedBytes,
        bool allowQueue,
        RpcDeadline deadline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfNegative(retainedBytes);
        if (_draining.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
            return ValueTask.FromResult(AdmissionDecision.Reject("draining", SharpLinkErrorCode.Unavailable));

        AdmissionPartitionLease? partitionLease = null;
        if (_partitions is not null)
        {
            partitionLease = _partitions.TryAcquire(context);
            if (partitionLease is null)
                return ValueTask.FromResult(AdmissionDecision.Reject("partition_capacity"));
        }

        var request = CreateRequest(context, partitionLease);
        if (request.TryAcquire(this, out var lease, out var failedSlot))
            return ValueTask.FromResult(AdmissionDecision.Accept(lease!));

        if (!allowQueue || _maxQueuedCalls == 0)
        {
            request.Dispose();
            return ValueTask.FromResult(AdmissionDecision.Reject(failedSlot.Reason, failedSlot.Scope));
        }

        if (!TryReserveQueue(retainedBytes, out var queueReason))
        {
            request.Dispose();
            return ValueTask.FromResult(queueReason == "draining"
                ? AdmissionDecision.Reject(queueReason, SharpLinkErrorCode.Unavailable)
                : AdmissionDecision.Reject(queueReason));
        }
        return WaitForAdmissionAsync(
            request,
            failedSlot,
            retainedBytes,
            deadline,
            cancellationToken);
    }

    internal void StopAccepting()
    {
        try
        {
            _draining.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private AdmissionRequest CreateRequest(
        SharpLinkAdmissionContext context,
        AdmissionPartitionLease? partitionLease)
    {
        _contracts.TryGetValue(context.ContractId, out var contract);
        _methods.TryGetValue((context.ContractId, context.MethodId), out var method);
        var count = (_global?.SlotCount ?? 0) +
                    (contract?.SlotCount ?? 0) +
                    (method?.SlotCount ?? 0) +
                    (partitionLease?.Runtime.SlotCount ?? 0);
        var slots = new AdmissionLimiterSlot[count];
        count = 0;
        _global?.AppendTo(slots, ref count);
        contract?.AppendTo(slots, ref count);
        method?.AppendTo(slots, ref count);
        partitionLease?.Runtime.AppendTo(slots, ref count);
        return new AdmissionRequest(slots, count, partitionLease);
    }

    private async ValueTask<AdmissionDecision> WaitForAdmissionAsync(
        AdmissionRequest request,
        AdmissionLimiterSlot failedSlot,
        int retainedBytes,
        RpcDeadline deadline,
        CancellationToken cancellationToken)
    {
        var started = _timeProvider.GetTimestamp();
        var maximumDelay = _maxQueueDelay;
        var deadlineLimitsWait = false;
        if (deadline.HasValue && deadline.WouldExpireBeforeOrAt(maximumDelay, _timeProvider))
        {
            maximumDelay = deadline.GetRemaining(_timeProvider);
            deadlineLimitsWait = true;
        }
        using var timeoutCancellation = maximumDelay <= TimeSpan.Zero
            ? new CancellationTokenSource()
            : new CancellationTokenSource(maximumDelay, _timeProvider);
        if (maximumDelay <= TimeSpan.Zero)
            timeoutCancellation.Cancel();
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _draining.Token,
            timeoutCancellation.Token);

        try
        {
            while (true)
            {
                RateLimitLease waitedLease;
                try
                {
                    waitedLease = await failedSlot.Limiter
                        .AcquireAsync(1, waitCancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    _draining.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    return AdmissionDecision.Reject("draining", SharpLinkErrorCode.Unavailable);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // The admission timer and the server deadline scheduler intentionally race.
                    // Preserve the deadline result when this local bounded-wait timer wins;
                    // otherwise identical calls could surface ResourceExhausted or
                    // DeadlineExceeded depending on scheduler timing.
                    return deadlineLimitsWait
                        ? AdmissionDecision.Reject("deadline", SharpLinkErrorCode.DeadlineExceeded)
                        : AdmissionDecision.Reject(failedSlot.Reason, failedSlot.Scope);
                }

                if (!waitedLease.IsAcquired)
                {
                    waitedLease.Dispose();
                    return AdmissionDecision.Reject(failedSlot.Reason, failedSlot.Scope);
                }
                if (request.TryAcquireUsing(
                        this,
                        failedSlot.Limiter,
                        waitedLease,
                        out var lease,
                        out failedSlot))
                {
                    return AdmissionDecision.Accept(lease!);
                }
            }
        }
        finally
        {
            ReleaseQueue(retainedBytes);
            SharpLinkTelemetry.RecordAdmissionQueueDuration(
                _timeProvider.GetElapsedTime(started));
            request.Dispose();
        }
    }

    private bool TryReserveQueue(int retainedBytes, out string reason)
    {
        lock (_queueGate)
        {
            if (_draining.IsCancellationRequested)
            {
                reason = "draining";
                return false;
            }
            if (_queuedCalls >= _maxQueuedCalls)
            {
                reason = "queue_count";
                return false;
            }
            if (retainedBytes > _maxQueuedBytes - _queuedBytes)
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

    private void ReleaseQueue(int retainedBytes)
    {
        TaskCompletionSource<bool>? drained = null;
        lock (_queueGate)
        {
            _queuedCalls--;
            _queuedBytes -= retainedBytes;
            if (_queuedCalls == 0)
                drained = _queueDrained;
        }
        drained?.TrySetResult(true);
        SharpLinkTelemetry.AddAdmissionQueuedCalls(-1);
    }

    internal bool TryReserveAdditionalQueuedBytes(int retainedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainedBytes);
        lock (_queueGate)
        {
            if (_draining.IsCancellationRequested ||
                retainedBytes > _maxQueuedBytes - _queuedBytes)
            {
                return false;
            }
            _queuedBytes += retainedBytes;
            return true;
        }
    }

    internal void ReleaseAdditionalQueuedBytes(int retainedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainedBytes);
        lock (_queueGate)
        {
            _queuedBytes -= retainedBytes;
            if (_queuedBytes < 0)
                throw new InvalidOperationException("Admission queued byte accounting underflowed.");
        }
    }

    internal void OnLeaseCreated()
    {
        lock (_queueGate)
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
        lock (_queueGate)
        {
            if (--_activePermits == 0)
                drained = _permitsDrained;
        }
        drained?.TrySetResult(true);
        SharpLinkTelemetry.AddAdmissionActivePermits(-1);
    }

    internal int ActivePermits => Volatile.Read(ref _activePermits);
    internal int QueuedCalls => Volatile.Read(ref _queuedCalls);
    internal long QueuedBytes => Volatile.Read(ref _queuedBytes);
    internal int ActivePartitions => _partitions?.Count ?? 0;
    internal bool QueueOneWayCalls => _queueOneWayCalls;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        StopAccepting();
        while (true)
        {
            Task queueDrained;
            Task permitsDrained;
            lock (_queueGate)
            {
                queueDrained = _queueDrained.Task;
                permitsDrained = _permitsDrained.Task;
            }
            await Task.WhenAll(queueDrained, permitsDrained).ConfigureAwait(false);
            lock (_queueGate)
            {
                if (_queuedCalls == 0 && _activePermits == 0)
                    break;
            }
        }
        _global?.Dispose();
        foreach (var rule in _contracts.Values)
            rule.Dispose();
        foreach (var rule in _methods.Values)
            rule.Dispose();
        _partitions?.Dispose();
        _draining.Dispose();
        await ValueTask.CompletedTask;
    }

    private static TaskCompletionSource<bool> CompletedSignal()
    {
        var signal = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult(true);
        return signal;
    }
}

internal readonly record struct AdmissionDecision(
    AdmissionLease? Lease,
    string? Reason,
    string? Scope,
    SharpLinkErrorCode ErrorCode)
{
    internal bool IsAcquired => Lease is not null;
    internal static AdmissionDecision Accept(AdmissionLease lease)
        => new(lease, null, null, SharpLinkErrorCode.Unknown);
    internal static AdmissionDecision Reject(
        string reason,
        string scope = "queue",
        SharpLinkErrorCode errorCode = SharpLinkErrorCode.ResourceExhausted)
        => new(null, reason, scope, errorCode);
    internal static AdmissionDecision Reject(string reason, SharpLinkErrorCode errorCode)
        => new(null, reason, "server", errorCode);
}

internal sealed class AdmissionLease : IDisposable
{
    private SharpLinkAdmissionController? _owner;
    private RateLimitLease? _singleLease;
    private RateLimitLease[]? _leases;
    private AdmissionPartitionLease? _partition;

    internal AdmissionLease(
        SharpLinkAdmissionController owner,
        RateLimitLease singleLease,
        AdmissionPartitionLease? partition)
    {
        _owner = owner;
        _singleLease = singleLease;
        _partition = partition;
        owner.OnLeaseCreated();
    }

    internal AdmissionLease(
        SharpLinkAdmissionController owner,
        RateLimitLease[] leases,
        AdmissionPartitionLease? partition)
    {
        _owner = owner;
        _leases = leases;
        _partition = partition;
        owner.OnLeaseCreated();
    }

    public void Dispose()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        if (owner is null)
            return;
        Interlocked.Exchange(ref _singleLease, null)?.Dispose();
        var leases = Interlocked.Exchange(ref _leases, null);
        if (leases is not null)
        {
            for (var index = leases.Length - 1; index >= 0; index--)
                leases[index]?.Dispose();
        }
        Interlocked.Exchange(ref _partition, null)?.Dispose();
        owner.OnLeaseDisposed();
    }
}

internal sealed class AdmissionRequest(
    AdmissionLimiterSlot[] slots,
    int slotCount,
    AdmissionPartitionLease? partition) : IDisposable
{
    private AdmissionPartitionLease? _partition = partition;
    private readonly RateLimitLease?[]? _retainedLeases =
        HasRetainedSlot(slots, slotCount) ? new RateLimitLease?[slotCount] : null;

    internal bool TryAcquire(
        SharpLinkAdmissionController owner,
        out AdmissionLease? admissionLease,
        out AdmissionLimiterSlot failedSlot)
        => TryAcquireCore(owner, null, null, out admissionLease, out failedSlot);

    internal bool TryAcquireUsing(
        SharpLinkAdmissionController owner,
        RateLimiter suppliedLimiter,
        RateLimitLease suppliedLease,
        out AdmissionLease? admissionLease,
        out AdmissionLimiterSlot failedSlot)
        => TryAcquireCore(
            owner,
            suppliedLimiter,
            suppliedLease,
            out admissionLease,
            out failedSlot);

    private bool TryAcquireCore(
        SharpLinkAdmissionController owner,
        RateLimiter? suppliedLimiter,
        RateLimitLease? suppliedLease,
        out AdmissionLease? admissionLease,
        out AdmissionLimiterSlot failedSlot)
    {
        if (slotCount == 1 && _retainedLeases is null && suppliedLease is null)
        {
            var singleLease = slots[0].Limiter.AttemptAcquire(1);
            if (!singleLease.IsAcquired)
            {
                singleLease.Dispose();
                admissionLease = null;
                failedSlot = slots[0];
                return false;
            }
            admissionLease = new AdmissionLease(
                owner,
                singleLease,
                Interlocked.Exchange(ref _partition, null));
            failedSlot = default;
            return true;
        }

        var retainedLeases = _retainedLeases;
        var leases = new RateLimitLease[slotCount];
        var suppliedIndex = -1;
        if (suppliedLease is not null)
        {
            for (var index = 0; index < slotCount; index++)
            {
                if (!ReferenceEquals(slots[index].Limiter, suppliedLimiter))
                    continue;
                suppliedIndex = index;
                if (slots[index].RetainOnFailure)
                    retainedLeases![index] = suppliedLease;
                break;
            }
            if (suppliedIndex < 0)
            {
                suppliedLease.Dispose();
                throw new InvalidOperationException("The supplied admission limiter is not part of this request.");
            }
        }

        for (var index = 0; index < slotCount; index++)
        {
            var lease = retainedLeases?[index] ??
                (index == suppliedIndex
                    ? suppliedLease!
                    : slots[index].Limiter.AttemptAcquire(1));
            if (!lease.IsAcquired)
            {
                lease.Dispose();
                for (var acquired = index - 1; acquired >= 0; acquired--)
                {
                    if (!ReferenceEquals(retainedLeases?[acquired], leases[acquired]))
                        leases[acquired].Dispose();
                }
                if (suppliedLease is not null &&
                    suppliedIndex > index &&
                    !ReferenceEquals(retainedLeases?[suppliedIndex], suppliedLease))
                {
                    suppliedLease.Dispose();
                }
                admissionLease = null;
                failedSlot = slots[index];
                return false;
            }
            if (slots[index].RetainOnFailure)
                retainedLeases![index] = lease;
            leases[index] = lease;
        }

        if (retainedLeases is not null)
            Array.Clear(retainedLeases, 0, slotCount);
        var ownedPartition = Interlocked.Exchange(ref _partition, null);
        admissionLease = new AdmissionLease(owner, leases, ownedPartition);
        failedSlot = default;
        return true;
    }

    public void Dispose()
    {
        if (_retainedLeases is not null)
            for (var index = _retainedLeases.Length - 1; index >= 0; index--)
                Interlocked.Exchange(ref _retainedLeases[index], null)?.Dispose();
        Interlocked.Exchange(ref _partition, null)?.Dispose();
    }

    private static bool HasRetainedSlot(AdmissionLimiterSlot[] slots, int slotCount)
    {
        for (var index = 0; index < slotCount; index++)
            if (slots[index].RetainOnFailure)
                return true;
        return false;
    }
}

internal readonly record struct AdmissionLimiterSlot(
    RateLimiter Limiter,
    string Scope,
    string Reason,
    bool RetainOnFailure);

internal sealed class AdmissionRuleRuntime : IDisposable
{
    private readonly AdmissionLimiterSlot[] _slots;

    private AdmissionRuleRuntime(AdmissionLimiterSlot[] slots) => _slots = slots;

    internal int SlotCount => _slots.Length;

    internal static AdmissionRuleRuntime Create(
        SharpLinkAdmissionRuleOptions options,
        int queueLimit,
        string scope)
    {
        var slots = new List<AdmissionLimiterSlot>(2);
        if (options.Concurrency is { } concurrency)
        {
            slots.Add(new AdmissionLimiterSlot(
                new ConcurrencyLimiter(new ConcurrencyLimiterOptions
                {
                    PermitLimit = concurrency.PermitLimit,
                    QueueLimit = queueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                }),
                scope,
                "concurrency",
                RetainOnFailure: false));
        }

        RateLimiter? rateLimiter = options.RateLimit switch
        {
            SharpLinkTokenBucketLimitOptions tokenBucket => new TokenBucketRateLimiter(
                new TokenBucketRateLimiterOptions
                {
                    TokenLimit = tokenBucket.TokenLimit,
                    TokensPerPeriod = tokenBucket.TokensPerPeriod,
                    ReplenishmentPeriod = tokenBucket.ReplenishmentPeriod,
                    AutoReplenishment = true,
                    QueueLimit = queueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                }),
            SharpLinkFixedWindowLimitOptions fixedWindow => new FixedWindowRateLimiter(
                new FixedWindowRateLimiterOptions
                {
                    PermitLimit = fixedWindow.PermitLimit,
                    Window = fixedWindow.Window,
                    AutoReplenishment = true,
                    QueueLimit = queueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                }),
            SharpLinkSlidingWindowLimitOptions slidingWindow => new SlidingWindowRateLimiter(
                new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = slidingWindow.PermitLimit,
                    Window = slidingWindow.Window,
                    SegmentsPerWindow = slidingWindow.SegmentsPerWindow,
                    AutoReplenishment = true,
                    QueueLimit = queueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                }),
            _ => null
        };
        if (rateLimiter is not null)
            slots.Add(new AdmissionLimiterSlot(rateLimiter, scope, "rate", RetainOnFailure: true));
        return new AdmissionRuleRuntime(slots.ToArray());
    }

    internal void AppendTo(AdmissionLimiterSlot[] destination, ref int count)
    {
        foreach (var slot in _slots)
            destination[count++] = slot;
    }

    public void Dispose()
    {
        foreach (var slot in _slots)
            slot.Limiter.Dispose();
    }
}

internal sealed class AdmissionPartitionPool : IDisposable
{
    private readonly Func<SharpLinkAdmissionContext, string?> _selector;
    private readonly SharpLinkPartitionAdmissionOptions _options;
    private readonly int _queueLimit;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private readonly Dictionary<AdmissionPartitionKey, AdmissionPartitionEntry> _entries = [];
    private bool _hasIdleExpiryHint;
    private long _earliestIdleSince;
    private long _reclaimScanCount;
    private long _reclaimEntriesVisited;
    private int _disposed;

    internal AdmissionPartitionPool(
        Func<SharpLinkAdmissionContext, string?> selector,
        SharpLinkPartitionAdmissionOptions options,
        int queueLimit,
        TimeProvider timeProvider)
    {
        _selector = selector;
        _options = options.CloneValidated();
        _queueLimit = queueLimit;
        _timeProvider = timeProvider;
    }

    internal AdmissionPartitionLease? TryAcquire(SharpLinkAdmissionContext context)
    {
        var selected = _selector(context);
        if (selected is { Length: > 256 })
            throw new InvalidOperationException("Admission partition keys cannot exceed 256 characters.");
        var key = string.IsNullOrEmpty(selected)
            ? AdmissionPartitionKey.Default
            : AdmissionPartitionKey.ForUser(selected);

        List<AdmissionRuleRuntime>? evicted = null;
        AdmissionPartitionEntry entry;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return null;
            if (!_entries.TryGetValue(key, out entry!))
            {
                evicted = ReclaimIdleEntriesIfDue(_timeProvider.GetTimestamp());
                if (_entries.Count >= _options.MaxPartitions)
                    return null;
                entry = new AdmissionPartitionEntry(
                    AdmissionRuleRuntime.Create(_options, _queueLimit, "partition"));
                _entries.Add(key, entry);
                SharpLinkTelemetry.AddAdmissionActivePartitions(1);
            }
            entry.References++;
            entry.IsIdle = false;
        }
        DisposeRules(evicted);
        return new AdmissionPartitionLease(this, entry);
    }

    internal void Release(AdmissionPartitionEntry entry)
    {
        List<AdmissionRuleRuntime>? evicted = null;
        lock (_gate)
        {
            entry.References--;
            if (Volatile.Read(ref _disposed) != 0)
                return;

            var now = _timeProvider.GetTimestamp();
            if (entry.References == 0)
            {
                entry.IdleSince = now;
                entry.IsIdle = true;
                if (!_hasIdleExpiryHint)
                {
                    _earliestIdleSince = now;
                    _hasIdleExpiryHint = true;
                }
            }
            evicted = ReclaimIdleEntriesIfDue(now);
        }
        DisposeRules(evicted);
    }

    private List<AdmissionRuleRuntime>? ReclaimIdleEntriesIfDue(long now)
    {
        if (!_hasIdleExpiryHint ||
            _timeProvider.GetElapsedTime(_earliestIdleSince, now) < _options.IdleTimeout)
        {
            return null;
        }
        return ReconcileExpiredIdleEntries(now);
    }

    private List<AdmissionRuleRuntime>? ReconcileExpiredIdleEntries(long now)
    {
        _reclaimScanCount++;
        List<AdmissionPartitionKey>? expiredKeys = null;
        var hasNextIdle = false;
        var nextIdleSince = 0L;
        var longestRemainingElapsed = TimeSpan.Zero;

        foreach (var pair in _entries)
        {
            _reclaimEntriesVisited++;
            var entry = pair.Value;
            if (entry.References != 0 || !entry.IsIdle)
                continue;

            var elapsed = _timeProvider.GetElapsedTime(entry.IdleSince, now);
            if (elapsed >= _options.IdleTimeout)
            {
                (expiredKeys ??= []).Add(pair.Key);
                continue;
            }

            if (!hasNextIdle || elapsed > longestRemainingElapsed)
            {
                hasNextIdle = true;
                nextIdleSince = entry.IdleSince;
                longestRemainingElapsed = elapsed;
            }
        }

        _hasIdleExpiryHint = hasNextIdle;
        _earliestIdleSince = hasNextIdle ? nextIdleSince : 0;
        if (expiredKeys is null)
            return null;

        var rules = new List<AdmissionRuleRuntime>(expiredKeys.Count);
        foreach (var key in expiredKeys)
        {
            rules.Add(_entries[key].Runtime);
            _entries.Remove(key);
            SharpLinkTelemetry.AddAdmissionActivePartitions(-1);
        }
        return rules;
    }

    internal int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    internal long ReclaimScanCount
    {
        get
        {
            lock (_gate)
                return _reclaimScanCount;
        }
    }

    internal long ReclaimEntriesVisited
    {
        get
        {
            lock (_gate)
                return _reclaimEntriesVisited;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        AdmissionRuleRuntime[] rules;
        lock (_gate)
        {
            rules = _entries.Values.Select(static entry => entry.Runtime).ToArray();
            var count = _entries.Count;
            _entries.Clear();
            _hasIdleExpiryHint = false;
            _earliestIdleSince = 0;
            if (count != 0)
                SharpLinkTelemetry.AddAdmissionActivePartitions(-count);
        }
        foreach (var rule in rules)
            rule.Dispose();
    }

    private static void DisposeRules(List<AdmissionRuleRuntime>? rules)
    {
        if (rules is null)
            return;
        foreach (var rule in rules)
            rule.Dispose();
    }

    private readonly record struct AdmissionPartitionKey(string? Value, bool IsDefault)
    {
        internal static AdmissionPartitionKey Default { get; } = new(null, true);
        internal static AdmissionPartitionKey ForUser(string value) => new(value, false);
    }
}

internal sealed class AdmissionPartitionEntry(AdmissionRuleRuntime runtime)
{
    internal AdmissionRuleRuntime Runtime { get; } = runtime;
    internal int References;
    internal long IdleSince;
    internal bool IsIdle;
}

internal sealed class AdmissionPartitionLease(
    AdmissionPartitionPool owner,
    AdmissionPartitionEntry entry) : IDisposable
{
    private AdmissionPartitionPool? _owner = owner;
    internal AdmissionRuleRuntime Runtime => entry.Runtime;
    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(entry);
}
