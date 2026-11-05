#!/usr/bin/env python3
"""Twenty-three ordinary parser/handshake candidate UIDs, once each; raw reports remain private.

Extends, never replaces, the reviewed single-build/double-discovery custody
lane. This candidate validation cannot qualify the historical failed full suite, D2, or performance.
"""
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import re
import sys
import tempfile
import time
import xml.etree.ElementTree as ET

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
ACTIVE = {'lane': None, 'errors': [], 'work': None}
SOURCE_TREES = {'src': 'c3a94c896418a5d5ec62646461f17c67dc89bc96',
                'test': '226e6404e69879868bc2f98037624ec357529f49'}
HELPERS = {
    'eng/run-unit-census.py': 'a445781ba97a6c6a963faa9ba4d7e0bb890a96df6395e482ddb3948f8d12e4bf',
    'eng/unit-census-results.py': 'cbf6ce2096576dfe027b8e196144e9cba57860ad99be48c1f89d7ba73a71414e',
    'eng/run-unit-execution.py': 'ede7580fa1a9e37fba16322ef0158a9db8905bc6983c217c8bdad6eb5b784acf',
    'eng/unit-execution-results.py': '5c2f5108f0770bbc582c8f6d208fb66934a2ca3e02f180f3996c05ec31474cb7',
    'eng/package-smoke-process.py': 'ba9f6ae43784ac752acdbfee1af60f33361ba76398a558948da721994449dda3',
    'eng/route59-results.py': 'eb95aaf6a6799b0c216ee3687d71e5e1aeb5651867d515e3a4db16561bfcda8b',
    'eng/retirement-results.py': '6774c8bdd8bd8b3f618c01aee34e429d8a3b8218cdb169e752cd81ab6a9e79eb',
    'eng/tests/test_unit_census.py': 'd12fbbc8e3634fac209aac866eab101d2938d4531ea27e04b9732fb1f7922da5',
    'eng/tests/test_unit_execution.py': 'c9fbae133b363f6ee0150036adfaba80528a3371ba73a5d6140423399672fa19',
    'eng/unit-census.json': '4b4f7cd616a746b153aee47a2fdd9f2685fdeee18a17ee0693743610642209a0',
    'eng/rebase-three-fixture-census.py': '92af1a4c420d5ccaa47d8f5298ef8b6ce8e4befef3b46cc6572912ccb28a12ec',
    'eng/tests/test_three_fixture_census.py': '34669c538d218a34effb86dc9f4e086749320b0e09e493b807e626fd7377b71b',
    'eng/three-fixture-census-rebase.json': 'cd1e92af4898bdc25466bc4caa1a887ff7fba7dea06e3e9f38965ee177b8d179',
    'eng/fixture-migration-baseline/unit-census.json': '2c34614a0bc918c48b860e795f3f331d0243065f05f0c9530e6aa0d430faeb66',
    'eng/fixture-migration-baseline/test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs.txt': 'd2f827e0f45adae15e78c9db4d9c468de1d247c34eb60ed5e01296b59b584e73',
    'eng/fixture-migration-baseline/test/SharpLink.UnitTests/Runtime/SendPumpProgressIsolationTests.cs.txt': '67ffae89508dc0aba7f25f968bec4eb6fa91f997c9acc32784e9defc96a536a9',
    'eng/fixture-migration-baseline/src/SharpLink.Runtime/OwnedFrame.cs.txt': '71948e8baa3494c086d368454460f0422bc635095f91234ea859a3b5eb4bf3d5',
    'eng/fixture-migration-baseline/test/SharpLink.UnitTests/Runtime/Stack13FrameClearTests.cs.txt': '3b4d5efdf3aba44ee1abc34199b517df615c768ec9721d5bc6ddc3c1b7076a3e',
    'eng/tests/test_parser_handshake_validation.py': 'cd1a43f96ae148e1599e9d181ff4dda1cae2137bf04f36767304ae3937142524',
    'eng/fixture-migration-baseline/parser-handshake-diagnostic-result.json': 'f2f09025a1cc611049c32ec83ad6be2ef2cd8a6351efb9191588a02d761424e1',
    'eng/fixture-migration-baseline/test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs.txt': 'b6f8d1b1130a10ee446e63a00b525f37e81a74c1846371b86363225e4a1cf3c8',
    'eng/fixture-migration-baseline/test/SharpLink.UnitTests/Protocol/ProtocolV2ErrorCompatibilityTests.cs.txt': 'c2ab7b602ecc465b366775b8565df52b37a21b159259e188b0f7b0fdcb43f7c8',
}

PROTOCOL = 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs'
NEGOTIATOR = 'test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs'
COMPATIBILITY = 'test/SharpLink.UnitTests/Protocol/ProtocolV2ErrorCompatibilityTests.cs'
PROGRESS = 'test/SharpLink.UnitTests/Runtime/SendPumpProgressIsolationTests.cs'
STACK = 'test/SharpLink.UnitTests/Runtime/Stack13FrameClearTests.cs'
OWNED_FRAME = 'src/SharpLink.Runtime/OwnedFrame.cs'
FLUSH = 'test/SharpLink.UnitTests/Runtime/FlushOperationStressTests.cs'
GRAMMAR = 'test/SharpLink.UnitTests/Protocol/Protocol3FrameAndHandshakeTests.cs'
FRAME = 'src/SharpLink.Runtime/ProtocolV2/ProtocolV2FrameCodec.cs'
PAYLOAD = 'src/SharpLink.Runtime/ProtocolV2/ProtocolV2PayloadCodec.cs'
SOURCE_PINS = {
    'src/SharpLink.Abstractions/ProtocolV2.cs': '1e0b64dcba691ed274e66984fb8b566a10c68e57a7bfc2a5eefb5a506db994a2',
    'src/SharpLink.Runtime/OwnedFrame.cs': 'c3a6ed62dd0d0738b838c76354b2b730c2808c2db37e5c88c821cb95142d3195',
    'src/SharpLink.Runtime/ProtocolV2/ProtocolV2FrameCodec.cs': '737e171bed77f428e4e5d7a0128b8344ae0886cf70300775c9f7df823f588d0e',
    'src/SharpLink.Runtime/ProtocolV2/ProtocolV2Negotiator.cs': 'db95fa43e92596680d5b026ba8981f648770587e437d0addfe880ff28347fb74',
    'src/SharpLink.Runtime/ProtocolV2/ProtocolV2PayloadCodec.cs': 'c3a94f11332f1026f10c72fe20b34666704ef0996e2c7dbdd9073ce43e94c732',
    'src/SharpLink.Runtime/ProtocolViolationReason.cs': 'be29a593585933e97a233b9a5500affdee02a7faf27d2bedd8682b5c61630bc1',
    'test/SharpLink.UnitTests/Protocol/Protocol3FrameAndHandshakeTests.cs': '8804cf11f26fdd938c5ff786c154bf46fca05d6cca5af4e8a65d34d9e19f6fe5',
    'test/SharpLink.UnitTests/Protocol/ProtocolV2ErrorCompatibilityTests.cs': '46c916c63f7d93941eaa02841e0d255e6c5d9b1761e49a23f4057cef3cb5cf1b',
    'test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs': 'd40c522f8bc8805860c9a29288109221545c69c0d598ef5ce6c206efe13e6f57',
    'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs': '00745a416645a7c5e83a934e47900f75a2c656ab6b072ac9029e8aa3b8aeeb85',
    'test/SharpLink.UnitTests/Runtime/FlushOperationStressTests.cs': '98349cbf0ea4545a597e633002cc44557ae1272730d5558487a52f28c05f23b1',
    'test/SharpLink.UnitTests/Runtime/SendPumpProgressIsolationTests.cs': '70e7f967dbd40b85d41b7df22131fe48fdb39a5e59789a5b5034561e2c1a8a7c',
    'test/SharpLink.UnitTests/Runtime/Stack13FrameClearTests.cs': '94ac76dbf52335d3b7703d9fac0aef118716cf8eb16061ce4a203138e9955f0d',
}
# Fixed source identities, never names or argument values copied from reports.
CASES = (
    ('test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 'SharpLink.UnitTests.Protocol', 'ProtocolV2Tests', 'WriterAndParserShouldRoundTripFrame', 13, 28),
    ('test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 'SharpLink.UnitTests.Protocol', 'ProtocolV2Tests', 'HeaderWithEveryByteInSeparateSegmentShouldParse', 50, 68),
    ('test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 'SharpLink.UnitTests.Protocol', 'ProtocolV2Tests', 'RawFrameWriterAndTokenShouldRemainInternalImplementationDetails', 31, 35),
    ('test/SharpLink.UnitTests/Runtime/SendPumpProgressIsolationTests.cs', 'SharpLink.UnitTests.Runtime', 'SendPumpProgressIsolationTests', 'ProgressFlagDoesNotGrowOwnedFrame', 656, 736),
    ('test/SharpLink.UnitTests/Protocol/Protocol3FrameAndHandshakeTests.cs', 'SharpLink.UnitTests.Protocol', 'Protocol3FrameAndHandshakeTests', 'OnlyNewMagicAndPositive63CorrelationAreAccepted', 13, 24),
    ('test/SharpLink.UnitTests/Protocol/Protocol3FrameAndHandshakeTests.cs', 'SharpLink.UnitTests.Protocol', 'Protocol3FrameAndHandshakeTests', 'CancellationHasExactlyOneKnownReasonByte', 27, 33),
    ('test/SharpLink.UnitTests/Runtime/SendPumpProgressIsolationTests.cs', 'SharpLink.UnitTests.Runtime', 'SendPumpProgressIsolationTests', 'StreamCompleteNeverOvertakesQueuedStreamData', 109, 139),
    ('test/SharpLink.UnitTests/Runtime/FlushOperationStressTests.cs', 'SharpLink.UnitTests.Runtime', 'FlushOperationStressTests', 'SessionStopShouldCompleteEveryForceFlushWaiter', 169, 204),
    ('test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 'SharpLink.UnitTests.Protocol', 'ProtocolV2Tests', 'ZeroThroughFourteenHeaderBytesShouldRemainPartial', 38, 47),
    ('test/SharpLink.UnitTests/Runtime/Stack13FrameClearTests.cs', 'SharpLink.UnitTests.Runtime', 'Stack13FrameClearTests', 'FrameInitializationShouldTouchOnlyItsOwnFifteenBytes', 13, 28),
    ('test/SharpLink.UnitTests/Runtime/Stack13FrameClearTests.cs', 'SharpLink.UnitTests.Runtime', 'Stack13FrameClearTests', 'FrameInitializationShouldTouchOnlyItsOwnFifteenBytes', 13, 28),
    ('test/SharpLink.UnitTests/Runtime/Stack13FrameClearTests.cs', 'SharpLink.UnitTests.Runtime', 'Stack13FrameClearTests', 'FrameInitializationShouldTouchOnlyItsOwnFifteenBytes', 13, 28),
    ('test/SharpLink.UnitTests/Runtime/Stack13FrameClearTests.cs', 'SharpLink.UnitTests.Runtime', 'Stack13FrameClearTests', 'ExactWriterLimitAndDisposedLeaseMustRemainEnforced', 31, 42),
    ('test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 'SharpLink.UnitTests.Protocol', 'ProtocolV2Tests', 'MultipleFramesFollowedByHalfFrameShouldPreserveRemainder', 794, 813),
    ('test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 'SharpLink.UnitTests.Protocol', 'ProtocolV2Tests', 'InvalidLengthTypeFlagsAndRequestIdShouldBeRejected', 71, 101),
    ('test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 'SharpLink.UnitTests.Protocol', 'ProtocolV2Tests', 'HealthControlFramesShouldRequireBoundedPayloadAndRequestId', 748, 791),
    ('test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 'SharpLink.UnitTests.Protocol', 'ProtocolV2Tests', 'HandshakeRequestAndResponseShouldRoundTrip', 180, 233),
    ('test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs', 'SharpLink.UnitTests.Protocol', 'ProtocolV2NegotiatorTests', 'ClientOfferShouldContainOnlyPolicyAndAuthenticationInputs', 10, 45),
    ('test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs', 'SharpLink.UnitTests.Protocol', 'ProtocolV2NegotiatorTests', 'ImplementedCapabilitiesShouldRemainExplicitAndProtocolRecognized', 48, 65),
    ('test/SharpLink.UnitTests/Protocol/ProtocolV2ErrorCompatibilityTests.cs', 'SharpLink.UnitTests.Protocol', 'ProtocolV2ErrorCompatibilityTests', 'StructuredErrorShapeShouldRejectPublishedMinorThreePeers', 6, 56),
    ('test/SharpLink.UnitTests/Protocol/Protocol3FrameAndHandshakeTests.cs', 'SharpLink.UnitTests.Protocol', 'Protocol3FrameAndHandshakeTests', 'HandshakeRequestPinsNewOffsetsAndStrictRevisionCapabilitiesAndLengths', 76, 102),
    ('test/SharpLink.UnitTests/Protocol/Protocol3FrameAndHandshakeTests.cs', 'SharpLink.UnitTests.Protocol', 'Protocol3FrameAndHandshakeTests', 'SuccessHandshakeCarriesExactAuthorityAndNoDowngrade', 105, 125),
    ('test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 'SharpLink.UnitTests.Protocol', 'ProtocolV2Tests', 'ParserViolationsShouldCarryFixedLowCardinalityClassification', 136, 154),
)
# Pinned TUnit generator ff2dc5186b05eddc8f6aea03a18784df3aa6e737:
# TestMetadataGenerator.cs 4273-4290 and 4333-4342 use the attributed
# MethodDeclarationSyntax location, converted from zero-based to one-based.
# The three Arguments rows share exact source start 9 and end 28; UIDs stay
# opaque. Sorting only schedules the three distinct discovered identities.
PARAMETERIZED_CASE_INDICES = frozenset((9, 10, 11))
PARAMETERIZED_ATTRIBUTE_LINE = 9
# Candidate-only literal vocabulary. Each exact source line is verified by controls.
ASSERTION_LITERALS = {
    0: (('frame parsed', 'frame-parsed', PROTOCOL, 22),
        ('frame type', 'frame-type', PROTOCOL, 23),
        ('frame flags', 'frame-flags', PROTOCOL, 24),
        ('maximum positive 63-bit request ID', 'maximum-positive-63-bit-request-id', PROTOCOL, 25),
        ('payload length', 'payload-length', PROTOCOL, 26),
        ('frame consumed', 'frame-consumed', PROTOCOL, 27)),
    1: (('frame spans separate byte segments', 'separate-byte-segments', PROTOCOL, 59),
        ('segmented header', 'segmented-header', PROTOCOL, 60),
        ('segmented frame type', 'segmented-frame-type', PROTOCOL, 61),
        ('segmented frame flags', 'segmented-frame-flags', PROTOCOL, 62),
        ('segmented request ID', 'segmented-request-id', PROTOCOL, 63),
        ('segmented cancellation reason length', 'segmented-reason-length', PROTOCOL, 64),
        ('segmented cancellation reason', 'segmented-reason', PROTOCOL, 66),
        ('segmented frame consumed', 'segmented-frame-consumed', PROTOCOL, 67)),
    3: (('both flags must share exactly one private readonly byte field', 'exactly-one-private-readonly-packed-byte', PROGRESS, 668),
        ('the packed flags must not retain either bool auto-property backing field', 'no-separate-bool-backing-fields', PROGRESS, 670),
        ('the comparison must mirror every current field in declaration order, substituting only the packed byte for ForceFlush', 'exact-packed-byte-shape-substitution', PROGRESS, 679),
        ('default frames and their copies must keep both flags false and the packed byte zero', 'default-flags-and-copy-zero', PROGRESS, 692),
        ('packing must preserve the frame owner, payload, length and flush completion', 'packing-preserves-frame-data', PROGRESS, 718),
        ('clearing the original holder must reset both of its flags', 'cleared-original-flags-zero', PROGRESS, 722),
        ('copies must preserve the frame owner, payload, length and flush completion after clearing the original', 'copies-preserve-frame-data', PROGRESS, 732)),
}
STACK_INITIALIZATION_ASSERTIONS = (
    ('header committed length', 'header-committed-length', STACK, 19),
    ('prior bytes unchanged', 'prior-bytes-unchanged', STACK, 20),
    ('uncommitted tail must not be cleared', 'uncommitted-tail-not-cleared', STACK, 21),
    ('magic', 'header-magic', STACK, 24),
    ('empty length', 'empty-payload-length', STACK, 25),
    ('type and flags', 'response-type-and-flags', STACK, 26),
    ('id', 'maximum-positive-correlation-id', STACK, 27),
)
ASSERTION_LITERALS.update({index: STACK_INITIALIZATION_ASSERTIONS for index in PARAMETERIZED_CASE_INDICES})
ASSERTION_LITERALS[12] = (('exact-size header', 'exact-size-header', STACK, 36),
                         ('lease limit', 'lease-limit', STACK, 38))

# Exact current parser/handshake assertion literals; historical replaced messages are unknown.
ASSERTION_LITERALS[13] = (('first frame', 'parser-handshake-14-first-frame', 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 808), ('second frame', 'parser-handshake-14-second-frame', 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 809), ('half frame', 'parser-handshake-14-half-frame', 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 810), ('remainder preserved', 'parser-handshake-14-remainder-preserved', 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 811), ('remainder bytes preserved', 'parser-handshake-14-remainder-bytes-preserved', 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 812))
ASSERTION_LITERALS[14] = (('unmutated header fixture should parse', 'parser-handshake-15-unmutated-header-fixture-should-parse', 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 76),)
ASSERTION_LITERALS[15] = (('health request should parse', 'parser-handshake-16-health-request-should-parse', 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 758), ('health request shape', 'parser-handshake-16-health-request-shape', 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 759), ('health response should parse', 'parser-handshake-16-health-response-should-parse', 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 769), ('health response status', 'parser-handshake-16-health-response-status', 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 771))
ASSERTION_LITERALS[16] = (('handshake minor', 'parser-handshake-17-handshake-minor', 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 201), ('handshake supported capabilities', 'parser-handshake-17-handshake-supported-capabilities', 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 202), ('handshake required capabilities', 'parser-handshake-17-handshake-required-capabilities', 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 203), ('handshake frame limit', 'parser-handshake-17-handshake-frame-limit', 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 204), ('handshake stream window', 'parser-handshake-17-handshake-stream-window', 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 205), ('handshake connection window', 'parser-handshake-17-handshake-connection-window', 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 206), ('handshake auth payload', 'parser-handshake-17-handshake-auth-payload', 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 207), ('handshake compression profiles', 'parser-handshake-17-handshake-compression-profiles', 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 209), ('handshake response compression preference generation', 'parser-handshake-17-handshake-response-compression-preference-generation', 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 211), ('handshake response compression preference', 'parser-handshake-17-handshake-response-compression-preference', 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 213), ('handshake binding limits', 'parser-handshake-17-handshake-binding-limits', 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 214), ('handshake response round-trip', 'parser-handshake-17-handshake-response-round-trip', 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 231), ('handshake response authority', 'parser-handshake-17-handshake-response-authority', 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs', 232))
ASSERTION_LITERALS[17] = (('the implemented policy must advertise the current minor version', 'parser-handshake-18-the-implemented-policy-must-advertise-the-current-minor-version', 'test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs', 30), ('the client offer must publish the complete explicit local policy', 'parser-handshake-18-the-client-offer-must-publish-the-complete-explicit-local-policy', 'test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs', 32), ("the offer must require the caller's capabilities plus the fixed Protocol3 mandatory set", 'parser-handshake-18-the-offer-must-require-the-caller-s-capabilities-plus-the-fixed-protocol3-mandatory-set', 'test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs', 36), ('the offer must publish the complete local limit policy', 'parser-handshake-18-the-offer-must-publish-the-complete-local-limit-policy', 'test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs', 40), ('the negotiator must carry the opaque authentication payload without interpreting it', 'parser-handshake-18-the-negotiator-must-carry-the-opaque-authentication-payload-without-interpreting-it', 'test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs', 42), ('the client provider order must become the offer preference order', 'parser-handshake-18-the-client-provider-order-must-become-the-offer-preference-order', 'test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs', 44))
ASSERTION_LITERALS[18] = (('endpoint implementation support must remain an explicit declaration', 'parser-handshake-19-endpoint-implementation-support-must-remain-an-explicit-declaration', 'test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs', 58), ('every implemented capability must be recognized by the protocol boundary', 'parser-handshake-19-every-implemented-capability-must-be-recognized-by-the-protocol-boundary', 'test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs', 61), ('compression must remain conditional on configured provider support', 'parser-handshake-19-compression-must-remain-conditional-on-configured-provider-support', 'test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs', 64))
ASSERTION_LITERALS[19] = (('Protocol3 uses the fixed revision 1', 'parser-handshake-20-protocol3-uses-the-fixed-revision-1', 'test/SharpLink.UnitTests/Protocol/ProtocolV2ErrorCompatibilityTests.cs', 8), ('published minor-3 peers must be rejected before decoding the structured error shape', 'parser-handshake-20-published-minor-3-peers-must-be-rejected-before-decoding-the-structured-error-shape', 'test/SharpLink.UnitTests/Protocol/ProtocolV2ErrorCompatibilityTests.cs', 11), ('the current-revision server baseline must be valid', 'parser-handshake-20-the-current-revision-server-baseline-must-be-valid', 'test/SharpLink.UnitTests/Protocol/ProtocolV2ErrorCompatibilityTests.cs', 38))
for index in (4, 5, 20, 21):
    ASSERTION_LITERALS[index] = (
        ('Expected Protocol3 structural violation.', 'expected-structural-violation', GRAMMAR, 246),
        ('Protocol3 source regression assertion failed.', 'protocol3-source-assertion', GRAMMAR, 251))

EXCEPTION_CATEGORIES = {
    'System.Exception': 'exception',
    'SharpLink.Runtime.SharpLinkProtocolViolationException': 'protocol-violation',
    'SharpLink.Abstractions.SharpLinkException': 'sharplink-exception',
    'System.InvalidOperationException': 'invalid-operation',
    'System.ArgumentException': 'argument',
    'System.ArgumentNullException': 'argument-null',
    'System.ArgumentOutOfRangeException': 'argument-range',
    'System.NullReferenceException': 'null-reference',
    'System.TypeInitializationException': 'type-initialization',
    'System.TypeLoadException': 'type-load',
    'System.MissingMethodException': 'missing-method',
    'System.MethodAccessException': 'method-access',
    'System.IO.FileNotFoundException': 'file-not-found',
    'System.IO.FileLoadException': 'file-load',
    'System.BadImageFormatException': 'bad-image',
    'System.AggregateException': 'aggregate',
    'TUnit.Assertions.Exceptions.AssertionException': 'assertion',
}
CODES = frozenset(('PD_HELPERS', 'PD_SOURCE', 'PD_SDK', 'PD_RUNTIME', 'PD_SELECTION',
    'PD_PREPARATION', 'PD_COMMANDS', 'PD_ARGV', 'PD_SHAPE', 'PD_LIMIT', 'PD_EXCEPTION',
    'PD_JUNIT', 'PD_REPORTS', 'PD_OWNER', 'PD_EXIT', 'PD_RETRY', 'PD_CASE_FAILED',
    'PD_INCOMPLETE', 'PD_BUDGET', 'PD_PUBLIC'))
RUNTIMES = [{'framework': 'Microsoft.AspNetCore.App', 'version': '10.0.14'},
            {'framework': 'Microsoft.NETCore.App', 'version': '10.0.14'}]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    obj = importlib.util.module_from_spec(spec); spec.loader.exec_module(obj)
    return obj


def verify_source(r, root):
    for name, digest in SOURCE_PINS.items():
        r.require(r.sha(root / name) == digest, 'PD_SOURCE')


def verify_isolated_sdk(lane, census):
    r = lane.r
    dotnet = Path(census.shutil.which('dotnet', path=lane.env['PATH']) or '').resolve()
    root = dotnet.parent
    temporary = os.environ.get('RUNNER_TEMP')
    r.require(type(temporary) is str and Path(temporary).is_absolute() and
              root == Path(temporary) / 'parser-handshake-fixtures-dotnet' and dotnet.name == 'dotnet' and dotnet.is_file(), 'PD_SDK')
    r.legacy.directory_identity_strict(root)
    for name in ('DOTNET_ROOT', 'DOTNET_ROOT_X64'):
        if name in lane.env: r.require(Path(lane.env[name]) == root, 'PD_SDK')
    r.require(lane.env.get('DOTNET_MULTILEVEL_LOOKUP') == '0', 'PD_SDK')


def selected(r, ordinary):
    output = []; groups = {}; consumed = {}
    for index, case in enumerate(CASES):
        source, namespace, cls, method, start, end = case
        if case not in groups:
            matches = [(uid, row) for uid, row in ordinary.items()
                       if row['type']['namespace'] == namespace and
                       row['type']['typeName'] == cls and
                       row['type']['methodName'] == method]
            r.require(len(matches) == CASES.count(case), 'PD_SELECTION')
            for uid, row in matches:
                r.require(row['uid'] == uid and type(uid) is str and uid and
                          not uid.startswith(('-', '@')) and len(uid) <= 4096 and
                          not any(ord(c) < 32 for c in uid), 'PD_SELECTION')
            groups[case] = sorted(matches, key=lambda pair: pair[0]); consumed[case] = 0
        uid, row = groups[case][consumed[case]]; consumed[case] += 1
        loc = row['location']
        valid_start = (loc.get('lineStart') == PARAMETERIZED_ATTRIBUTE_LINE
                       if index in PARAMETERIZED_CASE_INDICES
                       else type(loc.get('lineStart')) is int and start - 1 <= loc['lineStart'] <= start)
        r.require(set(loc) == {'file', 'lineStart', 'lineEnd'} and loc['file'] == '/_/' + source and
                  type(loc['lineStart']) is int and valid_start and
                  type(loc['lineEnd']) is int and loc['lineEnd'] == end, 'PD_SELECTION')
        output.append((uid, row))
    r.require(len({uid for uid, _ in output}) == 23 and
              all(consumed[case] == len(rows) for case, rows in groups.items()), 'PD_SELECTION')
    return output


def keys(r, obj, required, optional=()):
    r.require(type(obj) is dict and set(required) <= set(obj) <= set(required) | set(optional), 'PD_SHAPE')


def string(r, value):
    r.require(type(value) is str and len(value) <= 1024 * 1024, 'PD_SHAPE')


def number(r, value):
    r.require(type(value) in (int, float) and math.isfinite(value), 'PD_SHAPE')


def strings(r, values):
    r.require(type(values) is list and len(values) <= 1024, 'PD_LIMIT')
    for value in values: string(r, value)


def pairs(r, values):
    r.require(type(values) is list and len(values) <= 1024, 'PD_LIMIT')
    for value in values:
        keys(r, value, ('key', 'value'))
        for item in value.values(): string(r, item)


def exception_shape(r, exception, depth=0):
    r.require(depth < 16, 'PD_LIMIT')
    keys(r, exception, ('type', 'message'), ('stackTrace', 'innerException'))
    for name in ('type', 'message', 'stackTrace'):
        if name in exception: string(r, exception[name])
    if 'innerException' in exception: exception_shape(r, exception['innerException'], depth + 1)


def native_shape(r, native):
    required = ('schemaVersion', 'publicationGeneration', 'assemblyName', 'machineName', 'timestamp',
                'tunitVersion', 'operatingSystem', 'runtimeVersion', 'totalDurationMs', 'summary', 'groups')
    optional = ('filter', 'artifactUrl', 'commitSha', 'branch', 'pullRequestNumber', 'repositorySlug', 'sourceLinks', 'spans')
    keys(r, native, required, optional)
    for name in set(required + optional) - {'schemaVersion', 'totalDurationMs', 'summary', 'groups', 'sourceLinks', 'spans'}:
        if name in native: string(r, native[name])
    number(r, native['totalDurationMs'])
    if 'sourceLinks' in native:
        keys(r, native['sourceLinks'], ('lineUrl', 'rangeUrl'), ('rawUrl',))
        for value in native['sourceLinks'].values(): string(r, value)
    # Exact pinned optional tracing schema; these fields are never projected.
    spans = native.get('spans', [])
    r.require(type(spans) is list and len(spans) <= 4096, 'PD_LIMIT')
    for span in spans:
        keys(r, span, ('traceId', 'spanId', 'name', 'source', 'kind', 'startTimeMs', 'durationMs', 'status'),
             ('parentSpanId', 'spanType', 'statusMessage', 'tags', 'events', 'links'))
        for name, value in span.items():
            if name in ('startTimeMs', 'durationMs'): number(r, value)
            elif name == 'tags': pairs(r, value)
            elif name not in ('events', 'links'): string(r, value)
        for field in ('events', 'links'):
            values = span.get(field, []); r.require(type(values) is list and len(values) <= 1024, 'PD_LIMIT')
            for value in values:
                if field == 'links':
                    keys(r, value, ('traceId', 'spanId'))
                    for item in value.values(): string(r, item)
                else:
                    keys(r, value, ('name', 'timestampMs'), ('tags',))
                    string(r, value['name']); number(r, value['timestampMs'])
                    if 'tags' in value: pairs(r, value['tags'])
    r.require(type(native['groups']) is list and len(native['groups']) == 1, 'PD_SHAPE')
    group = native['groups'][0]; keys(r, group, ('namespace', 'className', 'summary', 'tests'))
    r.require(type(group['tests']) is list and len(group['tests']) == 1, 'PD_SHAPE')
    test = group['tests'][0]
    required = ('id', 'displayName', 'methodName', 'className', 'status', 'durationMs', 'filePath', 'lineNumber', 'endLineNumber')
    optional = ('startTime', 'endTime', 'exception', 'output', 'errorOutput', 'categories', 'customProperties',
                'sourceRelativePath', 'skipReason', 'retryAttempt', 'attempts', 'traceId', 'spanId', 'additionalTraceIds')
    keys(r, test, required, optional)
    r.require(not {'retryAttempt', 'attempts'} & set(test), 'PD_RETRY')
    for name in set(required + optional) - {'durationMs', 'lineNumber', 'endLineNumber', 'exception', 'categories', 'customProperties', 'additionalTraceIds', 'retryAttempt', 'attempts'}:
        if name in test: string(r, test[name])
    for name in ('categories', 'additionalTraceIds'):
        if name in test: strings(r, test[name])
    if 'customProperties' in test: pairs(r, test['customProperties'])
    if 'exception' in test: exception_shape(r, test['exception'])
    r.require(test['status'] in ('passed', 'failed', 'error'), 'PD_EXCEPTION')
    r.require(('exception' in test) == (test['status'] != 'passed') and 'skipReason' not in test, 'PD_EXCEPTION')
    return test


def junit_shape(r, execution, text, test):
    # Existing classifier checks totals, exact discovery identities, multiplicity
    # and state mapping first. This adds the pinned writer's attribute boundary.
    root = ET.fromstring(text); suite = root[0]
    attrs = {'name', 'tests', 'failures', 'errors', 'skipped', 'time', 'timestamp'}
    r.require(set(root.attrib) == attrs and set(suite.attrib) == attrs | {'hostname'}, 'PD_JUNIT')
    properties = suite.findall('properties')
    r.require(len(properties) == 1 and not properties[0].attrib, 'PD_JUNIT')
    seen = set()
    for item in properties[0]:
        r.require(item.tag == 'property' and set(item.attrib) == {'name', 'value'} and len(item) == 0, 'PD_JUNIT')
        name = item.get('name'); r.require(name in ('framework', 'platform', 'runtime', 'filter') and name not in seen, 'PD_JUNIT'); seen.add(name)
    r.require({'framework', 'platform', 'runtime'} <= seen, 'PD_JUNIT')
    cases = suite.findall('testcase'); r.require(len(cases) == 1 and set(cases[0].attrib) == {'name', 'classname', 'time'}, 'PD_JUNIT')
    if test['status'] != 'passed':
        child = cases[0][0]; exception = test['exception']
        r.require(set(child.attrib) == {'message', 'type'} and len(child) == 0, 'PD_JUNIT')
        r.require(child.get('message') == execution.xml_name(exception['message']) and child.get('type') == exception['type'], 'PD_JUNIT')
        # JUnit text is Exception.ToString(), not native stackTrace. It remains
        # original private evidence and is deliberately never copied/rendered.


def projected_exception(r, index, exception):
    """Only fixed vocabulary, bounded numerics and verified source literals."""
    exception_shape(r, exception)
    source, namespace, cls, method, start, end = CASES[index]
    result = []; depth = 0
    current = exception
    while current is not None:
        category = EXCEPTION_CATEGORIES.get(current['type'], 'unknown')
        row = {'depth': depth, 'category': category, 'assertionTag': 'unknown', 'sourceFrames': []}
        known = {message: (tag, path, line) for message, tag, path, line in ASSERTION_LITERALS.get(index, ())}
        if category == ('invalid-operation' if source in (STACK, GRAMMAR) else 'exception') and current['message'] in known:
            tag, path, line = known[current['message']]
            row.update(assertionTag=tag, exactMessageSource={'source': path, 'line': line})
        protocol_messages = {
            (0, 'Frame Request must use a positive 63-bit correlation ID.'): ('request-id-positive-63-bit', FRAME, 173),
            (1, 'Cancel payload must contain exactly one reason byte.'): ('cancel-payload-one-reason-byte', PAYLOAD, 390),
        }
        match = protocol_messages.get((index, current['message'])) if category == 'protocol-violation' else None
        if match:
            tag, path, line = match
            row.update(assertionTag=tag, exactMessageSource={'source': path, 'line': line})
        # The assertion text is interpolated in public source. Only its exact
        # fixed template plus two bounded decimal sizes yields numeric facts.
        if index == 3 and category == 'exception':
            match = re.fullmatch(r'OwnedFrame must not grow with the progress flag \(measured ([1-9][0-9]{0,3}) vs ([1-9][0-9]{0,3})\)', current['message'])
            if match and all(1 <= int(value) <= 4096 for value in match.groups()):
                row.update(assertionTag='owned-frame-size-comparison', measuredBytes=int(match[1]), comparatorBytes=int(match[2]),
                           exactMessageSource={'source': PROGRESS, 'line': 684})
        # Exact test-method frames only. No arbitrary signature, path or line is
        # returned. Unsupported frames are counted, never stringified.
        frame_map = {}
        full = namespace + '.' + cls
        for line in range(start, end + 1):
            parameters = 'Int32 capacity' if index in PARAMETERIZED_CASE_INDICES else ''
            frame_map[f'   at {full}.{method}({parameters}) in /_/{source}:line {line}'] = (method, line)
        ensure_line = {PROTOCOL: 903, PROGRESS: 897, NEGOTIATOR: 624, COMPATIBILITY: 74}.get(source)
        if ensure_line is not None:
            frame_map[f'   at {full}.Ensure(Boolean condition, String message) in /_/{source}:line {ensure_line}'] = ('Ensure', ensure_line)
        if source == STACK:
            frame_map[f'   at {full}.Check(Boolean condition, String message) in /_/{source}:line 71'] = ('Check', 71)
        if source == GRAMMAR:
            frame_map[f'   at {full}.Ensure(Boolean condition) in /_/{source}:line 251'] = ('Ensure', 251)
            frame_map[f'   at {full}.ExpectViolation(Action action) in /_/{source}:line 246'] = ('ExpectViolation', 246)
        if index in (13, 14, 15):
            frame_map[f'   at {full}.CreateFrame(ProtocolV2FrameType type, ProtocolV2FrameFlags flags, UInt64 requestId, Byte[] payload) in /_/{source}:line 835'] = ('CreateFrame', 835)
        lines = current.get('stackTrace', '').splitlines()
        r.require(len(lines) <= 4096, 'PD_LIMIT')
        for raw in lines:
            if raw in frame_map:
                member, line = frame_map[raw]
                row['sourceFrames'].append({'source': source, 'member': member, 'line': line})
        row['unrecognizedStackLineCount'] = len(lines) - len(row['sourceFrames'])
        result.append(row); current = current.get('innerException'); depth += 1
    return result


def classify(execution, native, junit, index, selection):
    r = execution.census
    test = native_shape(r, native)
    uid, row = selection
    facts = {}
    passed = execution.classify(native, junit, {uid: row}, set(), facts, RUNTIMES)
    junit_shape(r, execution, junit, test)
    r.require(native['runtimeVersion'] == '.NET 10.0.14' and facts['retriesObserved'] is False, 'PD_RUNTIME')
    source, namespace, cls, method, _, _ = CASES[index]
    return {'case': index + 1, 'source': source, 'method': namespace + '.' + cls + '.' + method,
            'status': test['status'], 'selectedPassed': passed,
            'nativeCanonicalSHA256': r.digest(native), 'junitDecodedTextSHA256': hashlib.sha256(junit.encode()).hexdigest(),
            'resultUIDSetSHA256': facts['resultUIDSetSHA256'],
            'exceptions': projected_exception(r, index, test['exception']) if 'exception' in test else []}


def case_argv(execution, dotnet, dll, uid, report):
    return execution.census.legacy.case_argv(dotnet, dll, uid, report, '60s')


def lane_type(census, execution):
    class DiagnosticLane(census.Lane):
        def run(self, label, argv, cwd=None, seconds=20):
            output = super().run(label, argv, cwd, seconds)
            if label == 'clone-head':
                for name, expected in SOURCE_TREES.items():
                    self.r.require(self.git('diagnostic-' + name + '-tree', 'rev-parse', 'HEAD:' + name, cwd=self.source) == expected, 'PD_SOURCE')
                verify_source(self.r, self.source)
            if label == 'sdk-version':
                self.r.require(self.r.stable_read(output)[0].decode().strip() == '10.0.114', 'PD_SDK')
            if label == 'sdk-runtimes':
                dotnet = Path(argv[0]); text = self.r.stable_read(output)[0].decode()
                expected = {f"{row['framework']} {row['version']} [{dotnet.parent / 'shared' / row['framework']}]" for row in RUNTIMES}
                self.r.require(set(text.splitlines()) == expected and len(text.splitlines()) == 2, 'PD_RUNTIME')
            return output
    return DiagnosticLane


def handoff_selections(lane, ordinary, explicit):
    r = lane.r
    r.require(lane.identity['sdk'] == '10.0.114' and
              sorted(lane.identity['installedRuntimes'], key=lambda x: x['framework']) == RUNTIMES, 'PD_RUNTIME')
    selections = selected(r, ordinary)
    r.require(not ({uid for uid, _ in selections} & explicit), 'PD_SELECTION')
    r.require(lane.sequence == 32, 'PD_COMMANDS')
    # Selection and every independent seal must finish under the ORIGINAL
    # preparation deadline. The body phase cannot revive expired preparation.
    lane.check()
    return selections


def run_cases(lane, execution, selections, overall_stop):
    r = lane.r
    lane.check()
    now = time.monotonic()
    r.require(now < lane.stop and now < overall_stop, 'PD_BUDGET')
    lane.stop = min(overall_stop, now + 1500)
    # The original checkout remains an independently sealed, unbuilt input.
    # Rebase controls must use it, not the clone containing generated bin/obj.
    lane.run('parser-handshake-validation-python-controls', [sys.executable, '-B', ROOT / 'eng/tests/test_parser_handshake_validation.py'], ROOT, 60)
    r.require(lane.sequence == 33, 'PD_COMMANDS')
    dotnet = next(path for path in lane.inputs if path.name == 'dotnet' and path.parent in lane.trees)
    dll = lane.source / 'test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll'
    lane.env['TUNIT_ENABLE_JUNIT_REPORTER'] = '1'
    for index, selection in enumerate(selections):
        lane.check(); verify_source(r, lane.source)
        r.require(r.sha(dll) == lane.identity['binarySHA256'], 'UC_INPUT_CHANGED')
        uid, _ = selection; label = 'parser-handshake-validation-case-' + str(index + 1)
        report = lane.work / (label + '-results'); report.mkdir(mode=0o700)
        report_identity = r.legacy.directory_identity_strict(report); r.legacy.verify_directory(report, report_identity, empty=True)
        argv = case_argv(execution, dotnet, dll, uid, report)
        r.require(argv == [str(x) for x in (dotnet, dll, '--filter-uid', uid, '--maximum-parallel-tests', '1', '--timeout', '60s',
                  '--results-directory', report, '--report-html', '--report-html-filename', report / 'session.html', '--junit-output-path', report / 'junit.xml')], 'PD_ARGV')
        execution.verify_body_inputs(execution.body_argv(dotnet, dll, report), dotnet, dll, report, lane.env)
        try: lane.run(label, argv, lane.source, 180)
        except BaseException as error: lane.record(error)
        state = lane.commands.get(label)
        # A timeout/unknown owner is not a closed result domain, even if files exist.
        r.require(execution.owner_closed(state, label, argv), 'PD_OWNER')
        snapshot = execution.report_snapshot(report, report_identity)
        r.require(set(snapshot) == {'.'} | execution.PARENT_REPORTS, 'PD_REPORTS')
        lane.seals[report] = snapshot; lane.seal_domains[report] = ('unknown', None)
        lane.check()
        native = r.read_json(report / 'session.tunit-report.json')
        junit = r.stable_read(report / 'junit.xml')[0].decode('utf-8-sig')
        result = classify(execution, native, junit, index, selection)
        exit_code = state['receipt']['exitCode']
        r.require(type(exit_code) is int and exit_code == (0 if result['selectedPassed'] else 2), 'PD_EXIT')
        result.update(ownerClosed=True, bodyExitCode=exit_code,
                      nativeFileSHA256=r.sha(report / 'session.tunit-report.json'), junitFileSHA256=r.sha(report / 'junit.xml'))
        lane.case_facts.append(result)
        lane.check()
        if not result['selectedPassed']: lane.record(r.Rejected('PD_CASE_FAILED'))
    r.require(lane.sequence == 56 and len(lane.case_facts) == 23, 'PD_COMMANDS')
    lane.candidate_complete = True


def rejections(lane, execution):
    allowed = CODES | execution.CODES | lane.r.PUBLIC_REJECTION_CODES
    queue = list(lane.errors); seen = set(); codes = set(); unknown = 0
    while queue:
        error = queue.pop()
        if not isinstance(error, BaseException) or id(error) in seen: continue
        seen.add(id(error))
        if type(error) is lane.r.Rejected and type(error.code) is str and error.code in allowed: codes.add(error.code)
        elif not isinstance(error, BaseExceptionGroup): unknown += 1
        if isinstance(error, BaseExceptionGroup): queue.extend(error.exceptions)
        if error.__cause__ is not None: queue.append(error.__cause__)
        if error.__context__ is not None: queue.append(error.__context__)
    return {'codes': sorted(codes), 'unclassifiedExceptionNodes': unknown}


def public_identity(r, identity):
    output = {}
    for name in ('head', 'tree', 'binarySHA256', 'sourceManifestSHA256', 'sdkManifestSHA256', 'packageManifestSHA256', 'providerProofSHA256'):
        value = identity.get(name)
        if value is not None:
            r.require(type(value) is str and re.fullmatch('[0-9a-f]{' + ('40' if name in ('head', 'tree') else '64') + '}', value), 'PD_PUBLIC')
            output[name] = value
    return output


def finish(lane, census, execution):
    r = lane.r
    try: lane.check()
    except BaseException as error: lane.record(error)
    complete = lane.candidate_complete and len(lane.case_facts) == 23
    qualified = bool(complete and not lane.errors and lane.durable and time.monotonic() < lane.stop)
    try:
        summary = {'schemaVersion': 1, 'scope': 'twenty-three-ordinary-parser-handshake-fixture-validation',
            'identity': public_identity(r, lane.identity),
            'candidateCatalogSHA256': HELPERS['eng/unit-census.json'],
            'frozenCatalogSHA256': HELPERS['eng/fixture-migration-baseline/unit-census.json'],
            'sourceTrees': dict(SOURCE_TREES), 'requiredSdk': '10.0.114', 'requiredRuntime': '10.0.14',
            'preparationQualifiedAtHandoff': lane.preparation_complete,
            'candidateValidationComplete': complete, 'candidateValidationQualified': qualified,
            'allSelectedPassed': bool(complete and all(row['selectedPassed'] for row in lane.case_facts)),
            'candidateParentHead': 'd6c4d41bca201b2bd82e2e917cfd93d701976b5a',
            'fullSuiteQualified': False, 'historicalOrdinaryFailureCount': 290, 'requiredSelectedCaseCount': 23,
            'expectedOrdinaryMetadataCount': 2879, 'expectedExplicitMetadataCount': 5,
            'historicalFullRunId': 41940224644, 'historicalDiagnosticRunId': 41947099090,
            'historicalFirstMigrationRun': {'runId': 41950562552, 'head': '4209233576c2100e0d5dc625308f49b76d500169',
                'complete': True, 'passed': 5, 'nonpassing': 1, 'qualified': False,
                'case4': {'measuredBytes': 64, 'comparatorBytes': 56, 'shapeGuardsPassed': True}},
            'historicalPackingRun': {'runId': 41952557024, 'head': '3f2f1d03da65844fba6f367811545f195ccf5206',
                'complete': True, 'passed': 8, 'nonpassing': 0, 'qualified': True, 'fullSuiteQualified': False},
            'historicalThirteenCaseRun': {'runId': 41954983558, 'head': '5e021af675f9252a7bb2ac4023d78bf8bec0aa92',
                'complete': True, 'passed': 13, 'nonpassing': 0, 'qualified': True, 'fullSuiteQualified': False},
            'historicalParserHandshakeDiagnostic': {'runId': 41957258310, 'jobId': 127015025107,
                'head': 'd6c4d41bca201b2bd82e2e917cfd93d701976b5a', 'complete': True,
                'passed': 4, 'nonpassing': 7, 'errorOccurrences': 21, 'qualified': False, 'fullSuiteQualified': False,
                'evidenceSHA256': HELPERS['eng/fixture-migration-baseline/parser-handshake-diagnostic-result.json']},
            'errorOccurrences': len(lane.errors), 'rejections': rejections(lane, execution),
            'evidenceDurable': lane.durable, 'cases': lane.case_facts, 'exit': 0 if qualified else 1}
        r.write_json(lane.evidence, 'parser-handshake-validation-summary.json', summary); lane.extend_evidence(['parser-handshake-validation-summary.json'])
        lane.check(); rendered = json.dumps(summary, sort_keys=True, allow_nan=False)
        r.require(time.monotonic() < lane.stop, 'PD_BUDGET')
        print(rendered, flush=True)
        r.require(time.monotonic() < lane.stop, 'PD_BUDGET')
        return 0 if qualified else 1
    except BaseException as error:
        lane.record(error)
        try: os.write(2, b'{"terminalDiagnostic":true,"candidateValidationQualified":false,"fullSuiteQualified":false,"exit":1}\n')
        except BaseException as secondary: lane.record(secondary)
        try: os.write(2, census.terminal_diagnostic(lane, 'guard'))
        except BaseException as secondary: lane.record(secondary)
        return 1


def main():
    global ACTIVE
    lane = None; census = None; execution_runner = None
    ACTIVE = {'lane': None, 'errors': [], 'work': None}
    try:
        started = time.monotonic()
        work = Path(tempfile.mkdtemp(prefix='slphv-', dir='/tmp')); os.chmod(work, 0o700); ACTIVE['work'] = work
        # Fixed reviewed helper hashes before importing executable helper content.
        for name, expected in HELPERS.items():
            path = ROOT / name
            if path.is_symlink() or hashlib.sha256(path.read_bytes()).hexdigest() != expected:
                raise RuntimeError('PD_HELPERS')
        census = load('pure_census', ROOT / 'eng/run-unit-census.py')
        execution = load('pure_execution_results', ROOT / 'eng/unit-execution-results.py')
        execution_runner = load('pure_execution_runner', ROOT / 'eng/run-unit-execution.py')
        owner = load('pure_owner', ROOT / 'eng/package-smoke-process.py'); owner.enable_subreaper()
        lane = lane_type(census, execution)(work, execution.census, owner)
        ACTIVE['lane'] = lane; ACTIVE['errors'] = lane.errors
        lane.case_facts = []; lane.candidate_complete = False; lane.preparation_complete = False
        lane.stop = min(lane.stop, started + 3600)
        try:
            for name, expected in HELPERS.items(): lane.r.require(lane.r.sha(ROOT / name) == expected, 'PD_HELPERS')
            verify_isolated_sdk(lane, census)
            ordinary, explicit = execution_runner.prepare(lane, census, execution)
            selections = handoff_selections(lane, ordinary, explicit)
            lane.preparation_complete = True
            run_cases(lane, execution, selections, started + 5100)
        except BaseException as error: lane.record(error)
        return finish(lane, census, execution)
    except BaseException as error:
        if lane is not None: lane.record(error)
        else:
            ACTIVE['errors'].append(error)
            if census is not None and ACTIVE['work'] is not None: census.persist(ACTIVE['work'], ACTIVE['errors'], 999)
            elif execution_runner is not None: execution_runner.bootstrap_persist(ACTIVE['work'], ACTIVE['errors'])
        try: os.write(2, b'PARSER_HANDSHAKE_VALIDATION_BOOTSTRAP_FAILED\n')
        except BaseException as secondary: ACTIVE['errors'].append(secondary)
        return 1


def guarded_main():
    try: return main()
    except BaseException as error:
        ACTIVE['errors'].append(error)
        try: os.write(2, b'PARSER_HANDSHAKE_VALIDATION_OUTERMOST_FAILED\n')
        except BaseException as secondary: ACTIVE['errors'].append(secondary)
        return 1


if __name__ == '__main__': raise SystemExit(guarded_main())
