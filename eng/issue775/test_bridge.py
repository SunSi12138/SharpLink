import copy
import unittest
from pathlib import Path
from bridge import validate_bridge, PATCH_SHA, default_source, digest

VALID = dict(Passed=True,Checks=46,TransportCount=2,WriterTypes=2,GeneratedReceived=2048,StreamWrites=0,Magic=183)


class BridgeReaderTests(unittest.TestCase):
    def test_valid(self):
        validate_bridge(VALID)

    def test_each_field_is_required_and_exact(self):
        for key in VALID:
            with self.subTest(key=key):
                missing = dict(VALID)
                del missing[key]
                with self.assertRaises(ValueError): validate_bridge(missing)
                changed = dict(VALID)
                changed[key] = -1
                with self.assertRaises(ValueError): validate_bridge(changed)

    def test_no_extra_approval(self):
        with self.assertRaises(ValueError): validate_bridge(VALID | {"stage2Authorized":True})

    def test_patch_locked(self):
        self.assertEqual(digest(Path(__file__).with_name("bridge.patch")),PATCH_SHA)

    def test_default_excludes_experiment(self):
        self.assertEqual(default_source("#if SHARPLINK_STATIC_BINDING_PROTOTYPE\nchanged();\n#endif\n"),"")

    def test_frozen_workload_and_no_policy_edit(self):
        text = Path(__file__).with_name("bridge.patch").read_text()
        paths = [s for s in text.splitlines() if s.startswith("+++ ")]
        self.assertEqual(len(paths),2)
        self.assertTrue(all("src/SharpLink.Client/" in s for s in paths))
        self.assertIn("typeof(TStreams) == typeof(RpcNoClientStreams)",text)
        self.assertIn("return client.InvokeStaticOneWayAsync(site, method, request, requestCodec, streams, metadata, cancellationToken)",text)


if __name__ == "__main__": unittest.main()
