using System.Threading.RateLimiting;

namespace SharpLink.Server;

/// <summary>
/// Immutable admission policy/binding for one program generation. Mutable limiter, queue, permit,
/// and partition state is owned by the stable server-scoped <see cref="AdmissionStateKernel"/>.
/// </summary>
internal sealed class SharpLinkAdmissionController : IAsyncDisposable
{
    private readonly AdmissionStateKernel _kernel;
    private AdmissionRuleRuntime? _global;
    private FrozenDictionary<long, AdmissionRuleRuntime> _contracts;
    private FrozenDictionary<(long ContractId, long MethodId), AdmissionRuleRuntime> _methods;
    private readonly int _maxQueuedCalls;
    private readonly long _maxQueuedBytes;
    private readonly TimeSpan _maxQueueDelay;
    private readonly bool _queueOneWayCalls;
    private readonly TimeProvider _timeProvider;
    private AdmissionPartitionPool? _partitions;
    private AdmissionRuleStateBinding[] _ruleStateBindings;
    private AdmissionPartitionStateBinding? _partitionStateBinding;
    private readonly bool _ownsKernel;
    private AdmissionProgram? _program;

    private SharpLinkAdmissionController(
        AdmissionStateKernel kernel,
        int maxQueuedCalls,
        long maxQueuedBytes,
        TimeSpan maxQueueDelay,
        bool queueOneWayCalls,
        AdmissionRuleRuntime? global,
        FrozenDictionary<long, AdmissionRuleRuntime> contracts,
        FrozenDictionary<(long ContractId, long MethodId), AdmissionRuleRuntime> methods,
        AdmissionPartitionPool? partitions,
        AdmissionRuleStateBinding[] ruleStateBindings,
        AdmissionPartitionStateBinding? partitionStateBinding,
        TimeProvider timeProvider,
        bool ownsKernel)
    {
        _kernel = kernel;
        _maxQueuedCalls = maxQueuedCalls;
        _maxQueuedBytes = maxQueuedBytes;
        _maxQueueDelay = maxQueueDelay;
        _queueOneWayCalls = queueOneWayCalls;
        _timeProvider = timeProvider;
        _global = global;
        _contracts = contracts;
        _methods = methods;
        _partitions = partitions;
        _ruleStateBindings = ruleStateBindings;
        _partitionStateBinding = partitionStateBinding;
        _ownsKernel = ownsKernel;
    }

    internal static SharpLinkAdmissionController CreateDisabled(TimeProvider? timeProvider = null)
    {
        timeProvider ??= TimeProvider.System;
        var kernel = new AdmissionStateKernel(timeProvider);
        return new SharpLinkAdmissionController(
            kernel,
            0,
            0,
            TimeSpan.Zero,
            queueOneWayCalls: false,
            global: null,
            FrozenDictionary<long, AdmissionRuleRuntime>.Empty,
            FrozenDictionary<(long ContractId, long MethodId), AdmissionRuleRuntime>.Empty,
            partitions: null,
            [],
            partitionStateBinding: null,
            timeProvider,
            ownsKernel: true);
    }

    internal static SharpLinkAdmissionController Create(
        SharpLinkAdmissionControlOptions options,
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> manifests,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(manifests);
        timeProvider ??= TimeProvider.System;
        var kernel = new AdmissionStateKernel(timeProvider);
        try
        {
            return Create(kernel, options, manifests, timeProvider, ownsKernel: true);
        }
        catch
        {
            SharpLinkAsyncCleanup.DisposeSynchronously(kernel);
            throw;
        }
    }

    internal static SharpLinkAdmissionController Create(
        AdmissionStateKernel kernel,
        SharpLinkAdmissionControlOptions options,
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> manifests,
        TimeProvider timeProvider,
        bool ownsKernel)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentNullException.ThrowIfNull(timeProvider);
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
        var bindings = new List<AdmissionRuleStateBinding>(1 + contractOptions.Count + methodOptions.Count);
        AdmissionPartitionStateBinding? partitionBinding = null;
        try
        {
            if (options.Global.HasLimit)
            {
                var binding = kernel.AcquireRuleState(
                    AdmissionRuleStateKey.Global(options.Global, options.MaxQueuedCalls),
                    options.Global,
                    options.MaxQueuedCalls,
                    "global");
                bindings.Add(binding);
                global = binding.Runtime;
            }

            foreach (var pair in contractOptions)
            {
                var binding = kernel.AcquireRuleState(
                    AdmissionRuleStateKey.Contract(pair.Key, pair.Value, options.MaxQueuedCalls),
                    pair.Value,
                    options.MaxQueuedCalls,
                    "contract");
                bindings.Add(binding);
                contractRules.Add(pair.Key, binding.Runtime);
            }

            foreach (var pair in methodOptions)
            {
                var binding = kernel.AcquireRuleState(
                    AdmissionRuleStateKey.Method(pair.Key.Item1, pair.Key.Item2, pair.Value, options.MaxQueuedCalls),
                    pair.Value,
                    options.MaxQueuedCalls,
                    "method");
                bindings.Add(binding);
                methodRules.Add(pair.Key, binding.Runtime);
            }

            AdmissionPartitionPool? partitions = null;
            if (options.Partition is { } partition)
            {
                var selector = options.PartitionSelector!;
                partitionBinding = kernel.AcquirePartitionState(
                    AdmissionPartitionStateKey.Create(selector, partition, options.MaxQueuedCalls),
                    selector,
                    partition,
                    options.MaxQueuedCalls);
                partitions = partitionBinding.Value.Pool;
            }

            return new SharpLinkAdmissionController(
                kernel,
                options.MaxQueuedCalls,
                options.MaxQueuedBytes,
                options.MaxQueueDelay,
                options.QueueOneWayCalls,
                global,
                contractRules.ToFrozenDictionary(),
                methodRules.ToFrozenDictionary(),
                partitions,
                [.. bindings],
                partitionBinding,
                timeProvider,
                ownsKernel);
        }
        catch
        {
            var rollback = new SharpLinkAdmissionController(
                kernel,
                options.MaxQueuedCalls,
                options.MaxQueuedBytes,
                options.MaxQueueDelay,
                options.QueueOneWayCalls,
                global,
                contractRules.ToFrozenDictionary(),
                methodRules.ToFrozenDictionary(),
                partitionBinding?.Pool,
                [.. bindings],
                partitionBinding,
                timeProvider,
                ownsKernel: false);
            kernel.ReleaseUnpublishedBindings(rollback);
            throw;
        }
    }

    internal AdmissionStateKernel Kernel => _kernel;

    internal AdmissionProgram? Program => Volatile.Read(ref _program);

    internal bool IsEnabled
        => _global is not null || _contracts.Count != 0 || _methods.Count != 0 || _partitions is not null;

    internal IReadOnlyList<AdmissionRuleStateBinding> RuleStateBindings => _ruleStateBindings;

    internal AdmissionPartitionStateBinding? PartitionStateBinding => _partitionStateBinding;

    internal AdmissionRuleRuntime? GlobalStateForTests => _global;

    internal AdmissionRuleRuntime? ContractStateForTests(long contractId)
        => _contracts.GetValueOrDefault(contractId);

    internal AdmissionRuleRuntime? MethodStateForTests(long contractId, long methodId)
        => _methods.GetValueOrDefault((contractId, methodId));

    internal AdmissionPartitionPool? PartitionStateForTests => _partitions;

    internal void AttachProgram(AdmissionProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (Interlocked.CompareExchange(ref _program, program, null) is not null)
            throw new InvalidOperationException("Admission policy binding already belongs to a program generation.");
    }

    internal void DetachReclaimedState(AdmissionProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _program, null, program), program))
        {
            throw new InvalidOperationException(
                "Admission program/controller ownership was not intact during reclamation.");
        }

        _global = null;
        _contracts = FrozenDictionary<long, AdmissionRuleRuntime>.Empty;
        _methods = FrozenDictionary<(long ContractId, long MethodId), AdmissionRuleRuntime>.Empty;
        _partitions = null;
        _ruleStateBindings = [];
        _partitionStateBinding = null;
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
        if (_kernel.IsDraining)
            return ValueTask.FromResult(AdmissionDecision.Reject("draining", SharpLinkErrorCode.Unavailable));

        AdmissionPartitionLease? partitionLease = null;
        if (_partitions is not null)
        {
            partitionLease = _partitions.TryAcquire(context);
            if (partitionLease is null)
                return ValueTask.FromResult(AdmissionDecision.Reject("partition_capacity"));
        }

        var request = CreateRequest(context, partitionLease);
        if (request.TryAcquire(_kernel, out var lease, out var failedSlot))
            return ValueTask.FromResult(AdmissionDecision.Accept(lease!));

        if (!allowQueue || _maxQueuedCalls == 0)
        {
            request.Dispose();
            return ValueTask.FromResult(AdmissionDecision.Reject(failedSlot.Reason, failedSlot.Scope));
        }

        if (!_kernel.TryReserveQueue(retainedBytes, _maxQueuedCalls, _maxQueuedBytes, out var queueReason))
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

    internal void StopAccepting() => _kernel.StopAccepting();

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
        var queueDeadline = RpcDeadline.Create(
            _maxQueueDelay,
            started,
            _timeProvider.TimestampFrequency);
        var deadlineLimitsWait =
            deadline.HasValue && deadline.WouldExpireBeforeOrAt(_maxQueueDelay, _timeProvider);
        var waitDeadline = deadlineLimitsWait ? deadline : queueDeadline;
        var waitDelay = waitDeadline.GetRemaining(_timeProvider);
        using var timeoutCancellation = waitDelay <= TimeSpan.Zero
            ? new CancellationTokenSource()
            : new CancellationTokenSource(waitDelay, _timeProvider);
        if (waitDelay <= TimeSpan.Zero)
            timeoutCancellation.Cancel();
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _kernel.DrainingToken,
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
                    _kernel.IsDraining && !cancellationToken.IsCancellationRequested)
                {
                    if (waitDeadline.IsExpired(_timeProvider))
                        return RejectExpiredAdmissionWait(failedSlot, deadlineLimitsWait);
                    return AdmissionDecision.Reject("draining", SharpLinkErrorCode.Unavailable);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    if (waitDeadline.IsExpired(_timeProvider))
                        return RejectExpiredAdmissionWait(failedSlot, deadlineLimitsWait);
                    throw;
                }
                catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
                {
                    return RejectExpiredAdmissionWait(failedSlot, deadlineLimitsWait);
                }

                if (waitDeadline.IsExpired(_timeProvider))
                {
                    waitedLease.Dispose();
                    return RejectExpiredAdmissionWait(failedSlot, deadlineLimitsWait);
                }
                if (_kernel.IsDraining)
                {
                    waitedLease.Dispose();
                    return AdmissionDecision.Reject("draining", SharpLinkErrorCode.Unavailable);
                }
                if (cancellationToken.IsCancellationRequested)
                {
                    waitedLease.Dispose();
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (!waitedLease.IsAcquired)
                {
                    waitedLease.Dispose();
                    return AdmissionDecision.Reject(failedSlot.Reason, failedSlot.Scope);
                }
                if (request.TryAcquireUsing(
                        _kernel,
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
            _kernel.ReleaseQueue(retainedBytes);
            SharpLinkTelemetry.RecordAdmissionQueueDuration(
                _timeProvider.GetElapsedTime(started));
            request.Dispose();
        }
    }

    private static AdmissionDecision RejectExpiredAdmissionWait(
        AdmissionLimiterSlot failedSlot,
        bool deadlineLimitsWait)
        => deadlineLimitsWait
            ? AdmissionDecision.Reject("deadline", SharpLinkErrorCode.DeadlineExceeded)
            : AdmissionDecision.Reject(failedSlot.Reason, failedSlot.Scope);

    internal bool TryReserveAdditionalQueuedBytes(int retainedBytes)
        => _kernel.TryReserveAdditionalQueuedBytes(retainedBytes, _maxQueuedBytes);

    internal void ReleaseAdditionalQueuedBytes(int retainedBytes)
        => _kernel.ReleaseAdditionalQueuedBytes(retainedBytes);

    internal int ActivePermits => _kernel.ActivePermits;
    internal int QueuedCalls => _kernel.QueuedCalls;
    internal long QueuedBytes => _kernel.QueuedBytes;
    internal int ActivePartitions => _partitions?.Count ?? 0;
    internal bool QueueOneWayCalls => _queueOneWayCalls;

    public ValueTask DisposeAsync()
        => _ownsKernel ? _kernel.DisposeAsync() : ValueTask.CompletedTask;
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
    private AdmissionStateKernel? _owner;
    private RateLimitLease? _singleLease;
    private RateLimitLease[]? _leases;
    private AdmissionPartitionLease? _partition;

    internal AdmissionLease(
        AdmissionStateKernel owner,
        RateLimitLease singleLease,
        AdmissionPartitionLease? partition)
    {
        _owner = owner;
        _singleLease = singleLease;
        _partition = partition;
        owner.OnLeaseCreated();
    }

    internal AdmissionLease(
        AdmissionStateKernel owner,
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
        AdmissionStateKernel owner,
        out AdmissionLease? admissionLease,
        out AdmissionLimiterSlot failedSlot)
        => TryAcquireCore(owner, null, null, out admissionLease, out failedSlot);

    internal bool TryAcquireUsing(
        AdmissionStateKernel owner,
        RateLimiter suppliedLimiter,
        RateLimitLease suppliedLease,
        out AdmissionLease? admissionLease,
        out AdmissionLimiterSlot failedSlot)
        => TryAcquireCore(owner, suppliedLimiter, suppliedLease, out admissionLease, out failedSlot);

    private bool TryAcquireCore(
        AdmissionStateKernel owner,
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

/// <summary>Kernel-owned mutable limiter state for one explicit structural rule identity.</summary>
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

/// <summary>Kernel-owned partition namespace/state shared by compatible program generations.</summary>
internal sealed class AdmissionPartitionPool : IDisposable
{
    private readonly Func<SharpLinkAdmissionContext, string?> _selector;
    private readonly SharpLinkPartitionAdmissionOptions _options;
    private readonly int _queueLimit;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private readonly Dictionary<AdmissionPartitionKey, AdmissionPartitionEntry> _entries = [];
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
                evicted = ReclaimIdleEntries(_timeProvider.GetTimestamp(), stopAfterOne: true);
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
        List<AdmissionRuleRuntime>? evicted;
        lock (_gate)
        {
            if (--entry.References < 0)
                throw new InvalidOperationException("Admission partition reference count underflowed.");
            if (entry.References == 0)
            {
                entry.IdleSince = _timeProvider.GetTimestamp();
                entry.IsIdle = true;
            }
            evicted = ReclaimIdleEntries(_timeProvider.GetTimestamp(), stopAfterOne: true);
        }
        DisposeRules(evicted);
    }

    private List<AdmissionRuleRuntime>? ReclaimIdleEntries(long now, bool stopAfterOne)
    {
        List<AdmissionPartitionKey>? keys = null;
        foreach (var pair in _entries)
        {
            if (pair.Value.References != 0 || !pair.Value.IsIdle ||
                _timeProvider.GetElapsedTime(pair.Value.IdleSince, now) < _options.IdleTimeout)
            {
                continue;
            }
            (keys ??= []).Add(pair.Key);
            if (stopAfterOne)
                break;
        }
        if (keys is null)
            return null;
        var rules = new List<AdmissionRuleRuntime>(keys.Count);
        foreach (var key in keys)
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
