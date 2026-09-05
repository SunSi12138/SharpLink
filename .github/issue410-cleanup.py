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


def remove_between(text: str, start: str, end: str, label: str) -> str:
    start_index = text.index(start)
    end_index = text.index(end, start_index)
    if end_index <= start_index:
        raise RuntimeError(f"{label}: invalid marker order")
    return text[:start_index] + text[end_index:]


# Finish the generation boundary by deleting the remaining rate-transition compatibility surface.
path = "src/SharpLink.Server/Admission/AdmissionLimiterState.cs"
text = load(path)
for line in [
    "    internal object Lineage { get; } = new();\n\n",
    "    internal long TransitionDebtForDiagnostics => 0;\n\n",
    "    internal long TransitionBarrierExpiryForDiagnostics => 0;\n\n",
]:
    if line not in text:
        raise RuntimeError(f"limiter cleanup missing: {line.strip()}")
    text = text.replace(line, "", 1)
text = replace_once(
    text,
    "    internal static AdmissionRateState Create(\n        SharpLinkAdmissionRuleOptions options,\n        TimeProvider timeProvider,\n        AdmissionRateState? transitionSource = null)\n",
    "    internal static AdmissionRateState Create(\n        SharpLinkAdmissionRuleOptions options,\n        TimeProvider timeProvider)\n",
    "rate create signature",
)
text = replace_once(text, "        _ = transitionSource;\n", "", "remove transition source")
text = replace_once(
    text,
    "    internal void CommitTransitionTo(AdmissionRateState? target)\n        => _ = target;\n\n",
    "",
    "remove rate transition hook",
)
save(path, text)

# Rate update planning becomes purely prospective. Only unchanged definitions share a state.
path = "src/SharpLink.Server/Admission/AdmissionStateKernel.cs"
text = load(path)
for line in [
    "    private readonly Dictionary<AdmissionRuleStateKey, AdmissionRateState> _publishedRateStates = [];\n",
    "    private bool _hasPublishedRateLineage;\n",
]:
    if line not in text:
        raise RuntimeError(f"kernel field cleanup missing: {line.strip()}")
    text = text.replace(line, "", 1)
text = remove_between(
    text,
    "    /// <summary>\n    /// Records only the rate states of the actual publication.",
    "    /// <summary>\n    /// Records only the selector namespace of the actual current publication.",
    "remove published rate lineage",
)
text = replace_once(
    text,
    "        string scope,\n        List<AdmissionConcurrencyResize> resizes,\n        List<AdmissionRateTransition> rateTransitions)\n",
    "        string scope,\n        List<AdmissionConcurrencyResize> resizes)\n",
    "update binding signature",
)
rate_start = text.index("            var sourceRate = sourceRuntime?.RateState;")
rate_end = text.index("            var runtime = AdmissionRuleRuntime.CreateBound", rate_start)
text = text[:rate_start] + """            var sourceRate = sourceRuntime?.RateState;
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
                    rate = CreateRateLocked(key, options);
                }
            }

""" + text[rate_end:]
text = replace_once(
    text,
    "            _rateStates.Clear();\n            _publishedRateStates.Clear();\n            _hasPublishedRateLineage = false;\n",
    "            _rateStates.Clear();\n",
    "dispose rate registry cleanup",
)
rate_compat_start = text.index("    private AdmissionRateState AcquireCompatibleRateLocked(")
rate_compat_end = text.index("    private void AddRateReferenceLocked", rate_compat_start)
text = text[:rate_compat_start] + """    private AdmissionRateState AcquireCompatibleRateLocked(
        AdmissionRuleStateKey scope,
        SharpLinkAdmissionRuleOptions options)
        => CreateRateLocked(scope, options);

    private AdmissionRateState CreateRateLocked(
        AdmissionRuleStateKey scope,
        SharpLinkAdmissionRuleOptions options)
    {
        var definition = AdmissionRateStateDefinition.Create(options.RateLimit);
        var key = new AdmissionRateStateKey(scope, definition, ++_nextRateStateGeneration);
        var state = AdmissionRateState.Create(options, _timeProvider);
        _rateStates.Add(key, new RateStateEntry(state, 1));
        return state;
    }

""" + text[rate_compat_end:]
text = replace_once(
    text,
    "        entry.ProgramReferences++;\n        entry.RetainedLineageAnchor = false;\n",
    "        entry.ProgramReferences++;\n",
    "rate add reference",
)
text = remove_between(
    text,
    "    private bool HasOtherRateStateInLineageLocked(AdmissionRateState state)",
    "    private AdmissionPartitionStateBinding CreatePartitionStateLocked(",
    "remove rate lineage anchor helpers",
)
release_start = text.index("            if (binding.RateState is { } rate &&", text.index("    private void ReleaseBindingsLocked("))
release_end = text.index("        }\n\n        if (controller.PartitionStateBinding", release_start)
text = text[:release_start] + """            if (binding.RateState is { } rate &&
                TryFindRateEntryLocked(binding.Key, rate, out var rateKey, out var rateEntry))
            {
                if (--rateEntry.ProgramReferences < 0)
                    throw new InvalidOperationException("Admission rate state reference count underflowed.");
                if (rateEntry.ProgramReferences == 0)
                {
                    _rateStates.Remove(rateKey);
                    (dispose ??= []).Add(rateEntry.State);
                }
            }
""" + text[release_end:]
text = replace_once(text, "\n        CollectUnreferencedRateAnchorsLocked(ref dispose);\n", "\n", "remove rate anchor collection")
text = replace_once(text, "        internal bool RetainedLineageAnchor;\n", "", "remove retained lineage anchor field")
text = replace_once(
    text,
    "internal readonly record struct AdmissionRateTransition(\n    AdmissionRateState Source,\n    AdmissionRateState? Target);\n\n",
    "",
    "remove rate transition record",
)
plan_start = text.index("/// <summary>Prepared transition whose only live mutations")
text = text[:plan_start] + """/// <summary>Prepared mutable-state changes committed inside publication serialization.</summary>
internal sealed class AdmissionUpdatePlan
{
    private readonly AdmissionConcurrencyResize[] _resizes;
    private readonly AdmissionPartitionUpdate? _partitionUpdate;
    private int _committed;

    internal AdmissionUpdatePlan(
        IEnumerable<AdmissionConcurrencyResize> resizes,
        AdmissionPartitionUpdate? partitionUpdate = null)
    {
        _resizes = [.. resizes];
        _partitionUpdate = partitionUpdate;
        foreach (var resize in _resizes)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resize.PermitLimit);
    }

    internal int ResizeCount => _resizes.Length;

    internal int PartitionUpdateCount => _partitionUpdate is null ? 0 : 1;

    internal bool RequiresTargetCommit => _resizes.Length != 0 || _partitionUpdate is not null;

    internal void Commit(Action<int, int>? afterResize = null)
    {
        if (Interlocked.Exchange(ref _committed, 1) != 0)
            throw new InvalidOperationException("Admission update plan was committed more than once.");

        // Partition preparation creates all per-entry target objects before mutating the pool. Run it
        // first so an allocation/configuration failure cannot follow a successful Global/Contract/Method resize.
        _partitionUpdate?.Commit();

        for (var index = 0; index < _resizes.Length; index++)
        {
            var resize = _resizes[index];
            resize.State.Resize(resize.PermitLimit);
            afterResize?.Invoke(index, _resizes.Length);
        }
    }
}
"""
for forbidden in [
    "AdmissionRateTransition",
    "_publishedRateStates",
    "_hasPublishedRateLineage",
    "RetainedLineageAnchor",
    "HasOtherRateStateInLineageLocked",
    "CollectUnreferencedRateAnchorsLocked",
    "transitionSource",
    "RateTransitionCount",
]:
    if forbidden in text:
        raise RuntimeError(f"kernel still contains rate migration scaffold: {forbidden}")
save(path, text)

# The controller no longer prepares or commits rate transitions. Changed rate bindings are complete
# when the immutable candidate is built; publication merely chooses which generation is current.
path = "src/SharpLink.Server/Admission/SharpLinkAdmissionController.cs"
text = load(path)
text = replace_once(text, "        var rateTransitions = new List<AdmissionRateTransition>();\n", "", "controller rate transition list")
text = text.replace("                    resizes,\n                    rateTransitions);", "                    resizes);")
text = replace_once(
    text,
    "            }\n            else if (source._global?.RateState is { } removedGlobalRate)\n            {\n                rateTransitions.Add(new AdmissionRateTransition(removedGlobalRate, null));\n            }\n\n            foreach (var pair in contractOptions)\n",
    "            }\n\n            foreach (var pair in contractOptions)\n",
    "remove global rate removal transition",
)
text = remove_between(
    text,
    "            foreach (var sourcePair in source._contracts)\n",
    "            foreach (var pair in methodOptions)\n",
    "remove contract rate removal transitions",
)
text = remove_between(
    text,
    "            foreach (var sourcePair in source._methods)\n",
    "            AdmissionPartitionPool? partitions = null;\n",
    "remove method rate removal transitions",
)
text = replace_once(
    text,
    "            updatePlan = new AdmissionUpdatePlan(resizes, rateTransitions, partitionUpdate);\n",
    "            updatePlan = new AdmissionUpdatePlan(resizes, partitionUpdate);\n",
    "controller update plan",
)
if "AdmissionRateTransition" in text or "rateTransitions" in text:
    raise RuntimeError("controller still contains rate transition scaffold")
save(path, text)

# Publication still records concurrency/partition compatibility, but rate generations need no
# published-lineage registry because unchanged Updates reuse their exact source binding directly.
path = "src/SharpLink.Server/SharpLinkServer.AdmissionProgram.cs"
text = load(path)
count = text.count("                lifecycle.Kernel.RecordPublishedRateLineage(")
if count != 2:
    raise RuntimeError(f"server expected two rate lineage publication calls, found {count}")
lines = text.splitlines(keepends=True)
lines = [line for line in lines if "RecordPublishedRateLineage(" not in line]
text = "".join(lines)
save(path, text)

# Remove the now-trivial grant forwarding method from the steady-state hot path.
path = "src/SharpLink.Server/Admission/AdmissionDynamicRateState.cs"
text = load(path)
text = text.replace("RecordGrantLocked(now);", "RecordOwnGrantLocked();")
start = text.index("    private void RecordGrantLocked(long now)")
end = text.index("    private void RecordOwnGrantLocked", start)
text = text[:start] + text[end:]
if "RecordGrantLocked" in text:
    raise RuntimeError("dynamic rate state still contains grant forwarding")
save(path, text)

# Final product-tree audit: generation-scoped rate state must not hide migration under a new owner.
product_paths = [
    "src/SharpLink.Server/Admission/AdmissionDynamicRateState.cs",
    "src/SharpLink.Server/Admission/AdmissionLimiterState.cs",
    "src/SharpLink.Server/Admission/AdmissionStateKernel.cs",
    "src/SharpLink.Server/Admission/SharpLinkAdmissionController.cs",
    "src/SharpLink.Server/SharpLinkServer.AdmissionProgram.cs",
]
forbidden = [
    "AdmissionRateTransitionLineage",
    "AdmissionRateTransition",
    "transitionDebt",
    "TransitionDebt",
    "transitionSource",
    "RetainedLineageAnchor",
    "RecordPublishedRateLineage",
    "CommitTransitionTo",
    "InitializeTransitionLocked",
    "RecordLegacyGrantLocked",
]
for product_path in product_paths:
    product = load(product_path)
    for symbol in forbidden:
        if symbol in product:
            raise RuntimeError(f"{product_path}: migration symbol remains: {symbol}")
