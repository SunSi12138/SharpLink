from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def replace_once(path: str, old: str, new: str) -> None:
    file = ROOT / path
    text = file.read_text()
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected exactly one match, found {count}: {old[:100]!r}")
    file.write_text(text.replace(old, new, 1))


# Move the immutable FixedWindow policy fields directly onto the already-existing AdmissionRateState.
policy = ROOT / "src/SharpLink.Server/Admission/DynamicFixedWindowRateLimiter.cs"
if not policy.exists():
    raise RuntimeError("DynamicFixedWindowRateLimiter.cs missing")
policy.unlink()

fixed_partial = ROOT / "src/SharpLink.Server/Admission/AdmissionRateState.FixedWindow.cs"
fixed_partial.write_text(r'''using System.Threading.RateLimiting;

namespace SharpLink.Server;

internal enum DynamicFixedWindowActivationMode
{
    Immediate,
    NextWindowBoundary
}

/// <summary>Immutable FixedWindow policy view carried directly by one AdmissionProgram rate state.</summary>
internal sealed partial class AdmissionRateState
{
    private readonly Counter? _fixedCounter;
    private readonly long _fixedSequence;
    private readonly long _fixedWindowTimestampTicks;
    private readonly DynamicFixedWindowActivationMode _fixedActivationMode;
    private int _fixedPreActivationLimit;
    private long _fixedActivationBoundary;
    private int _fixedCommitted;
    private int _fixedPublished;
    private int _fixedDisposed;

    private AdmissionRateState(
        AdmissionRateStateDefinition definition,
        TimeProvider timeProvider)
    {
        _definition = definition;
        _lineage = new AdmissionRateTransitionLineage();
        var window = TimeSpan.FromTicks(definition.PeriodTicks);
        _fixedCounter = new Counter(definition.Limit, window, timeProvider);
        _fixedSequence = 1;
        _fixedWindowTimestampTicks = _fixedCounter.ToTimestampTicks(definition.PeriodTicks);
        _fixedActivationMode = DynamicFixedWindowActivationMode.Immediate;
        _fixedPreActivationLimit = definition.Limit;
        _fixedCommitted = 1;
        _fixedPublished = 1;
    }

    private AdmissionRateState(
        AdmissionRateStateDefinition definition,
        Counter counter,
        long sequence,
        long windowTimestampTicks,
        DynamicFixedWindowActivationMode activationMode,
        AdmissionRateTransitionLineage lineage)
    {
        _definition = definition;
        _lineage = lineage;
        _fixedCounter = counter;
        _fixedSequence = sequence;
        _fixedWindowTimestampTicks = windowTimestampTicks;
        _fixedActivationMode = activationMode;
    }

    internal AdmissionRateState? FixedWindowForTests => _fixedCounter is null ? null : this;
    internal int PermitLimit => _definition.Limit;
    internal TimeSpan Window => TimeSpan.FromTicks(_definition.PeriodTicks);
    internal DynamicFixedWindowActivationMode ActivationModeForTests => _fixedActivationMode;
    internal long ConsumedForTests => _fixedCounter!.Consumed;
    internal int ActiveLimitForTests => _fixedCounter!.ActiveLimit;
    internal int QueuedLimitForTests => _fixedCounter!.QueuedLimit;
    internal TimeSpan ActiveWindowForTests => _fixedCounter!.ActiveWindow;
    internal bool HasPendingWindowForTests => _fixedCounter!.HasPendingTarget;
    internal long CounterIdentityForTests => _fixedCounter!.Identity;

    private AdmissionRateState CreateFixedSuccessor(
        AdmissionRateStateDefinition definition,
        DynamicFixedWindowActivationMode? activationMode)
    {
        ThrowIfFixedDisposed();
        var counter = _fixedCounter ??
            throw new InvalidOperationException("FixedWindow successor requires a stable counter.");
        var windowTimestampTicks = counter.ToTimestampTicks(definition.PeriodTicks);
        var resolvedActivation = counter.ResolveActivation(windowTimestampTicks, activationMode);
        return counter.CreateSuccessor(
            definition,
            _lineage,
            windowTimestampTicks,
            resolvedActivation);
    }

    private void CommitFixedTransitionTo(AdmissionRateState target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ThrowIfFixedDisposed();
        if (target._fixedActivationMode == DynamicFixedWindowActivationMode.Immediate &&
            target._fixedWindowTimestampTicks != _fixedWindowTimestampTicks)
        {
            throw new InvalidOperationException(
                "Immediate FixedWindow updates may change PermitLimit only. Change Window with NextWindowBoundary activation.");
        }
        _fixedCounter!.CommitTransition(this, target);
    }

    private void OnFixedPublished()
    {
        ThrowIfFixedDisposed();
        _fixedCounter!.Publish(this);
    }

    private RateLimitLease AttemptAcquireFixed(int permitCount)
    {
        ValidateFixedPermitCount(permitCount);
        if (Volatile.Read(ref _fixedDisposed) != 0)
            return AdmissionRateLeases.Failed;
        _fixedCounter!.Publish(this);
        return _fixedCounter.AttemptAcquire(this);
    }

    private ValueTask<RateLimitLease> AcquireFixedAsync(
        int permitCount,
        CancellationToken cancellationToken)
    {
        ValidateFixedPermitCount(permitCount);
        if (Volatile.Read(ref _fixedDisposed) != 0)
            return ValueTask.FromResult<RateLimitLease>(AdmissionRateLeases.Failed);
        _fixedCounter!.Publish(this);
        return _fixedCounter.AcquireAsync(cancellationToken);
    }

    private void DisposeFixed()
    {
        if (Interlocked.Exchange(ref _fixedDisposed, 1) != 0)
            return;
        _fixedCounter!.ReleaseView();
    }

    private void FinalizeFixedForCommit(int preActivationLimit, long activationBoundary)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preActivationLimit);
        if (Interlocked.Exchange(ref _fixedCommitted, 1) != 0)
            throw new InvalidOperationException("FixedWindow policy view was committed more than once.");
        _fixedPreActivationLimit = preActivationLimit;
        _fixedActivationBoundary = activationBoundary;
    }

    private void MarkFixedPublishedLocked()
        => Volatile.Write(ref _fixedPublished, 1);

    private void ThrowIfFixedDisposed()
    {
        if (Volatile.Read(ref _fixedDisposed) != 0)
            throw new ObjectDisposedException(nameof(AdmissionRateState));
    }

    private static void ValidateFixedPermitCount(int permitCount)
    {
        if (permitCount != 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(permitCount),
                "Admission FixedWindow limiters acquire exactly one permit.");
        }
    }
}
''')

# Collapse the outer wrapper union to use the FixedWindow partial directly.
path = "src/SharpLink.Server/Admission/AdmissionLimiterState.cs"
replace_once(path, "internal sealed class AdmissionRateState : RateLimiter", "internal sealed partial class AdmissionRateState : RateLimiter")
replace_once(path, "    private readonly DynamicFixedWindowRateLimiter? _fixedWindow;\n", "")
replace_once(
    path,
    r'''    private AdmissionRateState(
        AdmissionRateStateDefinition definition,
        DynamicFixedWindowRateLimiter fixedWindow,
        AdmissionRateTransitionLineage lineage)
    {
        _definition = definition;
        _fixedWindow = fixedWindow;
        _lineage = lineage;
    }

''',
    "",
)
replace_once(
    path,
    "    internal int WaitingCount => _fixedWindow?.WaitingCount ?? _state!.WaitingCount;",
    "    internal int WaitingCount => _fixedCounter?.WaitingCount ?? _state!.WaitingCount;",
)
replace_once(path, "\n    internal DynamicFixedWindowRateLimiter? FixedWindowForTests => _fixedWindow;\n", "\n")
replace_once(path, "    internal void OnPublished() => _fixedWindow?.OnPublished();", "    internal void OnPublished()\n    {\n        if (_fixedCounter is not null)\n            OnFixedPublished();\n    }")
replace_once(
    path,
    r'''        if (canUseStableFixedWindow)
        {
            var fixedOptions = (SharpLinkFixedWindowLimitOptions)options.RateLimit!;
            var window = TimeSpan.FromTicks(definition.PeriodTicks);
            if (transitionSource?._fixedWindow is { } sourceFixed)
            {
                if (fixedOptions.UpdateActivation == DynamicFixedWindowActivationMode.Immediate &&
                    definition.PeriodTicks != transitionSource.Definition.PeriodTicks)
                {
                    throw new InvalidOperationException(
                        "Immediate FixedWindow updates may change PermitLimit only. Change Window with NextWindowBoundary activation.");
                }

                return new AdmissionRateState(
                    definition,
                    sourceFixed.CreateSuccessor(
                        definition.Limit,
                        window,
                        fixedOptions.UpdateActivation),
                    transitionSource._lineage);
            }

            return new AdmissionRateState(
                definition,
                new DynamicFixedWindowRateLimiter(definition.Limit, window, timeProvider),
                new AdmissionRateTransitionLineage());
        }
''',
    r'''        if (canUseStableFixedWindow)
        {
            var fixedOptions = (SharpLinkFixedWindowLimitOptions)options.RateLimit!;
            if (transitionSource?._fixedCounter is not null)
                return transitionSource.CreateFixedSuccessor(definition, fixedOptions.UpdateActivation);
            return new AdmissionRateState(definition, timeProvider);
        }
''',
)
replace_once(
    path,
    r'''        if (_fixedWindow is not null)
        {
            if (target?._fixedWindow is not null && ReferenceEquals(_lineage, target._lineage))
                _fixedWindow.CommitTransitionTo(target._fixedWindow);
            return;
        }
''',
    r'''        if (_fixedCounter is not null)
        {
            if (target?._fixedCounter is not null && ReferenceEquals(_lineage, target._lineage))
                CommitFixedTransitionTo(target);
            return;
        }
''',
)
replace_once(
    path,
    "    protected override RateLimitLease AttemptAcquireCore(int permitCount)\n        => _fixedWindow?.AttemptAcquire(permitCount) ?? _state!.AttemptAcquire(permitCount);",
    "    protected override RateLimitLease AttemptAcquireCore(int permitCount)\n        => _fixedCounter is not null\n            ? AttemptAcquireFixed(permitCount)\n            : _state!.AttemptAcquire(permitCount);",
)
replace_once(
    path,
    r'''    protected override ValueTask<RateLimitLease> AcquireAsyncCore(
        int permitCount,
        CancellationToken cancellationToken)
        => _fixedWindow?.AcquireAsync(permitCount, cancellationToken) ??
           _state!.AcquireAsync(permitCount, cancellationToken);
''',
    r'''    protected override ValueTask<RateLimitLease> AcquireAsyncCore(
        int permitCount,
        CancellationToken cancellationToken)
        => _fixedCounter is not null
            ? AcquireFixedAsync(permitCount, cancellationToken)
            : _state!.AcquireAsync(permitCount, cancellationToken);
''',
)
replace_once(
    path,
    r'''        _fixedWindow?.Dispose();
        _state?.Dispose();
''',
    r'''        if (_fixedCounter is not null)
            DisposeFixed();
        _state?.Dispose();
''',
)

# Retarget the shared counter from the redundant inner RateLimiter to AdmissionRateState.
counter = ROOT / "src/SharpLink.Server/Admission/DynamicFixedWindowRateLimiter.Counter.cs"
text = counter.read_text()
text = text.replace("internal sealed partial class DynamicFixedWindowRateLimiter", "internal sealed partial class AdmissionRateState", 1)
text = text.replace("DynamicFixedWindowRateLimiter", "AdmissionRateState")
text = text.replace("policy._published", "policy._fixedPublished")
text = text.replace("policy._committed", "policy._fixedCommitted")
text = text.replace("policy._activationMode", "policy._fixedActivationMode")
text = text.replace("policy._windowTimestampTicks", "policy._fixedWindowTimestampTicks")
text = text.replace("policy._preActivationLimit", "policy._fixedPreActivationLimit")
text = text.replace("policy._activationBoundary", "policy._fixedActivationBoundary")
text = text.replace("policy._sequence", "policy._fixedSequence")
text = text.replace("source._counter", "source._fixedCounter")
text = text.replace("target._counter", "target._fixedCounter")
text = text.replace("policy._counter", "policy._fixedCounter")
text = text.replace("target.FinalizeForCommit", "target.FinalizeFixedForCommit")
text = text.replace("policy.MarkPublishedLocked", "policy.MarkFixedPublishedLocked")
text = text.replace("policy._permitLimit", "policy._definition.Limit")

old = r'''        internal AdmissionRateState CreateSuccessor(
            int permitLimit,
            TimeSpan window,
            DynamicFixedWindowActivationMode activationMode)
        {
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                var sequence = checked(++_nextSequence);
                var windowTicks = ToTimestampTicks(window.Ticks);
                _references = checked(_references + 1);
                return new AdmissionRateState(
                    this,
                    sequence,
                    permitLimit,
                    windowTicks,
                    activationMode);
            }
        }
'''
new = r'''        internal AdmissionRateState CreateSuccessor(
            AdmissionRateStateDefinition definition,
            AdmissionRateTransitionLineage lineage,
            long windowTimestampTicks,
            DynamicFixedWindowActivationMode activationMode)
        {
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                var sequence = checked(++_nextSequence);
                _references = checked(_references + 1);
                return new AdmissionRateState(
                    definition,
                    this,
                    sequence,
                    windowTimestampTicks,
                    activationMode,
                    lineage);
            }
        }
'''
if text.count(old) != 1:
    raise RuntimeError(f"counter CreateSuccessor mismatch: {text.count(old)}")
text = text.replace(old, new, 1)
# Remove dead local completion helpers left after shared-waiter extraction.
start = text.index("        private static void CompleteGranted(AdmissionRateWaiter? waiter)\n")
end = text.index("        private static long SaturatingAdd", start)
text = text[:start] + text[end:]
counter_target = ROOT / "src/SharpLink.Server/Admission/AdmissionRateState.FixedWindowCounter.cs"
counter_target.write_text(text)
counter.unlink()

# Test-only helpers now return the already-existing AdmissionRateState wrapper.
for test in (ROOT / "test").rglob("*.cs"):
    text = test.read_text()
    if "DynamicFixedWindowRateLimiter" not in text:
        continue
    test.write_text(text.replace("DynamicFixedWindowRateLimiter", "AdmissionRateState"))

# No product/test source should retain the redundant inner limiter type.
for source in list((ROOT / "src").rglob("*.cs")) + list((ROOT / "test").rglob("*.cs")):
    if "DynamicFixedWindowRateLimiter" in source.read_text():
        raise RuntimeError(f"redundant FixedWindow policy type survived in {source}")

print("issue #410 FixedWindow policy collapse staged")
