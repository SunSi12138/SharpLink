"""Keep the deliberately separate queued dispatch resource skeletons in sync.

Immediate dispatch is kept separate to avoid passing queue-only selection metadata
through ordinary-call ABIs.  This guard permits exactly the identity selection,
missing-registration response, and omitted already-completed admission stage.
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


def expected_admitted(source: str, name: str) -> str:
    body = method(source, name)
    body = body.replace(name + '(', name.replace('Dispatch', 'DispatchAdmitted', 1)
                        + '(\n        QueuedServiceSelection serviceSelection,', 1)
    start = body.index('            if (admissionProgram is not null && !admissionGranted)')
    end = body.index('            var admission = TryReserveCall', start)
    body = body[:start] + body[end:]
    body = body.replace(
        'Volatile.Read(ref _services).TryGetValue(request.InterfaceHash, out var serviceInfo)',
        'serviceSelection.TryGetService(out var serviceInfo)', 1)
    body = body.replace(
        'var resolved = ResolveMethodCall(serviceInfo, request.MethodHash);',
        'var resolved = new ResolvedMethodCall(serviceInfo, retainedAdmissionPayload!.SelectedMethod);', 1)
    if name == 'DispatchRpcAsync':
        body = body.replace(
            'SharpLinkErrorCode.Unimplemented,\n                            $"Service {request.InterfaceHash} is not implemented."',
            'SharpLinkErrorCode.Unavailable,\n                            "RPC module is draining"', 1)
    return body


class AdmittedDispatchParityTests(unittest.TestCase):
    def test_selected_payload_copy_resource_skeleton(self):
        source = (SERVER / 'SharpLinkServer.PreAdmissionStreams.cs').read_text()
        expected = method(source, 'TryCopyAdmissionPayload').replace(
            'TryCopyAdmissionPayload(', 'TryCopySelectedAdmissionPayload(', 1).replace(
            '        ProtocolV2FrameFlags flags,\n',
            '        ProtocolV2FrameFlags flags,\n        ServiceRegistration service,\n        RpcMethodFacts method,\n', 1).replace(
            '            owner = null;',
            '            retainedPayload.CaptureSelectedService(service, method);\n            owner = null;', 1)
        self.assertEqual(expected, method(source, 'TryCopySelectedAdmissionPayload'))
        self.assertIn('[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]\n'
                      '    private bool TryCopySelectedAdmissionPayload(', source)

    def test_one_way_resource_skeleton(self):
        self.assert_pair('AdmissionDispatch', 'AdmittedOneWayDispatch', 'DispatchOneWayRpc')

    def test_rpc_resource_skeleton(self):
        self.assert_pair('InvocationDispatch', 'AdmittedRpcDispatch', 'DispatchRpcAsync')

    def assert_pair(self, immediate_file, admitted_file, name):
        immediate = (SERVER / f'SharpLinkServer.{immediate_file}.cs').read_text()
        admitted = (SERVER / f'SharpLinkServer.{admitted_file}.cs').read_text()
        self.assertEqual(expected_admitted(immediate, name),
                         method(admitted, name.replace('Dispatch', 'DispatchAdmitted', 1)))


if __name__ == '__main__':
    unittest.main()
