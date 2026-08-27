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
    private readonly Dictionary<AdmissionRuleStateKey, List<ConcurrencyStateEntry>> _concurrencyStates = [];
    private readonly Dictionary<AdmissionRuleStateKey, ResizableConcurrencyState> _publishedConcurrencyStates = [];
    private readonly Dictionary<AdmissionRateStateKey, RateStateEntry> _rateStates = [];
    private readonly Dictionary<AdmissionRuleStateKey, AdmissionRateState> _publishedRateStates = [];
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
    private long _concurrencyTargetVersion;
    private long _nextRateStateGeneration;
    private bool _hasPublishedConcurrencyLineage;
    private bool _hasPublishedRateLineage;
    private int _disposed;

    internal AdmissionStateKernel(TimeProvider timeProvider)
        => _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    internal TimeProvider TimeProvider => _timeProvider;

    internal CancellationToken DrainingToken => _draining.Token;

    internal bool IsDraining => _draining.IsCancellationRequested || Volatile.Read(ref _disposed) != 0;

    internal Action? BeforeReclaimedStateDisposalForTests { get; set; }

    internal Action<int, int>? AfterConcurrencyResizeForTests { get; set; }

    internal Action? ConcurrencyTargetTransitionObservedForTests { get; set; }

    internal int QueuedCalls => Volatile.Read(ref _queuedCalls);

    internal long QueuedBytes => Volatile.Read(ref _queuedBytes);

    internal int ActivePermits => Volatile.Read(ref _activePermits);

    internal long ReadStableConcurrencyTargetVersion()
    {
        var spinner = new SpinWait();
        while (true)
        {
            var version = Volatile.Read(ref _concurrencyTargetVersion);
            if ((version & 1L) == 0)
                return version;
            ConcurrencyTargetTransitionObservedForTests?.Invoke();
            spinner.SpinOnce();
        }
    }

    internal bool IsConcurrencyTargetVersionCurrent(long version)
        => Volatile.Read(ref _concurrencyTargetVersion) == version;

    internal void BeginConcurrencyTargetCommit()
    {
        var version = Interlocked.Increment(ref _concurrencyTargetVersion);
        if ((version & 1L) == 0)
            throw new InvalidOperationException("Admission concurrency target commit was already open.");
    }

    internal void CompleteConcurrencyTargetCommit()
    {
        var version = Interlocked.Increment(ref _concurrencyTargetVersion);
        if ((version & 1L) != 0)
            throw new InvalidOperationException("Admission concurrency target commit was not open.");
    }

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
            {
                var counts = new Dictionary<AdmissionRuleStateKey, (int Concurrency, int Rate)>();
                foreach (var pair in _concurrencyStates)
                    counts[pair.Key] = (pair.Value.Count, 0);
                foreach (var pair in _rateStates)
                {
                    counts.TryGetValue(pair.Key.Scope, out var current);
                    counts[pair.Key.Scope] = (current.Concurrency, current.Rate + 1);
                }

                var total = 0;
                foreach (var count in counts.Values)
                    total += Math.Max(count.Concurrency, count.Rate);
                return total;
            }
        }
    }

    internal int ConcurrencyStateCount
    {
        get
        {
            lock (_registryGate)
                return _concurrencyStates.Values.Sum(static entries => entries.Count);
        }
    }

    internal int RateStateCount
    {
        get
        {
            lock (_registryGate)
                return _rateStates.Count;
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

    internal void RecordPublishedConcurrencyLineage(SharpLinkAdmissionController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        lock (_registryGate)
        {
            ThrowIfDisposed();
            _hasPublishedConcurrencyLineage = true;
            _publishedConcurrencyStates.Clear();
            foreach (var binding in controller.RuleStateBindings)
            {
                if (binding.ConcurrencyState is not { } state)
                    continue;
                if (!_concurrencyStates.TryGetValue(binding.Key, out var entries))
                    throw new InvalidOperationException("Published admission concurrency state is no longer registered.");

                var registered = false;
                foreach (var entry in entries)
                {
                    if (!ReferenceEquals(entry.State, state))
                        continue;
                    registered = true;
                    break;
                }
                if (!registered)
                    throw new InvalidOperationException("Published admission concurrency state is no longer registered.");
                _publishedConcurrencyStates.Add(binding.Key, state);
            }
        }
    }

    /// <summary>
    /// Records only the rate states of the actual publication. Historical removed states and losing
    /// candidates remain registered only for their old program users and are never compatibility
    /// sources for a later re-enable.
    /// </summary>
    internal void RecordPublishedRateLineage(SharpLinkAdmissionController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        lock (_registryGate)
        {
            ThrowIfDisposed();
            _hasPublishedRateLineage = true;
            _publishedRateStates.Clear();
            foreach (var binding in controller.RuleStateBindings)
            {
                if (binding.RateState is not { } state)
                    continue;
                if (!TryFindRateEntryLocked(binding.Key, state, out _, out _))
                    throw new InvalidOperationException("Published admission rate state is no longer registered.");
                _publishedRateStates.Add(binding.Key, state);
            }
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

    internal AdmissionProgram CreateUpdateProgram(
        AdmissionProgram source,
        SharpLinkAdmissionControlOptions options,
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> manifests,
        out AdmissionUpdatePlan updatePlan)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!ReferenceEquals(source.Kernel, this))
            throw new InvalidOperationException("Admission update source belongs to a different state kernel.");
        if (IsDraining)
            throw new InvalidOperationException("Admission state is sealed for shutdown.");

        var controller = SharpLinkAdmissionController.CreateUpdate(
            this,
            source.Controller,
            options,
            manifests,
            _timeProvider,
            out updatePlan);
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
        string scope)
    {
        lock (_registryGate)
        {
            ThrowIfDisposed();
            var concurrency = options.Concurrency is { } concurrencyOptions
                ? AcquireCompatibleConcurrencyLocked(key, concurrencyOptions.PermitLimit)
                : null;
            var rate = options.RateLimit is not null
                ? AcquireCompatibleRateLocked(key, options)
                : null;
            var runtime = AdmissionRuleRuntime.CreateBound(concurrency, rate, scope);
            return new AdmissionRuleStateBinding(key, runtime, concurrency, rate);
        }
    }

    internal AdmissionRuleStateBinding AcquireRuleStateForUpdate(
        AdmissionRuleStateKey key,
        SharpLinkAdmissionRuleOptions options,
        AdmissionRuleRuntime? sourceRuntime,
        string scope,
        List<AdmissionConcurrencyResize> resizes,
        List<AdmissionRateTransition> rateTransitions)
    {
        lock (_registryGate)
        {
            ThrowIfDisposed();

            ResizableConcurrencyState? concurrency = null;
            if (options.Concurrency is { } concurrencyOptions)
            {
                if (sourceRuntime?.ConcurrencyState is { } sourceConcurrency)
                {
                    AddConcurrencyReferenceLocked(key, sourceConcurrency);
                    concurrency = sourceConcurrency;
                    if (sourceConcurrency.PermitLimit != concurrencyOptions.PermitLimit)
                    {
                        resizes.Add(new AdmissionConcurrencyResize(
                            sourceConcurrency,
                            concurrencyOptions.PermitLimit));
                    }
                }
                else
                {
                    concurrency = CreateConcurrencyLocked(key, concurrencyOptions.PermitLimit);
                }
            }

            var sourceRate = sourceRuntime?.RateState;
            AdmissionRateState? rate = null;
            if (options.RateLimit is not null)
            {
                var candidateDefinition = AdmissionRateStateDefinition.Create(options.RateLimit);
                if (sourceRate is not null && sourceRate.Definition == candidateDefinition)
                {
                    AddRateReferenceLocked(key, sourceRate);
                    rate = sourceRate;
                }
                else
                {
                    rate = CreateRateLocked(key, options, sourceRate);
                    if (sourceRate is not null)
                        rateTransitions.Add(new AdmissionRateTransition(sourceRate, rate));
                }
            }
            else if (sourceRate is not null)
            {
                rateTransitions.Add(new AdmissionRateTransition(sourceRate, null));
            }

            var runtime = AdmissionRuleRuntime.CreateBound(concurrency, rate, scope);
            return new AdmissionRuleStateBinding(key, runtime, concurrency, rate);
        }
    }

    internal AdmissionPartitionStateBinding AcquirePartitionState(
        AdmissionPartitionStateKey key,
        Func<SharpLinkAdmissionContext, string?> selector,
        SharpLinkPartitionAdmissionOptions options)
    {
        lock (_registryGate)
        {
            ThrowIfDisposed();
            if (_partitionStates.TryGetValue(key, out var existing))
            {
                existing.ProgramReferences++;
                return new AdmissionPartitionStateBinding(key, existing.Pool);
            }

            var pool = new AdmissionPartitionPool(selector, options, _timeProvider);
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
        if (!program.TryBeginReclaim())
            return;

        List<IDisposable>? dispose = null;
        TaskCompletionSource<bool>? programsDrained = null;
        lock (_registryGate)
        {
            if (!_programs.Remove(program))
                throw new InvalidOperationException("Admission program reclamation lost its registered program.");
            _retiredPrograms.Remove(program);
            ReleaseBindingsLocked(program.Controller, ref dispose);
            if (_programs.Count == 0)
                programsDrained = _programsDrained;
        }

        if (dispose is { Count: > 0 })
            BeforeReclaimedStateDisposalForTests?.Invoke();
        DisposeStates(dispose);
        program.Controller.DetachReclaimedState(program);
        program.CompleteReclaim();
        programsDrained?.TrySetResult(true);
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
            foreach (var entries in _concurrencyStates.Values)
                foreach (var entry in entries)
                    dispose.Add(entry.State);
            foreach (var entry in _rateStates.Values)
                dispose.Add(entry.State);
            foreach (var entry in _partitionStates.Values)
                dispose.Add(entry.Pool);
            _concurrencyStates.Clear();
            _publishedConcurrencyStates.Clear();
            _hasPublishedConcurrencyLineage = false;
            _rateStates.Clear();
            _publishedRateStates.Clear();
            _hasPublishedRateLineage = false;
            _partitionStates.Clear();
            _retiredPrograms.Clear();
        }
        DisposeStates(dispose);
        _draining.Dispose();
    }

    private ResizableConcurrencyState AcquireCompatibleConcurrencyLocked(
        AdmissionRuleStateKey key,
        int permitLimit)
    {
        if (_hasPublishedConcurrencyLineage)
        {
            if (_publishedConcurrencyStates.TryGetValue(key, out var published) &&
                published.PermitLimit == permitLimit)
            {
                AddConcurrencyReferenceLocked(key, published);
                return published;
            }
            return CreateConcurrencyLocked(key, permitLimit);
        }

        if (_concurrencyStates.TryGetValue(key, out var entries))
        {
            foreach (var entry in entries)
            {
                if (entry.State.PermitLimit != permitLimit)
                    continue;
                entry.ProgramReferences++;
                return entry.State;
            }
        }
        return CreateConcurrencyLocked(key, permitLimit);
    }

    private ResizableConcurrencyState CreateConcurrencyLocked(
        AdmissionRuleStateKey key,
        int permitLimit)
    {
        var state = new ResizableConcurrencyState(permitLimit, this);
        if (!_concurrencyStates.TryGetValue(key, out var entries))
        {
            entries = [];
            _concurrencyStates.Add(key, entries);
        }
        entries.Add(new ConcurrencyStateEntry(state, 1));
        return state;
    }

    private void AddConcurrencyReferenceLocked(
        AdmissionRuleStateKey key,
        ResizableConcurrencyState state)
    {
        if (!_concurrencyStates.TryGetValue(key, out var entries))
            throw new InvalidOperationException("Source admission concurrency state is no longer registered.");
        foreach (var entry in entries)
        {
            if (!ReferenceEquals(entry.State, state))
                continue;
            entry.ProgramReferences++;
            return;
        }
        throw new InvalidOperationException("Source admission concurrency state is no longer registered.");
    }

    private AdmissionRateState AcquireCompatibleRateLocked(
        AdmissionRuleStateKey scope,
        SharpLinkAdmissionRuleOptions options)
    {
        var definition = AdmissionRateStateDefinition.Create(options.RateLimit);
        if (_hasPublishedRateLineage)
        {
            if (_publishedRateStates.TryGetValue(scope, out var published) &&
                published.Definition == definition)
            {
                AddRateReferenceLocked(scope, published);
                return published;
            }
            return CreateRateLocked(scope, options, transitionSource: null);
        }

        foreach (var pair in _rateStates)
        {
            if (pair.Key.Scope != scope || pair.Key.Definition != definition)
                continue;
            pair.Value.ProgramReferences++;
            pair.Value.RetainedLineageAnchor = false;
            return pair.Value.State;
        }
        return CreateRateLocked(scope, options, transitionSource: null);
    }

    private AdmissionRateState CreateRateLocked(
        AdmissionRuleStateKey scope,
        SharpLinkAdmissionRuleOptions options,
        AdmissionRateState? transitionSource)
    {
        var definition = AdmissionRateStateDefinition.Create(options.RateLimit);
        var key = new AdmissionRateStateKey(scope, definition, ++_nextRateStateGeneration);
        var state = AdmissionRateState.Create(options, _timeProvider, transitionSource);
        _rateStates.Add(key, new RateStateEntry(state, 1));
        return state;
    }

    private void AddRateReferenceLocked(AdmissionRuleStateKey scope, AdmissionRateState state)
    {
        if (!TryFindRateEntryLocked(scope, state, out _, out var entry))
            throw new InvalidOperationException("Source admission rate state is no longer registered.");
        entry.ProgramReferences++;
        entry.RetainedLineageAnchor = false;
    }

    private bool TryFindRateEntryLocked(
        AdmissionRuleStateKey scope,
        AdmissionRateState state,
        out AdmissionRateStateKey key,
        out RateStateEntry entry)
    {
        foreach (var pair in _rateStates)
        {
            if (pair.Key.Scope != scope || !ReferenceEquals(pair.Value.State, state))
                continue;
            key = pair.Key;
            entry = pair.Value;
            return true;
        }
        key = default;
        entry = null!;
        return false;
    }

    private bool HasOtherRateStateInLineageLocked(AdmissionRateState state)
    {
        foreach (var pair in _rateStates)
        {
            if (ReferenceEquals(pair.Value.State, state))
                continue;
            if (ReferenceEquals(pair.Value.State.Lineage, state.Lineage))
                return true;
        }
        return false;
    }

    private void CollectUnreferencedRateAnchorsLocked(ref List<IDisposable>? dispose)
    {
        List<AdmissionRateStateKey>? remove = null;
        foreach (var pair in _rateStates)
        {
            var entry = pair.Value;
            if (entry.ProgramReferences != 0 ||
                !entry.RetainedLineageAnchor ||
                HasOtherRateStateInLineageLocked(entry.State))
            {
                continue;
            }
            (remove ??= []).Add(pair.Key);
        }
        if (remove is null)
            return;

        foreach (var key in remove)
        {
            var entry = _rateStates[key];
            if (_publishedRateStates.TryGetValue(key.Scope, out var published) &&
                ReferenceEquals(published, entry.State))
            {
                _publishedRateStates.Remove(key.Scope);
            }
            _rateStates.Remove(key);
            (dispose ??= []).Add(entry.State);
        }
    }

    private void ReleaseBindingsLocked(
        SharpLinkAdmissionController controller,
        ref List<IDisposable>? dispose)
    {
        foreach (var binding in controller.RuleStateBindings)
        {
            if (binding.ConcurrencyState is { } concurrency &&
                _concurrencyStates.TryGetValue(binding.Key, out var entries))
            {
                for (var index = 0; index < entries.Count; index++)
                {
                    var entry = entries[index];
                    if (!ReferenceEquals(entry.State, concurrency))
                        continue;
                    if (--entry.ProgramReferences < 0)
                        throw new InvalidOperationException("Admission concurrency state reference count underflowed.");
                    if (entry.ProgramReferences == 0)
                    {
                        if (_publishedConcurrencyStates.TryGetValue(binding.Key, out var published) &&
                            ReferenceEquals(published, entry.State))
                        {
                            _publishedConcurrencyStates.Remove(binding.Key);
                        }
                        entries.RemoveAt(index);
                        if (entries.Count == 0)
                            _concurrencyStates.Remove(binding.Key);
                        (dispose ??= []).Add(entry.State);
                    }
                    break;
                }
            }

            if (binding.RateState is { } rate &&
                TryFindRateEntryLocked(binding.Key, rate, out var rateKey, out var rateEntry))
            {
                if (--rateEntry.ProgramReferences < 0)
                    throw new InvalidOperationException("Admission rate state reference count underflowed.");
                if (rateEntry.ProgramReferences == 0)
                {
                    var isPublished = _publishedRateStates.TryGetValue(binding.Key, out var published) &&
                                      ReferenceEquals(published, rateEntry.State);
                    if (isPublished && HasOtherRateStateInLineageLocked(rateEntry.State))
                    {
                        rateEntry.RetainedLineageAnchor = true;
                    }
                    else
                    {
                        if (isPublished)
                            _publishedRateStates.Remove(binding.Key);
                        _rateStates.Remove(rateKey);
                        (dispose ??= []).Add(rateEntry.State);
                    }
                }
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

        CollectUnreferencedRateAnchorsLocked(ref dispose);
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

    private sealed class ConcurrencyStateEntry(
        ResizableConcurrencyState state,
        int programReferences)
    {
        internal ResizableConcurrencyState State { get; } = state;
        internal int ProgramReferences = programReferences;
    }

    private sealed class RateStateEntry(AdmissionRateState state, int programReferences)
    {
        internal AdmissionRateState State { get; } = state;
        internal int ProgramReferences = programReferences;
        internal bool RetainedLineageAnchor;
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
    AdmissionRateStateDefinition Rate)
{
    internal static AdmissionRuleStateDefinition Create(SharpLinkAdmissionRuleOptions options)
        => new(
            options.Concurrency?.PermitLimit ?? 0,
            AdmissionRateStateDefinition.Create(options.RateLimit));
}

internal readonly record struct AdmissionRuleStateKey(
    AdmissionRuleStateScope Scope,
    long ContractId,
    long MethodId)
{
    internal static AdmissionRuleStateKey Global { get; } =
        new(AdmissionRuleStateScope.Global, 0, 0);

    internal static AdmissionRuleStateKey Contract(long contractId)
        => new(AdmissionRuleStateScope.Contract, contractId, 0);

    internal static AdmissionRuleStateKey Method(long contractId, long methodId)
        => new(AdmissionRuleStateScope.Method, contractId, methodId);
}

internal readonly record struct AdmissionRateStateKey(
    AdmissionRuleStateKey Scope,
    AdmissionRateStateDefinition Definition,
    long Generation);

internal readonly record struct AdmissionPartitionStateKey(
    Func<SharpLinkAdmissionContext, string?> Selector,
    AdmissionRuleStateDefinition Definition,
    int MaxPartitions,
    long IdleTimeoutTicks)
{
    internal static AdmissionPartitionStateKey Create(
        Func<SharpLinkAdmissionContext, string?> selector,
        SharpLinkPartitionAdmissionOptions options)
        => new(
            selector,
            AdmissionRuleStateDefinition.Create(options),
            options.MaxPartitions,
            options.IdleTimeout.Ticks);
}

internal readonly record struct AdmissionRuleStateBinding(
    AdmissionRuleStateKey Key,
    AdmissionRuleRuntime Runtime,
    ResizableConcurrencyState? ConcurrencyState,
    AdmissionRateState? RateState);

internal readonly record struct AdmissionPartitionStateBinding(
    AdmissionPartitionStateKey Key,
    AdmissionPartitionPool Pool);

internal readonly record struct AdmissionConcurrencyResize(
    ResizableConcurrencyState State,
    int PermitLimit);

internal readonly record struct AdmissionRateTransition(
    AdmissionRateState Source,
    AdmissionRateState? Target);

/// <summary>Prepared transition whose only live mutations are committed inside publication serialization.</summary>
internal sealed class AdmissionUpdatePlan
{
    private readonly AdmissionConcurrencyResize[] _resizes;
    private readonly AdmissionRateTransition[] _rateTransitions;
    private int _committed;

    internal AdmissionUpdatePlan(
        IEnumerable<AdmissionConcurrencyResize> resizes,
        IEnumerable<AdmissionRateTransition> rateTransitions)
    {
        _resizes = [.. resizes];
        _rateTransitions = [.. rateTransitions];
        foreach (var resize in _resizes)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resize.PermitLimit);
    }

    internal int ResizeCount => _resizes.Length;

    internal int RateTransitionCount => _rateTransitions.Length;

    internal void Commit(Action<int, int>? afterResize = null)
    {
        if (Interlocked.Exchange(ref _committed, 1) != 0)
            throw new InvalidOperationException("Admission update plan was committed more than once.");

        for (var index = 0; index < _resizes.Length; index++)
        {
            var resize = _resizes[index];
            resize.State.Resize(resize.PermitLimit);
            afterResize?.Invoke(index, _resizes.Length);
        }

        foreach (var transition in _rateTransitions)
            transition.Source.CommitTransitionTo(transition.Target);
    }
}
