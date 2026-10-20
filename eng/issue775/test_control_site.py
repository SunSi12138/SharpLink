import copy
import unittest
from control_site import default_source, validate_checks


def fixture():
    return {"Magic": 183, "Passed": True, "ControlBytes": 80, "Checks": 416,
        "Cases": [{"Transport": t, "Intercepted": i, "Telemetry": a, "Activities": int(a), "Passed": True}
            for t in ("tcp", "sharedmemory") for i in (False, True) for a in (False, True)]}


class ControlSiteTests(unittest.TestCase):
    def test_valid(self):
        validate_checks(fixture(), 80)
    def test_old_layout(self):
        row = fixture(); row["ControlBytes"] = 88
        validate_checks(row, 88)
    def reject(self, mutate):
        data = fixture(); mutate(data)
        with self.assertRaises(ValueError): validate_checks(data, 80)
    def test_wrong_layout(self): self.reject(lambda d: d.update(ControlBytes=88))
    def test_wrong_magic(self): self.reject(lambda d: d.update(Magic=137))
    def test_missing_case(self): self.reject(lambda d: d["Cases"].pop())
    def test_duplicate_case(self): self.reject(lambda d: d["Cases"].__setitem__(1, copy.deepcopy(d["Cases"][0])))
    def test_missing_telemetry(self): self.reject(lambda d: d["Cases"][1].update(Activities=0))
    def test_spurious_telemetry(self): self.reject(lambda d: d["Cases"][0].update(Activities=1))
    def test_missing_checks(self): self.reject(lambda d: d.update(Checks=1))
    def test_failed_scenario(self): self.reject(lambda d: d["Cases"][0].update(Passed=False))
    def test_default_branch(self):
        text = 'a\n#if SHARPLINK_STATIC_BINDING_PROTOTYPE\nx\n#else\nb\n#endif\nc'
        self.assertEqual(default_source(text), 'a b c')
    def test_default_nested(self):
        text = '#if SHARPLINK_STATIC_BINDING_PROTOTYPE\n#if SHARPLINK_STATIC_BINDING_PROTOTYPE\nx\n#else\ny\n#endif\n#else\nb\n#endif'
        self.assertEqual(default_source(text), 'b')
    def test_unclosed_default(self):
        with self.assertRaises(ValueError): default_source('#if SHARPLINK_STATIC_BINDING_PROTOTYPE\nx')


if __name__ == '__main__': unittest.main()
