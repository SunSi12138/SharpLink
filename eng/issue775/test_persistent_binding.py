"""Fail-closed checks for persistent-decode evidence, runnable without .NET."""
import copy
import unittest
from persistent_regression import MODES, validate


def sample(old=False):
    rows = []
    for transport in ("tcp", "sharedmemory"):
        for metadata in (False, True):
            for mode in MODES:
                persistent = mode == "persistent-large"
                success = not (old and persistent)
                rows.append(dict(Transport=transport, Metadata=metadata, Mode=mode,
                    PayloadBytes=8192 if mode == "sync-small" else 2097152, DeadlineEnabled=persistent,
                    WorkerStarts=int(persistent), Decompressions=1, BusinessCalls=int(success),
                    BindingUnchanged=True, PayloadMatched=success, ContextMatched=success,
                    HealthyAfter=True, Drained=True, CallError=None if success else "Target failure",
                    RecoveryError=None, InfrastructureError=None, Passed=success))
    return dict(Schema=1, Magic=183, Passed=not old, Cases=rows)


class PersistentEvidenceTests(unittest.TestCase):
    def test_valid_candidate(self):
        validate(sample(), False)

    def test_valid_old_reproduction(self):
        validate(sample(True), True)

    def test_reject_bad_evidence(self):
        changes = [lambda x: x.update(Magic=137), lambda x: x["Cases"].pop(),
            lambda x: x["Cases"].__setitem__(0, copy.deepcopy(x["Cases"][1])),
            lambda x: x["Cases"][2].update(WorkerStarts=0),
            lambda x: x["Cases"][2].update(PayloadBytes=8192),
            lambda x: x["Cases"][0].update(Decompressions=0),
            lambda x: x["Cases"][2].update(BusinessCalls=2),
            lambda x: x["Cases"][2].update(BindingUnchanged=False),
            lambda x: x["Cases"][2].update(ContextMatched=False),
            lambda x: x["Cases"][2].update(Drained=False),
            lambda x: x["Cases"][2].update(HealthyAfter=False),
            lambda x: x["Cases"][2].update(InfrastructureError="Build error"),
            lambda x: x.update(Passed=False)]
        for index, change in enumerate(changes):
            with self.subTest(index=index):
                value = sample()
                change(value)
                with self.assertRaises(ValueError): validate(value, False)

    def test_old_partial_or_unrelated_failure_is_rejected(self):
        for key, value in (("WorkerStarts", 0), ("Decompressions", 0), ("BusinessCalls", 1),
                           ("CallError", None), ("InfrastructureError", "No codec")):
            with self.subTest(key=key):
                data = sample(True)
                data["Cases"][2][key] = value
                with self.assertRaises(ValueError): validate(data, True)


if __name__ == "__main__":
    unittest.main()
