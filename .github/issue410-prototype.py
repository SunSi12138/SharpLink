from pathlib import Path


def write(path: str, text: str) -> None:
    Path(path).write_text(text, encoding="utf-8")


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, found {count}")
    return text.replace(old, new, 1)


# AdmissionDynamicRateState: keep steady-state algorithms/waiter ownership, delete
# cross-generation history migration and give each generation its own lock.
path = "src/SharpLink.Server/Admission/AdmissionDynamicRateState.cs"
text = Path(path).read_text(encoding="utf-8")
class_marker = "internal sealed class AdmissionDynamicRateState : IDisposable"
class_index = text.index(class_marker)
summary_index = text.index("/// <summary>", text.index("namespace SharpLink.Server;"))
text = text[:summary_index] + """/// <summary>
/// SharpLink-owned deterministic state for one immutable rate-policy generation. Mutable quota,
/// waiters, and timers are generation-local; no rate history is translated into a successor.
/// </summary>
""" + text[class_index:]
text = replace_once(
    text,
    "internal sealed class AdmissionDynamicRateState : IDisposable\n{\n    private readonly AdmissionRateStateDefinition _definition;",
    "internal sealed class AdmissionDynamicRateState : IDisposable\n{\n    private readonly Lock _gate = new();\n    private readonly AdmissionRateStateDefinition _definition;",
    "add generation-local gate",
)
for line in [
    "    private long _tokenTransitionCredit;\n",
    "    private long _transitionDebt;\n",
    "    private long _transitionDebtExpiry;\n",
    "    private long _latestGrantTimestamp = long.MinValue;\n",
]:
    if line not in text:
        raise RuntimeError(f"missing transition field: {line.strip()}")
    text = text.replace(line, "", 1)
text = replace_once(
    text,
    "    internal AdmissionDynamicRateState(\n        AdmissionRateStateDefinition definition,\n        TimeProvider timeProvider,\n        AdmissionRateTransitionLineage? lineage = null)\n",
    "    internal AdmissionDynamicRateState(\n        AdmissionRateStateDefinition definition,\n        TimeProvider timeProvider)\n",
    "constructor signature",
)
text = replace_once(text, "        Lineage = lineage ?? new AdmissionRateTransitionLineage();\n", "", "remove lineage assignment")
text = replace_once(text, "        if (lineage is null)\n            Lineage.AttachFresh(this);\n", "", "remove lineage attach")
text = replace_once(text, "    internal AdmissionRateTransitionLineage Lineage { get; }\n\n", "", "remove lineage property")
text = text.replace("lock (Lineage.Gate)", "lock (_gate)")

start = text.index("    internal long TransitionDebtForDiagnostics")
end = text.index("    internal RateLimitLease AttemptAcquire", start)
text = text[:start] + text[end:]

start = text.index("    internal void CommitTransitionTo")
end = text.index("    private void RecordGrantLocked", start)
text = text[:start] + text[end:]

start = text.index("    private void RecordGrantLocked")
end = text.index("    private void RecordOwnGrantLocked", start)
text = text[:start] + """    private void RecordGrantLocked(long now)
    {
        _ = now;
        RecordOwnGrantLocked();
    }

""" + text[end:]

start = text.index("    private void RecordLegacyGrantLocked")
end = text.index("    private bool CanGrantLocked", start)
text = text[:start] + text[end:]
text = replace_once(
    text,
    "    private bool CanGrantLocked()\n        => GetBurdenLocked() < _definition.Limit;\n\n    private long GetBurdenLocked()\n        => SaturatingAdd(_transitionDebt, GetOwnBurdenLocked());\n",
    "    private bool CanGrantLocked()\n        => GetOwnBurdenLocked() < _definition.Limit;\n",
    "generation-local burden",
)

start = text.index("    private long GetDebtExpiryLocked")
end = text.index("    private void AdvanceLocked", start)
text = text[:start] + text[end:]

start = text.index("    private void AdvanceLocked")
end = text.index("    private void AdvanceTokenBucketLocked", start)
text = text[:start] + """    private void AdvanceLocked(long now)
    {
        switch (_definition.Kind)
        {
            case AdmissionRateStateKind.TokenBucket:
                AdvanceTokenBucketLocked(now);
                break;
            case AdmissionRateStateKind.FixedWindow:
                AdvanceFixedWindowLocked(now);
                break;
            case AdmissionRateStateKind.SlidingWindow:
                AdvanceSlidingWindowLocked(now);
                break;
        }
    }

""" + text[end:]

start = text.index("    private void AdvanceTokenBucketLocked")
end = text.index("    private void AdvanceFixedWindowLocked", start)
text = text[:start] + """    private void AdvanceTokenBucketLocked(long now)
    {
        var period = GetPeriodTimestampTicks();
        var elapsed = now - _tokenAnchor;
        if (elapsed < period)
            return;

        var periods = elapsed / period;
        var credit = SaturatingMultiply(periods, _definition.Secondary);
        if (_tokenDebt != 0)
            _tokenDebt -= Math.Min(_tokenDebt, credit);
        _tokenAnchor = SaturatingAdd(_tokenAnchor, SaturatingMultiply(periods, period));
    }

""" + text[end:]

text = replace_once(text, "        var next = _transitionDebt == 0 ? long.MaxValue : _transitionDebtExpiry;\n", "        var next = long.MaxValue;\n", "next availability")
text = text.replace("var requiredReduction = GetBurdenLocked() - _definition.Limit + 1;", "var requiredReduction = _tokenDebt - _definition.Limit + 1;")
text = replace_once(text, "            Lineage.DetachIfCurrentLocked(this);\n", "", "remove lineage detach")
start = text.index("    private long GetBarrierHorizonTimestampTicks")
end = text.index("    private long GetPeriodTimestampTicks", start)
text = text[:start] + text[end:]
for forbidden in [
    "AdmissionRateTransitionLineage",
    "_transitionDebt",
    "_transitionDebtExpiry",
    "_tokenTransitionCredit",
    "_latestGrantTimestamp",
    "Lineage.Gate",
    "RecordLegacyGrantLocked",
    "InitializeTransitionLocked",
]:
    if forbidden in text:
        raise RuntimeError(f"prototype left migration symbol in dynamic state: {forbidden}")
write(path, text)

# AdmissionRateState remains the wrapper boundary for the first serious prototype. New rate
# definitions receive independent state; the old compatibility-transition hook is deliberately a no-op.
path = "src/SharpLink.Server/Admission/AdmissionLimiterState.cs"
text = Path(path).read_text(encoding="utf-8")
text = replace_once(text, "    internal AdmissionRateTransitionLineage Lineage => _state.Lineage;\n", "    internal object Lineage { get; } = new();\n", "wrapper lineage token")
text = replace_once(text, "    internal long TransitionDebtForDiagnostics => _state.TransitionDebtForDiagnostics;\n", "    internal long TransitionDebtForDiagnostics => 0;\n", "diagnostic debt")
text = replace_once(text, "    internal long TransitionBarrierExpiryForDiagnostics => _state.TransitionBarrierExpiryForDiagnostics;\n", "    internal long TransitionBarrierExpiryForDiagnostics => 0;\n", "diagnostic barrier")
text = replace_once(
    text,
    "        var state = new AdmissionDynamicRateState(\n            definition,\n            timeProvider,\n            transitionSource?.Lineage);\n",
    "        _ = transitionSource;\n        var state = new AdmissionDynamicRateState(definition, timeProvider);\n",
    "fresh generation state",
)
text = replace_once(
    text,
    "    internal void CommitTransitionTo(AdmissionRateState? target)\n        => _state.CommitTransitionTo(target?._state);\n",
    "    internal void CommitTransitionTo(AdmissionRateState? target)\n        => _ = target;\n",
    "no-op compatibility transition",
)
write(path, text)

# A fresh Enable after Disable must not bind to an old still-draining rate generation. Same-definition
# Update continues to reuse sourceRate through AcquireRuleStateForUpdate, preserving no-op update cadence.
path = "src/SharpLink.Server/Admission/AdmissionStateKernel.cs"
text = Path(path).read_text(encoding="utf-8")
old = """        if (_hasPublishedRateLineage)
        {
            if (_publishedRateStates.TryGetValue(scope, out var published) &&
                published.Definition == definition)
            {
                AddRateReferenceLocked(scope, published);
                return published;
            }
            return CreateRateLocked(scope, options, transitionSource: null);
        }
"""
new = """        if (_hasPublishedRateLineage)
            return CreateRateLocked(scope, options, transitionSource: null);
"""
text = replace_once(text, old, new, "fresh enable rate generation")
write(path, text)

# The simplification drops AdmissionDynamicRateState below the normal source LOC threshold, so the
# old maintainability exception must disappear with the transition machinery rather than linger.
path = "eng/maintainability/baseline.json"
text = Path(path).read_text(encoding="utf-8")
allowance = """    {
      \"domain\": \"source\",
      \"path\": \"src/SharpLink.Server/Admission/AdmissionDynamicRateState.cs\",
      \"maxLoc\": 904,
      \"reason\": \"Existing dev debt captured by issue #350.\"
    },
"""
text = replace_once(text, allowance, "", "remove obsolete dynamic-rate maintainability allowance")
write(path, text)
