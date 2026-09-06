from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def replace_once(path: str, old: str, new: str) -> None:
    file = ROOT / path
    text = file.read_text()
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected exactly one match, found {count}: {old[:100]!r}")
    file.write_text(text.replace(old, new, 1))


# AdmissionRateState no longer allocates/retains the legacy migration lineage for FixedWindow.
path = "src/SharpLink.Server/Admission/AdmissionLimiterState.cs"
replace_once(path, "    private readonly AdmissionRateTransitionLineage _lineage;\n", "")
replace_once(
    path,
    """    private AdmissionRateState(AdmissionDynamicRateState state)\n    {\n        _state = state;\n        _lineage = state.Lineage;\n        _definition = state.Definition;\n    }\n""",
    """    private AdmissionRateState(AdmissionDynamicRateState state)\n    {\n        _state = state;\n        _definition = state.Definition;\n    }\n""",
)
replace_once(
    path,
    "    internal AdmissionRateTransitionLineage Lineage => _lineage;",
    "    internal object LineageIdentity => _fixedCounter ?? (object)_state!.Lineage;",
)
replace_once(
    path,
    """        if (_fixedCounter is not null)\n        {\n            if (target?._fixedCounter is not null && ReferenceEquals(_lineage, target._lineage))\n                CommitFixedTransitionTo(target);\n            return;\n        }\n\n        if (target?._state is not null && ReferenceEquals(_lineage, target._lineage))\n            _state!.CommitTransitionTo(target._state);\n""",
    """        if (_fixedCounter is not null)\n        {\n            if (target?._fixedCounter is not null && ReferenceEquals(_fixedCounter, target._fixedCounter))\n                CommitFixedTransitionTo(target);\n            return;\n        }\n\n        if (target?._state is not null && ReferenceEquals(_state!.Lineage, target._state.Lineage))\n            _state.CommitTransitionTo(target._state);\n""",
)

path = "src/SharpLink.Server/Admission/AdmissionRateState.FixedWindow.cs"
replace_once(path, "        _lineage = new AdmissionRateTransitionLineage();\n", "")
replace_once(
    path,
    """        long sequence,\n        long windowTimestampTicks,\n        DynamicFixedWindowActivationMode activationMode,\n        AdmissionRateTransitionLineage lineage)\n    {\n        _definition = definition;\n        _lineage = lineage;\n""",
    """        long sequence,\n        long windowTimestampTicks,\n        DynamicFixedWindowActivationMode activationMode)\n    {\n        _definition = definition;\n""",
)
replace_once(
    path,
    """        return counter.CreateSuccessor(\n            definition,\n            _lineage,\n            windowTimestampTicks,\n            resolvedActivation);\n""",
    """        return counter.CreateSuccessor(\n            definition,\n            windowTimestampTicks,\n            resolvedActivation);\n""",
)

path = "src/SharpLink.Server/Admission/AdmissionRateState.FixedWindowCounter.cs"
replace_once(
    path,
    """        internal AdmissionRateState CreateSuccessor(\n            AdmissionRateStateDefinition definition,\n            AdmissionRateTransitionLineage lineage,\n            long windowTimestampTicks,\n            DynamicFixedWindowActivationMode activationMode)\n""",
    """        internal AdmissionRateState CreateSuccessor(\n            AdmissionRateStateDefinition definition,\n            long windowTimestampTicks,\n            DynamicFixedWindowActivationMode activationMode)\n""",
)
replace_once(
    path,
    """                    sequence,\n                    windowTimestampTicks,\n                    activationMode,\n                    lineage);\n""",
    """                    sequence,\n                    windowTimestampTicks,\n                    activationMode);\n""",
)

# Kernel lineage-anchor bookkeeping uses one identity object; Fixed uses the Counter directly.
path = "src/SharpLink.Server/Admission/AdmissionStateKernel.cs"
replace_once(
    path,
    "if (ReferenceEquals(pair.Value.State.Lineage, state.Lineage))",
    "if (ReferenceEquals(pair.Value.State.LineageIdentity, state.LineageIdentity))",
)

# Ensure no Fixed policy partial references the legacy lineage type/field.
fixed_text = (ROOT / "src/SharpLink.Server/Admission/AdmissionRateState.FixedWindow.cs").read_text()
if "AdmissionRateTransitionLineage" in fixed_text or "_lineage" in fixed_text:
    raise RuntimeError("FixedWindow still depends on legacy rate lineage")

print("issue #410 FixedWindow lineage identity cleanup staged")
