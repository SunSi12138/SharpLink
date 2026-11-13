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


def static_slice_profile(source: str, enabled: bool = False) -> str:
    """Select the one explicit experimental symbol, rejecting unknown directives."""
    output, stack = [], [True]
    for line in source.splitlines():
        directive = line.strip()
        if directive in ('#if SHARPLINK_775_STATIC_SLICE', '#if !SHARPLINK_775_STATIC_SLICE'):
            selected = enabled if directive == '#if SHARPLINK_775_STATIC_SLICE' else not enabled
            stack.append(stack[-1] and selected)
        elif directive == '#else':
            if len(stack) < 2:
                raise ValueError('Unmatched #else')
            stack[-1] = stack[-2] and not stack[-1]
        elif directive == '#endif':
            if len(stack) < 2:
                raise ValueError('Unmatched #endif')
            stack.pop()
        elif directive.startswith('#'):
            raise ValueError('Unreviewed conditional: ' + directive)
        elif stack[-1]:
            output.append(line)
    if len(stack) != 1:
        raise ValueError('Unclosed experimental conditional')
    return '\n'.join(output)


def exact_tokens(source: str) -> list[str]:
    # Ignore formatting only. Preserve complete strings/comments, including their spaces.
    return re.findall(r'@"(?:[^"]|"")*"|"(?:\\.|[^"\\])*"|//[^\n]*|/\*[\s\S]*?\*/|[^\s]', source)


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
            '        ProtocolV2FrameFlags flags,\n        ServiceRegistration service,\n', 1).replace(
            '            owner = null;',
            '            retainedPayload.CaptureSelectedService(service);\n            owner = null;', 1)
        self.assertEqual(expected, method(source, 'TryCopySelectedAdmissionPayload'))
        self.assertIn('[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]\n'
                      '    private bool TryCopySelectedAdmissionPayload(', source)

    def test_one_way_resource_skeleton(self):
        self.assert_pair('AdmissionDispatch', 'AdmittedOneWayDispatch', 'DispatchOneWayRpc')

    def test_rpc_resource_skeleton(self):
        self.assert_pair('InvocationDispatch', 'AdmittedRpcDispatch', 'DispatchRpcAsync')

    def assert_pair(self, immediate_file, admitted_file, name):
        immediate = static_slice_profile((SERVER / f'SharpLinkServer.{immediate_file}.cs').read_text())
        admitted = static_slice_profile((SERVER / f'SharpLinkServer.{admitted_file}.cs').read_text())
        self.assertEqual(exact_tokens(expected_admitted(immediate, name)),
                         exact_tokens(method(admitted, name.replace('Dispatch', 'DispatchAdmitted', 1))))

    def test_conditional_control_and_literal_preservation(self):
        text = 'a\n#if SHARPLINK_775_STATIC_SLICE\nb\n#else\nc\n#endif\nd'
        self.assertEqual(static_slice_profile(text), 'a\nc\nd')
        self.assertEqual(static_slice_profile(text, True), 'a\nb\nd')
        self.assertNotEqual(exact_tokens('"a b"'), exact_tokens('"a  b"'))
        self.assertNotEqual(exact_tokens('// a b'), exact_tokens('// a  b'))
        self.assertEqual(exact_tokens('f ( a )'), exact_tokens('f(a)'))
        with self.assertRaises(ValueError):
            static_slice_profile('#if UNKNOWN\na\n#endif')

    def test_static_slice_queued_identity_is_carried_not_resolved_again(self):
        for name in ('AdmittedRpcDispatch', 'AdmittedOneWayDispatch'):
            body = static_slice_profile((SERVER / f'SharpLinkServer.{name}.cs').read_text(), True)
            self.assertIn('retainedAdmissionPayload?.SliceMethod', body)
            self.assertIn('serviceSelection.TryGetService(out var serviceInfo)', body)
            self.assertNotIn('ResolveStaticRequest(', body)
            self.assertNotIn('Volatile.Read(ref _services)', body)
            self.assertNotIn('Stub.TryGetMethodDescriptor(', body)
        for name, payload in (('InvocationDispatch', 'queuedRetainedPayload'), ('AdmissionDispatch', 'retainedPayload')):
            body = static_slice_profile((SERVER / f'SharpLinkServer.{name}.cs').read_text(), True)
            self.assertIn(payload + '!.SliceMethod = boundMethod;', body)


if __name__ == '__main__':
    unittest.main()
