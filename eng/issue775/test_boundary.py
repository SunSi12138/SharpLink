import unittest
from boundary_codegen import validate_result


def sample(shape):
    one_way = shape == "oneway"
    return dict(Schema=1, Passed=True, Magic=183, Transport="tcp", Shape=shape,
                Pool=1 if one_way else 4, Concurrency=1 if one_way else 8,
                Batches=512 if one_way else 8, BatchSize=2048 if one_way else 65536,
                Sent=1048576 if one_way else 524288, Received=1048576 if one_way else 524288,
                BoundaryUnaryCalls=512 if one_way else 8, BindCount=1 if one_way else 4,
                PendingBinds=0, ProcessorCount=2, PerformanceEvidence=False)


class BoundaryTests(unittest.TestCase):
    def test_complete_both_shapes(self):
        for shape in ("unary", "oneway"):
            validate_result(sample(shape), "tcp", shape)

    def test_invalid_completion(self):
        for key, value in (("Received", 1), ("Sent", 1), ("Passed", False), ("Magic", 137),
                           ("Batches", 1), ("BatchSize", 1048576), ("BindCount", 2),
                           ("PendingBinds", 1), ("ProcessorCount", 4), ("PerformanceEvidence", True),
                           ("Transport", "sharedmemory"), ("BoundaryUnaryCalls", 0)):
            with self.subTest(key=key):
                data = sample("oneway")
                data[key] = value
                with self.assertRaises(ValueError):
                    validate_result(data, "tcp", "oneway")

    def test_missing_fields(self):
        for key in sample("unary"):
            with self.subTest(key=key):
                data = sample("unary")
                del data[key]
                with self.assertRaises(ValueError):
                    validate_result(data, "tcp", "unary")


if __name__ == "__main__":
    unittest.main()
