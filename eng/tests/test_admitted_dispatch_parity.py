"""Keep the deliberately separate queued dispatch resource skeletons in sync.

Immediate dispatch receives a prevalidated binding and owned head; queued dispatch
receives the same captured registration/facts and a retained whole-frame owner.
Only those explicit prologue/ownership differences and completed admission are allowed.
"""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
SERVER = ROOT / 'src' / 'SharpLink.Server'


def exact_tokens(source: str) -> list[str]:
    # Ignore formatting only. Preserve complete strings/comments, including their spaces.
    return re.findall(r'@"(?:[^"]|"")*"|"(?:\\.|[^"\\])*"|//[^\n]*|/\*[\s\S]*?\*/|[^\s]', source)


def method(source: str, name: str) -> str:
    start = re.search(r'^    private[^\n]*\b' + name + r'\(', source, re.M).start()
    # The resource skeleton is compared as source, including all strings/comments.
    # Both members end at their own four-space closing brace.
    end = source.index('\n    }', start) + len('\n    }')
    return source[start:end]


def replace_exact(source: str, old: str, new: str) -> str:
    if source.count(old) != 1:
        raise AssertionError('Reviewed dispatch delta changed: ' + old)
    return source.replace(old, new, 1)


# The immediate pipe head needs its own owner before RequestAccepted can suspend.
# Queued heads already live in ServerRetainedAdmissionPayload until dispatch exits.
STREAM_HEAD_COPY = """                else if (request.BoundMethod!.Descriptor.Kind is RpcMethodKind.ClientStreaming or RpcMethodKind.ServerStreaming or RpcMethodKind.DuplexStreaming)
                {
                    // Per-call acceptance can suspend before generated business decoding.
                    // Pipe-backed positions (even an empty slice) expire at AdvanceTo.
                    // Reuse the existing call-state payload owner and terminal cleanup path.
                    if (request.Arguments.IsEmpty)
                        request = request with { Arguments = ReadOnlySequence<byte>.Empty };
                    else
                    {
                        decodedRequestOwner = _runtimeContext.Buffers.Rent(checked((int)request.Arguments.Length));
                        foreach (var segment in request.Arguments) decodedRequestOwner.Write(segment.Span);
                        request = request with { Arguments = new ReadOnlySequence<byte>(decodedRequestOwner.WrittenMemory) };
                    }
                }
"""


def expected_admitted(source: str, name: str) -> str:
    body = method(source, name)
    body = body.replace(name + '(', name.replace('Dispatch', 'DispatchAdmitted', 1)
                        + '(\n        QueuedServiceSelection serviceSelection,', 1)
    start = body.index('            if (admissionProgram is not null && !admissionGranted)')
    end = body.index('            var admission = TryReserveCall', start)
    body = body[:start] + body[end:]
    body = replace_exact(body,
        'ServerRetainedAdmissionPayload? retainedAdmissionPayload = null\n'
        '        , BoundRequestSelection bindingSelection = default, ServerRequestEnvelope preparedRequest = default\n'
        '        )',
        'ServerRetainedAdmissionPayload? retainedAdmissionPayload = null)')
    carried = ('            var boundMethod = retainedAdmissionPayload?.SliceMethod ?? throw new InvalidOperationException("Missing captured binding facts.");\n'
               '            var bindingSelection = retainedAdmissionPayload!.SliceSelection;\n'
               '            request = request with { BoundMethod = boundMethod, BoundCall = bindingSelection.Invocation };\n')
    body = replace_exact(body, '            var request = preparedRequest;\n',
        '            var request = ReadRequestEnvelope(\n'
        '                session, payload, flags, admittedCallState?.Deadline ?? default);\n'
        + (carried if name == 'DispatchRpcAsync' else ''))
    binding_prologue = ('            var serviceInfo = bindingSelection.Registration;\n'
                        '            var boundMethod = bindingSelection.Method;\n')
    if name == 'DispatchRpcAsync':
        binding_prologue += '            var bindingFailure = StaticBindingFailure.None;\n'
    body = replace_exact(body, binding_prologue + '            if (serviceInfo is null)',
                         '            if (!serviceSelection.TryGetService(out var serviceInfo))')
    if name == 'DispatchRpcAsync':
        body = replace_exact(body, '                        StaticBindingWire.Error(bindingFailure),',
            '                        new SharpLinkException(\n'
            '                            SharpLinkErrorCode.Unavailable,\n'
            '                            "RPC module is draining"),')
        # This assignment moved into the queued prologue above, before deadline checks.
        marker = '            request = request with { BoundMethod = boundMethod, BoundCall = bindingSelection.Invocation };\n'
        # There are two occurrences: the carried prologue and immediate selected-service branch.
        index = body.index(marker, body.index('            if (!serviceSelection.TryGetService'))
        body = body[:index] + body[index + len(marker):]
        body = replace_exact(body, STREAM_HEAD_COPY, '')
    else:
        body = replace_exact(body, '            var descriptor = boundMethod.Descriptor;',
                             carried + '            var descriptor = boundMethod.Descriptor;')
    return body


class AdmittedDispatchParityTests(unittest.TestCase):
    def test_selected_payload_copy_resource_skeleton(self):
        source = (SERVER / 'SharpLinkServer.PreAdmissionStreams.cs').read_text(encoding='utf-8')
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
        immediate = (SERVER / f'SharpLinkServer.{immediate_file}.cs').read_text(encoding='utf-8')
        admitted = (SERVER / f'SharpLinkServer.{admitted_file}.cs').read_text(encoding='utf-8')
        self.assertEqual(exact_tokens(expected_admitted(immediate, name)),
                         exact_tokens(method(admitted, name.replace('Dispatch', 'DispatchAdmitted', 1))))

    def test_literal_and_comment_preservation(self):
        self.assertNotEqual(exact_tokens('"a b"'), exact_tokens('"a  b"'))
        self.assertNotEqual(exact_tokens('// a b'), exact_tokens('// a  b'))
        self.assertEqual(exact_tokens('f ( a )'), exact_tokens('f(a)'))

    def test_uncompressed_queued_frame_owner_outlives_dispatch(self):
        source = (SERVER / 'SharpLinkServer.QueuedAdmission.cs').read_text(encoding='utf-8')
        body = method(source, 'AwaitRpcAdmissionAsync')
        self.assertIn('retainedPayload.Payload,', body)
        self.assertIn('if ((flags & ProtocolV2FrameFlags.Compressed) != 0)\n'
                      '                retainedPayload.Dispose();\n'
                      '            if (!dispatchTask.IsCompletedSuccessfully)\n'
                      '                await dispatchTask.ConfigureAwait(false);', body)
        self.assertIn('finally\n        {\n            retainedPayload.Dispose();', body)
        continuation = (SERVER / 'SharpLinkServer.InvocationContinuation.cs').read_text(encoding='utf-8')
        self.assertIn('callState.AttachPayloadOwner(_runtimeContext.Buffers, decodedRequestOwner);', continuation)

    def test_bound_queued_identity_is_carried_not_resolved_again(self):
        for name in ('AdmittedRpcDispatch', 'AdmittedOneWayDispatch'):
            body = (SERVER / f'SharpLinkServer.{name}.cs').read_text(encoding='utf-8')
            self.assertIn('retainedAdmissionPayload?.SliceMethod', body)
            self.assertIn('serviceSelection.TryGetService(out var serviceInfo)', body)
            self.assertNotIn('ResolveStaticRequest(', body)
            self.assertNotIn('Volatile.Read(ref _services)', body)
            self.assertNotRegex(body, r'Stub\.(?:TryResolveMethod|TryGetMethodDescriptor)\(')
        for name, payload in (('InvocationDispatch', 'queuedRetainedPayload'), ('AdmissionDispatch', 'retainedPayload')):
            body = (SERVER / f'SharpLinkServer.{name}.cs').read_text(encoding='utf-8')
            self.assertIn(payload + '!.SliceMethod = boundMethod;', body)


if __name__ == '__main__':
    unittest.main()
