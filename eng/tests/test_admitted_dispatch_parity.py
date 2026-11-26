"""Keep the deliberately separate queued dispatch resource skeletons in sync.

Immediate dispatch retains its own preparation/admission guards. Both paths
must perform identical resource work after reserving the server call.
"""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
SERVER = ROOT / 'src' / 'SharpLink.Server'


def method(source: str, name: str) -> str:
    start = re.search(r'^    private[^\n]*\b' + name + r'\(', source, re.M).start()
    # The resource skeleton is compared as source, including all strings/comments.
    # Both members end at their own four-space closing brace.
    end = source.index('\n    }', start) + len('\n    }')
    return source[start:end]


def resource_skeleton(source: str, name: str) -> str:
    body = method(source, name)
    return body[body.index('            var admission = TryReserveCall'):]


class AdmittedDispatchParityTests(unittest.TestCase):
    def test_queued_and_decode_preparation_share_the_byte_owner(self):
        for suffix, dispatch in (
            ('AdmissionDispatch', 'DispatchOneWayRpc'),
            ('InvocationDispatch', 'DispatchRpcAsync'),
            ('PersistentDecodeDispatch', 'DispatchRpcWithPersistentDecodeAsync'),
        ):
            with self.subTest(dispatch=dispatch):
                source = (SERVER / f'SharpLinkServer.{suffix}.cs').read_text()
                self.assertIn('TryCopyAdmissionPayload(payload, flags, out ', method(source, dispatch))

    def test_one_way_resource_skeleton(self):
        self.assert_pair('AdmissionDispatch', 'AdmittedOneWayDispatch', 'DispatchOneWayRpc')

    def test_rpc_resource_skeleton(self):
        self.assert_pair('InvocationDispatch', 'AdmittedRpcDispatch', 'DispatchRpcAsync')

    def assert_pair(self, immediate_file, admitted_file, name):
        immediate = (SERVER / f'SharpLinkServer.{immediate_file}.cs').read_text()
        admitted = (SERVER / f'SharpLinkServer.{admitted_file}.cs').read_text()
        self.assertEqual(resource_skeleton(immediate, name),
                         resource_skeleton(admitted, name.replace('Dispatch', 'DispatchAdmitted', 1)))


if __name__ == '__main__':
    unittest.main()
