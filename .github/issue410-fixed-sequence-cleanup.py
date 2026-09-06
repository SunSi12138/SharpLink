from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def replace_once(path: str, old: str, new: str) -> None:
    file = ROOT / path
    text = file.read_text()
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected exactly one match, found {count}: {old[:100]!r}")
    file.write_text(text.replace(old, new, 1))


policy = "src/SharpLink.Server/Admission/AdmissionRateState.FixedWindow.cs"
replace_once(policy, "    private readonly long _fixedSequence;", "    private long _fixedSequence;")
replace_once(policy, "    private int _fixedCommitted;\n    private int _fixedPublished;\n", "")
replace_once(policy, "        _fixedCommitted = 1;\n        _fixedPublished = 1;\n", "")
replace_once(
    policy,
    """        Counter counter,\n        long sequence,\n        long windowTimestampTicks,\n        DynamicFixedWindowActivationMode activationMode)\n""",
    """        Counter counter,\n        long windowTimestampTicks,\n        DynamicFixedWindowActivationMode activationMode)\n""",
)
replace_once(policy, "        _fixedSequence = sequence;\n", "")
replace_once(
    policy,
    "    internal long CounterIdentityForTests => _fixedCounter!.Identity;",
    "    internal object CounterIdentityForTests => _fixedCounter!;",
)
replace_once(
    policy,
    """    private void FinalizeFixedForCommit(int preActivationLimit, long activationBoundary)\n    {\n        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preActivationLimit);\n        if (Interlocked.Exchange(ref _fixedCommitted, 1) != 0)\n            throw new InvalidOperationException(\"FixedWindow policy view was committed more than once.\");\n        _fixedPreActivationLimit = preActivationLimit;\n        _fixedActivationBoundary = activationBoundary;\n    }\n\n    private void MarkFixedPublishedLocked()\n        => Volatile.Write(ref _fixedPublished, 1);\n\n""",
    """    private void FinalizeFixedForCommit(\n        long sequence,\n        int preActivationLimit,\n        long activationBoundary)\n    {\n        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);\n        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preActivationLimit);\n        if (_fixedSequence != 0)\n            throw new InvalidOperationException(\"FixedWindow policy view was committed more than once.\");\n        _fixedSequence = sequence;\n        _fixedPreActivationLimit = preActivationLimit;\n        _fixedActivationBoundary = activationBoundary;\n    }\n\n""",
)

counter = "src/SharpLink.Server/Admission/AdmissionRateState.FixedWindowCounter.cs"
replace_once(counter, "        private static long s_nextIdentity;\n\n", "")
replace_once(
    counter,
    "        private long _nextSequence = 1;\n        private long _retiredThroughSequence;",
    "        private long _nextSequence = 1;\n        private long _publishedThroughSequence = 1;\n        private long _retiredThroughSequence;",
)
replace_once(counter, "            Identity = Interlocked.Increment(ref s_nextIdentity);\n", "")
replace_once(counter, "\n        internal long Identity { get; }\n", "\n")
replace_once(
    counter,
    """                var sequence = checked(++_nextSequence);\n                _references = checked(_references + 1);\n                return new AdmissionRateState(\n                    definition,\n                    this,\n                    sequence,\n                    windowTimestampTicks,\n                    activationMode);\n""",
    """                _references = checked(_references + 1);\n                return new AdmissionRateState(\n                    definition,\n                    this,\n                    windowTimestampTicks,\n                    activationMode);\n""",
)
replace_once(
    counter,
    "                target.FinalizeFixedForCommit(sourceLimit, boundary);",
    "                target.FinalizeFixedForCommit(checked(++_nextSequence), sourceLimit, boundary);",
)
replace_once(
    counter,
    """        internal void Publish(AdmissionRateState policy)\n        {\n            if (Volatile.Read(ref policy._fixedPublished) != 0)\n                return;\n\n            AdmissionRateWaiter? granted;\n""",
    """        internal void Publish(AdmissionRateState policy)\n        {\n            var sequence = Volatile.Read(ref policy._fixedSequence);\n            if (sequence != 0 && sequence <= Volatile.Read(ref _publishedThroughSequence))\n                return;\n\n            AdmissionRateWaiter? granted;\n""",
)
old_publish = """        private AdmissionRateWaiter? PublishLocked(AdmissionRateState policy, long now)\n        {\n            if (Volatile.Read(ref policy._fixedPublished) != 0)\n                return null;\n            if (Volatile.Read(ref policy._fixedCommitted) == 0)\n                throw new InvalidOperationException(\"Uncommitted Dynamic FixedWindow policy became visible.\");\n\n            AdvanceLocked(now);\n"""
new_publish = """        private AdmissionRateWaiter? PublishLocked(AdmissionRateState policy, long now)\n        {\n            var sequence = Volatile.Read(ref policy._fixedSequence);\n            if (sequence == 0)\n                throw new InvalidOperationException(\"Uncommitted Dynamic FixedWindow policy became visible.\");\n            if (sequence <= _publishedThroughSequence)\n                return null;\n            if (sequence != checked(_publishedThroughSequence + 1))\n                throw new InvalidOperationException(\"Dynamic FixedWindow policy publication sequence is not contiguous.\");\n\n            AdvanceLocked(now);\n"""
replace_once(counter, old_publish, new_publish)
replace_once(
    counter,
    """                    ActivateLatePublishedPolicyLocked(policy, now);\n                    policy.MarkFixedPublishedLocked();\n                    return GrantWaitersLocked(now);\n""",
    """                    ActivateLatePublishedPolicyLocked(policy, now);\n                    Volatile.Write(ref _publishedThroughSequence, sequence);\n                    return GrantWaitersLocked(now);\n""",
)
replace_once(
    counter,
    """            policy.MarkFixedPublishedLocked();\n            return GrantWaitersLocked(now);\n""",
    """            Volatile.Write(ref _publishedThroughSequence, sequence);\n            return GrantWaitersLocked(now);\n""",
)
old_direct = """        private int GetDirectLimitLocked(AdmissionRateState policy)\n        {\n            if (!ReferenceEquals(policy._fixedCounter, this))\n                throw new InvalidOperationException(\"Dynamic FixedWindow policy belongs to another counter.\");\n            if (policy._fixedSequence <= _retiredThroughSequence)\n                return _activeLimit;\n            if (policy._fixedActivationMode == DynamicFixedWindowActivationMode.Immediate)\n                return policy._definition.Limit;\n            if (Volatile.Read(ref policy._fixedCommitted) == 0)\n                throw new InvalidOperationException(\"Uncommitted Dynamic FixedWindow policy became visible.\");\n            return policy._fixedPreActivationLimit;\n        }\n"""
new_direct = """        private int GetDirectLimitLocked(AdmissionRateState policy)\n        {\n            if (!ReferenceEquals(policy._fixedCounter, this))\n                throw new InvalidOperationException(\"Dynamic FixedWindow policy belongs to another counter.\");\n            var sequence = Volatile.Read(ref policy._fixedSequence);\n            if (sequence == 0)\n                throw new InvalidOperationException(\"Uncommitted Dynamic FixedWindow policy became visible.\");\n            if (sequence <= _retiredThroughSequence)\n                return _activeLimit;\n            if (policy._fixedActivationMode == DynamicFixedWindowActivationMode.Immediate)\n                return policy._definition.Limit;\n            return policy._fixedPreActivationLimit;\n        }\n"""
replace_once(counter, old_direct, new_direct)

# Static audit: per-policy publication flags and synthetic Counter identity are gone.
for rel in (policy, counter):
    text = (ROOT / rel).read_text()
    for dead in ("_fixedCommitted", "_fixedPublished", "MarkFixedPublishedLocked", "s_nextIdentity", ".Identity"):
        if dead in text:
            raise RuntimeError(f"{rel}: obsolete fixed publication state survived: {dead}")

print("issue #410 FixedWindow sequence publication cleanup staged")
