from pathlib import Path


def load(path: str) -> str:
    return Path(path).read_text(encoding="utf-8")


def save(path: str, text: str) -> None:
    Path(path).write_text(text, encoding="utf-8")


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, found {count}")
    return text.replace(old, new, 1)


# Temporary top-level prototype selector. It is internal and copied with candidate options so the
# public API remains unchanged while Phase B1 evaluates actual server publication/routing.
path = "src/SharpLink.Server/Admission/SharpLinkAdmissionControlOptions.cs"
text = load(path)
text = replace_once(
    text,
    "    private SharpLinkPartitionAdmissionOptions? _partition;\n\n",
    "    private SharpLinkPartitionAdmissionOptions? _partition;\n\n"
    "    internal DynamicFixedWindowActivationMode? GlobalFixedWindowActivationModeForPrototype { get; set; }\n\n",
    "prototype activation property",
)
text = replace_once(
    text,
    "        Global.Validate();\n",
    "        Global.Validate();\n"
    "        if (GlobalFixedWindowActivationModeForPrototype is not null &&\n"
    "            Global.RateLimit is not SharpLinkFixedWindowLimitOptions)\n"
    "        {\n"
    "            throw new InvalidOperationException(\n"
    "                \"Dynamic FixedWindow prototype mode requires a Global FixedWindow rate policy.\");\n"
    "        }\n",
    "prototype validation",
)
text = replace_once(
    text,
    "            QueueOneWayCalls = QueueOneWayCalls,\n            _partitionSelector = _partitionSelector,",
    "            QueueOneWayCalls = QueueOneWayCalls,\n"
    "            GlobalFixedWindowActivationModeForPrototype = GlobalFixedWindowActivationModeForPrototype,\n"
    "            _partitionSelector = _partitionSelector,",
    "prototype clone",
)
save(path, text)


# AdmissionRateState becomes a lifecycle/registry wrapper that can own either the existing generic
# migration state or the specialized stable FixedWindow ledger. AdmissionRuleRuntime will bind the
# specialized limiter directly into the request slot so the wrapper does not tax the hot path.
path = "src/SharpLink.Server/Admission/AdmissionLimiterState.cs"
text = load(path)
start = text.index("internal sealed class AdmissionRateState : RateLimiter")
text = text[:start] + r'''internal sealed class AdmissionRateState : RateLimiter
{
    private readonly AdmissionDynamicRateState? _state;
    private readonly DynamicFixedWindowRateLimiter? _dynamicFixedWindow;
    private AdmissionRateStateDefinition _definition;

    private AdmissionRateState(AdmissionDynamicRateState state)
    {
        _state = state;
        _definition = state.Definition;
    }

    private AdmissionRateState(
        AdmissionRateStateDefinition definition,
        DynamicFixedWindowRateLimiter dynamicFixedWindow)
    {
        _definition = definition;
        _dynamicFixedWindow = dynamicFixedWindow;
    }

    internal AdmissionRateStateDefinition Definition => _definition;

    internal object Lineage => (object?)_state?.Lineage ?? this;

    internal int WaitingCount => _dynamicFixedWindow?.WaitingCount ?? _state!.WaitingCount;

    internal long TransitionDebtForDiagnostics => _state?.TransitionDebtForDiagnostics ?? 0;

    internal long TransitionBarrierExpiryForDiagnostics => _state?.TransitionBarrierExpiryForDiagnostics ?? 0;

    internal bool IsDynamicFixedWindow => _dynamicFixedWindow is not null;

    internal DynamicFixedWindowRateLimiter? DynamicFixedWindowForTests => _dynamicFixedWindow;

    internal RateLimiter LimiterForAdmission => _dynamicFixedWindow ?? this;

    internal static AdmissionRateState Create(
        SharpLinkAdmissionRuleOptions options,
        TimeProvider timeProvider,
        AdmissionRateState? transitionSource = null)
    {
        if (transitionSource is { IsDynamicFixedWindow: true })
        {
            throw new InvalidOperationException(
                "The Dynamic FixedWindow prototype cannot transition into the legacy migration path.");
        }

        var definition = AdmissionRateStateDefinition.Create(options.RateLimit);
        var state = new AdmissionDynamicRateState(
            definition,
            timeProvider,
            transitionSource?._state?.Lineage);
        return new AdmissionRateState(state);
    }

    internal static AdmissionRateState CreateDynamicFixedWindow(
        SharpLinkAdmissionRuleOptions options,
        TimeProvider timeProvider)
    {
        if (options.RateLimit is not SharpLinkFixedWindowLimitOptions fixedWindow)
            throw new InvalidOperationException("Dynamic FixedWindow state requires a FixedWindow definition.");
        var definition = AdmissionRateStateDefinition.Create(fixedWindow);
        var limiter = new DynamicFixedWindowRateLimiter(
            fixedWindow.PermitLimit,
            fixedWindow.Window,
            timeProvider);
        return new AdmissionRateState(definition, limiter);
    }

    internal void ApplyDynamicFixedWindowUpdate(
        AdmissionRateStateDefinition definition,
        DynamicFixedWindowActivationMode activationMode)
    {
        var limiter = _dynamicFixedWindow ??
            throw new InvalidOperationException("Rate state is not a Dynamic FixedWindow prototype state.");
        if (definition.Kind != AdmissionRateStateKind.FixedWindow)
            throw new InvalidOperationException("Dynamic FixedWindow update requires a FixedWindow definition.");

        limiter.Update(
            definition.Limit,
            TimeSpan.FromTicks(definition.PeriodTicks),
            activationMode);
        _definition = definition;
    }

    internal void CommitTransitionTo(AdmissionRateState? target)
    {
        if (_state is null)
        {
            if (target is null)
                return;
            throw new InvalidOperationException(
                "The Dynamic FixedWindow prototype does not support algorithm replacement in Phase B1.");
        }
        _state.CommitTransitionTo(target?._state);
    }

    public override TimeSpan? IdleDuration => null;

    public override RateLimiterStatistics? GetStatistics() => null;

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
        => _dynamicFixedWindow?.AttemptAcquire(permitCount) ?? _state!.AttemptAcquire(permitCount);

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(
        int permitCount,
        CancellationToken cancellationToken)
        => _dynamicFixedWindow?.AcquireAsync(permitCount, cancellationToken) ??
           _state!.AcquireAsync(permitCount, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
            return;
        _dynamicFixedWindow?.Dispose();
        _state?.Dispose();
    }
}
'''
save(path, text)


# Bind the specialized limiter directly into the request slot. Keep AdmissionRateState as the
# registry/lifecycle object so program reference accounting stays unchanged.
path = "src/SharpLink.Server/Admission/SharpLinkAdmissionController.cs"
text = load(path)
text = replace_once(
    text,
    "            _slots[index] = new AdmissionLimiterSlot(rate, scope, \"rate\", RetainOnFailure: true);",
    "            _slots[index] = new AdmissionLimiterSlot(\n"
    "                rate.LimiterForAdmission, scope, \"rate\", RetainOnFailure: true);",
    "direct specialized rate slot",
)
text = replace_once(
    text,
    "                    options.Global,\n                    \"global\");",
    "                    options.Global,\n"
    "                    \"global\",\n"
    "                    options.GlobalFixedWindowActivationModeForPrototype);",
    "global initial prototype binding",
)
text = replace_once(
    text,
    "        var rateTransitions = new List<AdmissionRateTransition>();\n",
    "        var rateTransitions = new List<AdmissionRateTransition>();\n"
    "        var fixedWindowUpdates = new List<AdmissionDynamicFixedWindowUpdate>();\n",
    "fixed update list",
)
text = replace_once(
    text,
    "                    \"global\",\n                    resizes,\n                    rateTransitions);",
    "                    \"global\",\n"
    "                    resizes,\n"
    "                    rateTransitions,\n"
    "                    fixedWindowUpdates,\n"
    "                    options.GlobalFixedWindowActivationModeForPrototype);",
    "global update prototype binding",
)
text = replace_once(
    text,
    "            updatePlan = new AdmissionUpdatePlan(resizes, rateTransitions, partitionUpdate);",
    "            updatePlan = new AdmissionUpdatePlan(\n"
    "                resizes, rateTransitions, partitionUpdate, fixedWindowUpdates);",
    "update plan fixed updates",
)
save(path, text)


# Kernel registry: default behavior is byte-for-byte the old #333 path. Only Global calls carrying
# the internal prototype selector create/reuse the stable specialized state.
path = "src/SharpLink.Server/Admission/AdmissionStateKernel.cs"
text = load(path)
text = replace_once(
    text,
    "    internal AdmissionRuleStateBinding AcquireRuleState(\n"
    "        AdmissionRuleStateKey key,\n"
    "        SharpLinkAdmissionRuleOptions options,\n"
    "        string scope)\n",
    "    internal AdmissionRuleStateBinding AcquireRuleState(\n"
    "        AdmissionRuleStateKey key,\n"
    "        SharpLinkAdmissionRuleOptions options,\n"
    "        string scope,\n"
    "        DynamicFixedWindowActivationMode? fixedWindowActivationMode = null)\n",
    "initial binding signature",
)
text = replace_once(
    text,
    "                ? AcquireCompatibleRateLocked(key, options)\n",
    "                ? AcquireCompatibleRateLocked(key, options, fixedWindowActivationMode)\n",
    "initial compatible rate",
)
text = replace_once(
    text,
    "        List<AdmissionConcurrencyResize> resizes,\n"
    "        List<AdmissionRateTransition> rateTransitions)\n",
    "        List<AdmissionConcurrencyResize> resizes,\n"
    "        List<AdmissionRateTransition> rateTransitions,\n"
    "        List<AdmissionDynamicFixedWindowUpdate>? fixedWindowUpdates = null,\n"
    "        DynamicFixedWindowActivationMode? fixedWindowActivationMode = null)\n",
    "update binding signature",
)
old_rate_block = '''            var sourceRate = sourceRuntime?.RateState;
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
'''
new_rate_block = '''            var sourceRate = sourceRuntime?.RateState;
            AdmissionRateState? rate = null;
            if (fixedWindowActivationMode is not null)
            {
                if (fixedWindowUpdates is null)
                    throw new InvalidOperationException("Dynamic FixedWindow update plan is unavailable.");
                if (options.RateLimit is not SharpLinkFixedWindowLimitOptions)
                {
                    throw new InvalidOperationException(
                        "Dynamic FixedWindow prototype updates require a FixedWindow target.");
                }
                var candidateDefinition = AdmissionRateStateDefinition.Create(options.RateLimit);
                if (sourceRate is null)
                {
                    rate = CreateRateLocked(
                        key, options, transitionSource: null, fixedWindowActivationMode);
                }
                else if (sourceRate.IsDynamicFixedWindow)
                {
                    AddRateReferenceLocked(key, sourceRate);
                    rate = sourceRate;
                    if (sourceRate.Definition != candidateDefinition)
                    {
                        fixedWindowUpdates.Add(new AdmissionDynamicFixedWindowUpdate(
                            sourceRate,
                            candidateDefinition,
                            fixedWindowActivationMode.Value));
                    }
                }
                else
                {
                    throw new InvalidOperationException(
                        "Dynamic FixedWindow Phase B1 must be enabled from the initial Global FixedWindow publication.");
                }
            }
            else if (options.RateLimit is not null)
            {
                if (sourceRate is { IsDynamicFixedWindow: true })
                {
                    throw new InvalidOperationException(
                        "Dynamic FixedWindow Phase B1 updates must keep an explicit activation mode.");
                }
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
'''
text = replace_once(text, old_rate_block, new_rate_block, "dynamic update rate block")
text = replace_once(
    text,
    "    private AdmissionRateState AcquireCompatibleRateLocked(\n"
    "        AdmissionRuleStateKey scope,\n"
    "        SharpLinkAdmissionRuleOptions options)\n",
    "    private AdmissionRateState AcquireCompatibleRateLocked(\n"
    "        AdmissionRuleStateKey scope,\n"
    "        SharpLinkAdmissionRuleOptions options,\n"
    "        DynamicFixedWindowActivationMode? fixedWindowActivationMode)\n",
    "compatible rate signature",
)
text = replace_once(
    text,
    "            if (_publishedRateStates.TryGetValue(scope, out var published) &&\n"
    "                published.Definition == definition)\n",
    "            if (_publishedRateStates.TryGetValue(scope, out var published) &&\n"
    "                published.Definition == definition &&\n"
    "                published.IsDynamicFixedWindow == (fixedWindowActivationMode is not null))\n",
    "published prototype compatibility",
)
text = replace_once(
    text,
    "            return CreateRateLocked(scope, options, transitionSource: null);\n",
    "            return CreateRateLocked(\n"
    "                scope, options, transitionSource: null, fixedWindowActivationMode);\n",
    "published prototype create",
)
text = replace_once(
    text,
    "            if (pair.Key.Scope != scope || pair.Key.Definition != definition)\n"
    "                continue;\n",
    "            if (pair.Key.Scope != scope || pair.Key.Definition != definition ||\n"
    "                pair.Value.State.IsDynamicFixedWindow != (fixedWindowActivationMode is not null))\n"
    "            {\n"
    "                continue;\n"
    "            }\n",
    "historical prototype compatibility",
)
text = replace_once(
    text,
    "        return CreateRateLocked(scope, options, transitionSource: null);\n",
    "        return CreateRateLocked(\n"
    "            scope, options, transitionSource: null, fixedWindowActivationMode);\n",
    "historical prototype create",
)
text = replace_once(
    text,
    "    private AdmissionRateState CreateRateLocked(\n"
    "        AdmissionRuleStateKey scope,\n"
    "        SharpLinkAdmissionRuleOptions options,\n"
    "        AdmissionRateState? transitionSource)\n",
    "    private AdmissionRateState CreateRateLocked(\n"
    "        AdmissionRuleStateKey scope,\n"
    "        SharpLinkAdmissionRuleOptions options,\n"
    "        AdmissionRateState? transitionSource,\n"
    "        DynamicFixedWindowActivationMode? fixedWindowActivationMode = null)\n",
    "create rate signature",
)
text = replace_once(
    text,
    "        var state = AdmissionRateState.Create(options, _timeProvider, transitionSource);\n",
    "        var state = fixedWindowActivationMode is not null\n"
    "            ? AdmissionRateState.CreateDynamicFixedWindow(options, _timeProvider)\n"
    "            : AdmissionRateState.Create(options, _timeProvider, transitionSource);\n",
    "specialized create",
)

record_marker = '''internal readonly record struct AdmissionRateTransition(
    AdmissionRateState Source,
    AdmissionRateState? Target);

'''
text = replace_once(
    text,
    record_marker,
    record_marker + '''internal readonly record struct AdmissionDynamicFixedWindowUpdate(
    AdmissionRateState State,
    AdmissionRateStateDefinition Definition,
    DynamicFixedWindowActivationMode ActivationMode);

''',
    "fixed update record",
)
text = replace_once(
    text,
    "    private readonly AdmissionPartitionUpdate? _partitionUpdate;\n",
    "    private readonly AdmissionPartitionUpdate? _partitionUpdate;\n"
    "    private readonly AdmissionDynamicFixedWindowUpdate[] _fixedWindowUpdates;\n",
    "fixed update field",
)
text = replace_once(
    text,
    "        IEnumerable<AdmissionConcurrencyResize> resizes,\n"
    "        IEnumerable<AdmissionRateTransition> rateTransitions,\n"
    "        AdmissionPartitionUpdate? partitionUpdate = null)\n",
    "        IEnumerable<AdmissionConcurrencyResize> resizes,\n"
    "        IEnumerable<AdmissionRateTransition> rateTransitions,\n"
    "        AdmissionPartitionUpdate? partitionUpdate = null,\n"
    "        IEnumerable<AdmissionDynamicFixedWindowUpdate>? fixedWindowUpdates = null)\n",
    "plan constructor signature",
)
text = replace_once(
    text,
    "        _partitionUpdate = partitionUpdate;\n",
    "        _partitionUpdate = partitionUpdate;\n"
    "        _fixedWindowUpdates = fixedWindowUpdates is null ? [] : [.. fixedWindowUpdates];\n",
    "plan fixed update assignment",
)
text = replace_once(
    text,
    "    internal int PartitionUpdateCount => _partitionUpdate is null ? 0 : 1;\n",
    "    internal int PartitionUpdateCount => _partitionUpdate is null ? 0 : 1;\n\n"
    "    internal int DynamicFixedWindowUpdateCount => _fixedWindowUpdates.Length;\n",
    "plan diagnostic count",
)
text = replace_once(
    text,
    "        foreach (var transition in _rateTransitions)\n"
    "            transition.Source.CommitTransitionTo(transition.Target);\n",
    "        foreach (var transition in _rateTransitions)\n"
    "            transition.Source.CommitTransitionTo(transition.Target);\n\n"
    "        foreach (var update in _fixedWindowUpdates)\n"
    "        {\n"
    "            update.State.ApplyDynamicFixedWindowUpdate(\n"
    "                update.Definition, update.ActivationMode);\n"
    "        }\n",
    "commit fixed updates",
)
save(path, text)


# Structural audit for the Phase B1 candidate.
controller = load("src/SharpLink.Server/Admission/SharpLinkAdmissionController.cs")
kernel = load("src/SharpLink.Server/Admission/AdmissionStateKernel.cs")
limiter = load("src/SharpLink.Server/Admission/AdmissionLimiterState.cs")
if "rate.LimiterForAdmission" not in controller:
    raise RuntimeError("specialized limiter is not bound directly into request slots")
if "AdmissionDynamicFixedWindowUpdate" not in kernel:
    raise RuntimeError("dynamic FixedWindow update is not represented in the update plan")
if "CreateDynamicFixedWindow" not in limiter:
    raise RuntimeError("specialized FixedWindow lifecycle wrapper is missing")
