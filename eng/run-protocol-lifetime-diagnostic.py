#!/usr/bin/env python3
"""Twenty-one unchanged cases: Protocol controls first, then lifetime failures.

Single build, double source-bound discovery, one fresh owned process per case.
Only fixed vocabulary, source-bound frames, and opaque UID hashes are public.
Historical full-suite failure stays separate and can never be qualified here.
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
SOURCE_TREES = {'src': 'c3a94c896418a5d5ec62646461f17c67dc89bc96', 'test': 'fb8f193f979ba936fcd3e51eff98562f27f06a50'}
HELPERS = {'eng/run-unit-census.py': 'a445781ba97a6c6a963faa9ba4d7e0bb890a96df6395e482ddb3948f8d12e4bf',
 'eng/unit-census-results.py': 'cbf6ce2096576dfe027b8e196144e9cba57860ad99be48c1f89d7ba73a71414e',
 'eng/run-unit-execution.py': 'ede7580fa1a9e37fba16322ef0158a9db8905bc6983c217c8bdad6eb5b784acf',
 'eng/unit-execution-results.py': '5e98431f1f9834c2d53156719def8c150db0d08bd06876e2207d50c068a4ad58',
 'eng/package-smoke-process.py': 'ba9f6ae43784ac752acdbfee1af60f33361ba76398a558948da721994449dda3',
 'eng/route59-results.py': 'eb95aaf6a6799b0c216ee3687d71e5e1aeb5651867d515e3a4db16561bfcda8b',
 'eng/retirement-results.py': '6774c8bdd8bd8b3f618c01aee34e429d8a3b8218cdb169e752cd81ab6a9e79eb',
 'eng/tests/test_unit_census.py': 'd12fbbc8e3634fac209aac866eab101d2938d4531ea27e04b9732fb1f7922da5',
 'eng/tests/test_unit_execution.py': 'c9fbae133b363f6ee0150036adfaba80528a3371ba73a5d6140423399672fa19',
 'eng/unit-census.json': '3d05292b01e9ec133f10fad420593895e122469eb82528d6190ac2e9a8dcde12',
 'eng/rebase-three-fixture-census.py': 'f2bf87c4dbc784fdd53f09061c091433e696c2849538e5841026c3d31786bdbc',
 'eng/tests/test_three_fixture_census.py': '7141ec44f8be2aceae85044a7aa2560d4bc15a05b4cc90d3987d58cc803a3ca3',
 'eng/three-fixture-census-rebase.json': 'a6649eca0a81d52e72de2307816692fa3b5e8d33a510d5e61e7cf9e0cf78c05f',
 'eng/fixture-migration-baseline/unit-census.json': '2c34614a0bc918c48b860e795f3f331d0243065f05f0c9530e6aa0d430faeb66',
 'eng/fixture-migration-baseline/test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs.txt': 'd2f827e0f45adae15e78c9db4d9c468de1d247c34eb60ed5e01296b59b584e73',
 'eng/fixture-migration-baseline/test/SharpLink.UnitTests/Runtime/SendPumpProgressIsolationTests.cs.txt': '67ffae89508dc0aba7f25f968bec4eb6fa91f997c9acc32784e9defc96a536a9',
 'eng/fixture-migration-baseline/src/SharpLink.Runtime/OwnedFrame.cs.txt': '71948e8baa3494c086d368454460f0422bc635095f91234ea859a3b5eb4bf3d5',
 'eng/fixture-migration-baseline/test/SharpLink.UnitTests/Runtime/Stack13FrameClearTests.cs.txt': '3b4d5efdf3aba44ee1abc34199b517df615c768ec9721d5bc6ddc3c1b7076a3e',
 'eng/fixture-migration-baseline/parser-handshake-diagnostic-result.json': 'f2f09025a1cc611049c32ec83ad6be2ef2cd8a6351efb9191588a02d761424e1',
 'eng/fixture-migration-baseline/test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs.txt': 'b6f8d1b1130a10ee446e63a00b525f37e81a74c1846371b86363225e4a1cf3c8',
 'eng/fixture-migration-baseline/test/SharpLink.UnitTests/Protocol/ProtocolV2ErrorCompatibilityTests.cs.txt': 'c2ab7b602ecc465b366775b8565df52b37a21b159259e188b0f7b0fdcb43f7c8',
 'eng/full-regression-history/original-ordinary-result.json': 'dae32c0724ad78dca30234d7d109e45e56566efe900aa854b6461edb4a50f214',
 'eng/full-regression-history/twenty-three-case-result.json': '363a314514e08f19abee140e4ea9c2e692e9836aaeebf02d317036b012fc07af',
 'eng/tests/test_pure_negotiation_full_regression.py': 'b5045fe69f50d59b48642ca1fc2cb591111f00d3b09a71ffc918e13f705d55c7',
 'eng/timed-stream-full-results.py': '1b1c0c9d56d240dc981d0e24ceedbebc1a13c1d138e6203bd9d67fdd8ad674ef',
 'eng/tests/test_timed_stream_full_results.py': 'eee2e402c75a89ba1157c1d13284064fb3d2394cc344dfdf567a57b4dacf8ca1',
 'eng/stream-negotiation-history/boolean-display-name-proof.json': '1917416924ccc1f506b7922aff4872d8b7bac20bfa2147da12347129c4cfed6e',
 'eng/pure-negotiation-history/full-ordinary-result.json': '8b5c5a42210d118055655b75e456cf65f114b9e88fb63debbd8b122c55997e56',
 'eng/pure-negotiation-history/sixteen-case-diagnostic-result.json': '57700796654908f5d3abb3064c04c746384253bad0668f5b4e9dab387373a55a',
 'eng/pure-negotiation-history/diagnostic-negotiator-preimage.cs.txt': 'd40c522f8bc8805860c9a29288109221545c69c0d598ef5ce6c206efe13e6f57',
 'eng/pure-negotiation-full-history/sixteen-case-success.json': 'b6ba59c1cdf4cccc61db90a31226f342286ba4cdb8f8bfc323e7f5f6127fd90d',
 'eng/run-pure-negotiation-full-regression.py': 'd708aef5417b425f21deb6a0c2a1281af1790af6a636a5ec42da20c5041165f6',
 'eng/protocol-lifetime-history/current-full-result.json': '29cf0a0f88a3f97b9a620c374b276cd9c900fba8f53aca2268ed869d9b663d19',
 'eng/protocol-lifetime-history/argument-display-name-proof.json': '37147528b72191820bfc81686c74f37baf6ac2f62369d6222c58697f81edd82b',
 'eng/tests/test_protocol_lifetime_diagnostic.py': '88cfeee33501b84e95b18be25d97c8b72b2c3953a86f09518511a57a95421622'}
SOURCE_PINS = {'src/SharpLink.Client/SharpLinkClient.ContractRoutes.cs': '1fb2ca46f003cb3c675c5f6bd1a7f694d26f3bd9409e313248c1945d9923e298',
 'src/SharpLink.Client/SharpLinkClient.cs': '0096ed43927669451702fc148fd0e933a05ec4005ee5a0c5b2bd4ff24bbf0af8',
 'src/SharpLink.Runtime/FrameworkTaskSupervisor.cs': 'f0bd27e13492e2888dc1c850eaafeabd2166568704543fe88bf4d1f42e068c51',
 'src/SharpLink.Runtime/ProtocolV2/ProtocolV2PayloadCodec.cs': 'c3a94f11332f1026f10c72fe20b34666704ef0996e2c7dbdd9073ce43e94c732',
 'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs': 'de1e0217562144fa8d2b427e72d321aa71928d5007cd28ac37aae19a2438ab2f',
 'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs': '1ecb51b1ebc4c880a927e9c94c27522a151efbd4fe67f049ecb2903a3cb45191',
 'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs': '9e92501f78b14811cb2bbbdcbf59fcbd9e93b0a93d6d9d428d88c0bad8209bca',
 'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs': '00745a416645a7c5e83a934e47900f75a2c656ab6b072ac9029e8aa3b8aeeb85',
 'test/SharpLink.UnitTests/Protocol/ResponseCompressionPreferenceProtocolTests.cs': 'f903e42a339e9e07e0dab9cf4d496de1e0266017cd800362a800666a08172970',
 'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs': '00417460067c00b87f52882434d161480aea0401d4a79c1018e5b12d836dce8c'}
CASES = (('test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs',
  'SharpLink.UnitTests.Protocol',
  'ProtocolV2Tests',
  'HandshakeResponseShouldRequireCompressionCapabilityAndProfileTogether',
  236,
  295),
 ('test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs',
  'SharpLink.UnitTests.Protocol',
  'ProtocolV2Tests',
  'HandshakeCapabilitiesShouldRejectInconsistentOrUnknownSets',
  298,
  348),
 ('test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs',
  'SharpLink.UnitTests.Protocol',
  'ProtocolV2Tests',
  'CancelReasonShouldRoundTripAndEnforceNegotiatedShape',
  393,
  454),
 ('test/SharpLink.UnitTests/Protocol/ResponseCompressionPreferenceProtocolTests.cs',
  'SharpLink.UnitTests.Protocol',
  'ResponseCompressionPreferenceProtocolTests',
  'HandshakeShouldRoundTripFixedPreferenceGeneration',
  9,
  33),
 ('test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs',
  'SharpLink.UnitTests.Protocol',
  'ProtocolV2Tests',
  'HandshakeRequestAndResponseShouldRoundTrip',
  180,
  233),
 ('test/SharpLink.UnitTests/Protocol/ResponseCompressionPreferenceProtocolTests.cs',
  'SharpLink.UnitTests.Protocol',
  'ResponseCompressionPreferenceProtocolTests',
  'PreferenceControlFramesShouldBeFixedAndStrict',
  36,
  81),
 ('test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs',
  'SharpLink.UnitTests.Protocol',
  'ProtocolV2Tests',
  'ControlWritersShouldClassifyInvalidLocalEnumsAsArgumentsWithoutPartialOutput',
  351,
  361),
 ('test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client',
  'InterceptedResponseLifetimeRecoveryTests',
  'DeadlineReturnsButSameResponseLeaseProtectsHeldManagerDecode',
  26,
  57),
 ('test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client',
  'InterceptedResponseLifetimeRecoveryTests',
  'DeadlineReturnsButSameResponseLeaseProtectsHeldManagerDecode',
  26,
  57),
 ('test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client',
  'InterceptedResponseLifetimeRecoveryTests',
  'LateSuccessfulInterceptorResultIsDisposedBeforeModuleRetires',
  60,
  78),
 ('test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client',
  'InterceptedResponseLifetimeRecoveryTests',
  'SuccessfulNonforwardingReplacementStillJoinsOriginalCoreWithoutDeadline',
  81,
  103),
 ('test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  'SharpLink.UnitTests.Runtime',
  'SharpLinkTimerCleanupOwnershipTests',
  'OverlappingCallbackFailureMustSurviveExpirySignalAndJoinEveryCallback',
  159,
  205),
 ('test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  'SharpLink.UnitTests.Runtime',
  'SharpLinkTimerCleanupOwnershipTests',
  'CallbackRearmFailureMustNotifyCloseAndJoinCallback',
  130,
  156),
 ('test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  'SharpLink.UnitTests.Runtime',
  'SharpLinkTimerCleanupOwnershipTests',
  'SourceSuccessMustAlsoJoinHeldTimerCallback',
  101,
  127),
 ('test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  'SharpLink.UnitTests.Client',
  'ContractScopedRouteFailureControls',
  'SitePrebuildFailuresAreAtomic',
  57,
  105),
 ('test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  'SharpLink.UnitTests.Client',
  'ContractScopedRouteFailureControls',
  'SitePrebuildFailuresAreAtomic',
  57,
  105),
 ('test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  'SharpLink.UnitTests.Client',
  'ContractScopedRouteFailureControls',
  'SitePrebuildFailuresAreAtomic',
  57,
  105),
 ('test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  'SharpLink.UnitTests.Client',
  'ContractScopedRouteFailureControls',
  'SitePrebuildFailuresAreAtomic',
  57,
  105),
 ('test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  'SharpLink.UnitTests.Client',
  'ContractScopedRouteFailureControls',
  'SitePrebuildFailuresAreAtomic',
  57,
  105),
 ('test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  'SharpLink.UnitTests.Client',
  'ContractScopedRouteFailureControls',
  'SitePrebuildFailuresAreAtomic',
  57,
  105),
 ('test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  'SharpLink.UnitTests.Client',
  'ContractScopedRouteFailureControls',
  'PendingBuildFailureDoesNotImposeAllBoundOnFreshPrepare',
  108,
  136))
ATTRIBUTE_LINES = (235, 297, 392, 8, 179, 35, 350, 23, 23, 59, 80, 158, 129, 100, 50, 50, 50, 50, 50, 50, 107)
PARAMETER_TYPES = [[],
 [],
 [],
 [],
 [],
 [],
 [],
 ['System.Boolean'],
 ['System.Boolean'],
 [],
 [],
 [],
 [],
 [],
 ['System.String'],
 ['System.String'],
 ['System.String'],
 ['System.String'],
 ['System.String'],
 ['System.String'],
 []]
RETURN_TYPES = ['System.Void',
 'System.Void',
 'System.Threading.Tasks.Task',
 'System.Void',
 'System.Void',
 'System.Void',
 'System.Threading.Tasks.Task',
 'System.Threading.Tasks.Task',
 'System.Threading.Tasks.Task',
 'System.Threading.Tasks.Task',
 'System.Threading.Tasks.Task',
 'System.Threading.Tasks.Task',
 'System.Threading.Tasks.Task',
 'System.Threading.Tasks.Task',
 'System.Threading.Tasks.Task',
 'System.Threading.Tasks.Task',
 'System.Threading.Tasks.Task',
 'System.Threading.Tasks.Task',
 'System.Threading.Tasks.Task',
 'System.Threading.Tasks.Task',
 'System.Threading.Tasks.Task']
GROUPS = [{'first': 0, 'count': 1, 'kind': 'protocol', 'parameter': None, 'literals': [], 'literalLines': []},
 {'first': 1, 'count': 1, 'kind': 'protocol', 'parameter': None, 'literals': [], 'literalLines': []},
 {'first': 2, 'count': 1, 'kind': 'protocol', 'parameter': None, 'literals': [], 'literalLines': []},
 {'first': 3, 'count': 1, 'kind': 'protocol', 'parameter': None, 'literals': [], 'literalLines': []},
 {'first': 4, 'count': 1, 'kind': 'protocol', 'parameter': None, 'literals': [], 'literalLines': []},
 {'first': 5, 'count': 1, 'kind': 'protocol', 'parameter': None, 'literals': [], 'literalLines': []},
 {'first': 6, 'count': 1, 'kind': 'protocol', 'parameter': None, 'literals': [], 'literalLines': []},
 {'first': 7, 'count': 2, 'kind': 'response-lease', 'parameter': 'duplex', 'literals': [False, True], 'literalLines': [24, 25]},
 {'first': 9, 'count': 1, 'kind': 'response-lease', 'parameter': None, 'literals': [], 'literalLines': []},
 {'first': 10, 'count': 1, 'kind': 'response-lease', 'parameter': None, 'literals': [], 'literalLines': []},
 {'first': 11, 'count': 1, 'kind': 'timer', 'parameter': None, 'literals': [], 'literalLines': []},
 {'first': 12, 'count': 1, 'kind': 'timer', 'parameter': None, 'literals': [], 'literalLines': []},
 {'first': 13, 'count': 1, 'kind': 'timer', 'parameter': None, 'literals': [], 'literalLines': []},
 {'first': 14,
  'count': 6,
  'kind': 'prebuild',
  'parameter': 'stage',
  'literals': ['targets', 'buckets', 'providers', 'state', 'signal', 'state-timeout'],
  'literalLines': [51, 52, 53, 54, 55, 56]},
 {'first': 20, 'count': 1, 'kind': 'prebuild', 'parameter': None, 'literals': [], 'literalLines': []}]
RUNTIMES = [{'framework': 'Microsoft.AspNetCore.App', 'version': '10.0.14'}, {'framework': 'Microsoft.NETCore.App', 'version': '10.0.14'}]
EXCEPTION_CATEGORIES = {'System.Exception': 'exception',
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
 'System.TimeoutException': 'timeout',
 'SharpLink.Runtime.PhysicalTaskFailureException': 'physical-task-failure',
 'System.OperationCanceledException': 'operation-cancelled',
 'System.Threading.Tasks.TaskCanceledException': 'task-cancelled',
 'SharpLink.Client.SharpLinkClient+ContractRouteFailure': 'contract-route-failure'}
REQUIRED_CASES = 21
BODY_COMMAND_COUNT = 54
EXCEPTION_TAGS = (((7, 8),
  'System.InvalidOperationException',
  'the real response route is manager-bound before codec Deserialize',
  'response-route-bound',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  38),
 ((7, 8),
  'System.InvalidOperationException',
  'logical deadline remains prompt',
  'response-logical-deadline',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  41),
 ((7, 8),
  'System.InvalidOperationException',
  'actual Pending deadline publishes its own original response error',
  'response-pending-deadline',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  44),
 ((7, 8),
  'System.InvalidOperationException',
  'logical completion cannot refund the same response lease while actual Decode remains held',
  'response-held-decode-lease',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  47),
 ((7, 8),
  'System.InvalidOperationException',
  'existing intercepted CTS remains physically owned',
  'response-held-cts',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  48),
 ((7, 8),
  'System.InvalidOperationException',
  'module releases only after original decoder exits',
  'response-module-release-after-decode',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  50),
 ((7, 8),
  'System.InvalidOperationException',
  'last physical owner releases lease and original CTS once',
  'response-lease-cts-refund',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  51),
 ((7, 8),
  'System.InvalidOperationException',
  'Stop retains exactly the original unconsumed Pending deadline',
  'response-stop-original-deadline-identity',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  55),
 ((9,),
  'System.InvalidOperationException',
  'late invocation still returns logical deadline',
  'response-late-result-logical-deadline',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  70),
 ((9,),
  'System.InvalidOperationException',
  'pending successful result still owns the same module lease',
  'response-late-result-lease-held',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  71),
 ((9,),
  'System.InvalidOperationException',
  'late successful enumerable is physically acquired and disposed, not merely Task-observed and discarded',
  'response-late-result-disposal',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  74),
 ((9,),
  'System.InvalidOperationException',
  'late-result cleanup completes before refund',
  'response-late-result-refund',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  76),
 ((10,),
  'System.InvalidOperationException',
  'ordinary successful replacement cleanup joins the original sink inline, even without abandoned deadline stage',
  'response-replacement-original-core-join',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  96),
 ((10,),
  'System.InvalidOperationException',
  'replacement remains an empty successful stream',
  'response-replacement-empty-success',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  97),
 ((10,),
  'System.InvalidOperationException',
  'original codec dispatch and original route are reconciled before module release',
  'response-replacement-core-reconciled',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  99),
 ((10,),
  'System.InvalidOperationException',
  'nonforwarding result cannot strand original stream or Pending registration',
  'response-replacement-no-stranded-owners',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  101),
 ((7, 8, 9, 10),
  'System.InvalidOperationException',
  'one genuine connected Session',
  'response-one-connected-session',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  196),
 ((7, 8, 9, 10),
  'System.InvalidOperationException',
  'peer binds only this exact current fixture contract identity',
  'response-exact-bind-identity',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  201),
 ((7, 8, 9, 10),
  'System.InvalidOperationException',
  'actual Connect/handshake and exact Bind reached ready state',
  'response-ready-bind',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  209),
 ((7, 8, 9, 10),
  'System.InvalidOperationException',
  'request owns an actual full Pending ID',
  'response-live-pending-id',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  225),
 ((7, 8, 9, 10),
  'System.InvalidOperationException',
  'arbitrary enumerator Dispose must not overlap MoveNext',
  'response-dispose-not-overlap-move',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  350),
 ((7, 8, 9, 10),
  'System.InvalidOperationException',
  'Expected actual failure.',
  'response-expected-failure-missing',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  428),
 ((7, 8, 9, 10),
  'System.TimeoutException',
  'codec release',
  'response-codec-release-timeout',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  364),
 ((7, 8, 9, 10),
  'System.TimeoutException',
  'owned fixture worker did not physically exit',
  'response-owned-worker-unjoined',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  269),
 ((7, 8),
  'System.TimeoutException',
  'only the original response lease remains',
  'response-original-lease-count-timeout',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  36),
 ((13,),
  'System.Exception',
  'source success must not detach a still-running callback',
  'timer-source-success-callback-held',
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  116),
 ((13,),
  'System.Exception',
  'source success before expiry must be preserved after quiescence',
  'timer-source-success-result',
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  124),
 ((13,),
  'System.Exception',
  'successful teardown must release the timer without closing a healthy Session',
  'timer-source-success-teardown',
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  126),
 ((12,),
  'System.Exception',
  'callback failure must not skip callback quiescence',
  'timer-rearm-failure-callback-held',
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  144),
 ((12,),
  'System.Exception',
  'callback rearm failure must reach the coordinator rather than escape the timer thread',
  'timer-rearm-failure-identity',
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  153),
 ((12,),
  'System.Exception',
  'failed callback must retain one timer teardown owner',
  'timer-rearm-one-owner',
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  155),
 ((11,),
  'System.Exception',
  'the regression requires two simultaneously active timer callbacks',
  'timer-two-overlapping-callbacks',
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  184),
 ((11,),
  'System.Exception',
  'one callback exiting must not satisfy teardown while the other is still active',
  'timer-one-callback-still-held',
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  189),
 ((11,),
  'System.Exception',
  'a callback fault after successful expiry signaling must be retained exactly once',
  'timer-late-rearm-failure-identity',
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  201),
 ((11,),
  'System.Exception',
  'all callbacks and timer ownership must join without a duplicate close notification',
  'timer-all-owners-joined',
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  204),
 ((11, 12, 13),
  'System.InvalidOperationException',
  'Change failed',
  'timer-injected-change-failure',
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  258),
 ((11, 12, 13),
  'System.InvalidOperationException',
  'timer not created',
  'timer-not-created',
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  284),
 ((14, 15, 16, 17, 18, 19),
  'System.InvalidOperationException',
  'the exact existing attempt and original public Prepare waiter precede the failing Bind commit',
  'prebuild-initial-attempt',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  65),
 ((14, 15, 16, 17, 18, 19),
  'System.InvalidOperationException',
  'the fault boundary must capture its current pending notification, not an earlier completed generation',
  'prebuild-current-notification',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  78),
 ((14, 15, 16, 17, 18, 19),
  'System.InvalidOperationException',
  'failed prebuild preserves view/attempt completion but immediately publishes the exact cold failure on the existing typed signal',
  'prebuild-atomic-publication',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  89),
 ((14, 15, 16, 17, 18, 19),
  'System.InvalidOperationException',
  'original and fresh Prepare must fail promptly while original Bind/cleanup and their physical candidate permits remain owned',
  'prebuild-held-cleanup',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  94),
 ((14, 15, 16, 17, 18, 19),
  'System.InvalidOperationException',
  'the failed exact candidate and its one cleanup owner are both physically released',
  'prebuild-candidate-refund',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  100),
 ((20,),
  'System.InvalidOperationException',
  'A remains exactly eligible while B owns its pending infrastructure failure',
  'prebuild-control-eligible-route',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  128),
 ((20,),
  'System.InvalidOperationException',
  "fresh at-least-one Prepare/RPC cannot wait for B's blocked cleanup",
  'prebuild-control-no-allbound-wait',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  131),
 ((20,),
  'System.InvalidOperationException',
  'R23 other-endpoint pending construction failure',
  'prebuild-control-injected-marker',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  115),
 ((14, 15, 16, 17, 18, 19),
  'System.TimeoutException',
  'R23 original construction timeout, not a Prepare wait timeout',
  'prebuild-injected-state-timeout',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  68),
 ((14, 15, 16, 17, 18, 19, 20),
  'System.InvalidOperationException',
  'one failed target must have exactly one cleanup owner',
  'prebuild-one-cleanup-owner',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  400),
 ((14, 15, 16, 17, 18, 19, 20),
  'System.InvalidOperationException',
  "the failed candidate's exact token owns the one real ReleaseBinding frame",
  'prebuild-exact-release-token',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  417),
 ((14, 15, 16, 17, 18, 19, 20),
  'System.InvalidOperationException',
  'the original cleanup Task physically exited successfully',
  'prebuild-cleanup-task-success',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  419),
 ((14, 15, 16, 17, 18, 19, 20),
  'System.InvalidOperationException',
  'both original Bind tasks were captured before any confirmation',
  'prebuild-two-original-binds',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  425),
 ((14, 15, 16, 17, 18, 19, 20),
  'System.InvalidOperationException',
  'the injected infrastructure failure cannot be reported as semantic Bind success',
  'prebuild-no-false-bind-success',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  427),
 ((14, 15, 16, 17, 18, 19, 20),
  'System.InvalidOperationException',
  "explicit cold retry publishes only A's independently confirmed new incarnation",
  'prebuild-retry-only-new-a',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  480),
 ((14, 15, 16, 17, 18, 19, 20),
  'System.InvalidOperationException',
  'the old successful notification value retains its original construction error after current signal rotation',
  'prebuild-captured-notification-retains-marker',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  486),
 ((14, 15, 16, 17, 18, 19, 20),
  'System.InvalidOperationException',
  'The actual cold ready-check must prefer captured failure over later readiness.',
  'prebuild-old-notification-priority',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  495),
 ((14, 15, 16, 17, 18, 19, 20),
  'System.InvalidOperationException',
  'A later successful route erased the captured old notification failure.',
  'prebuild-old-notification-not-erased',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  498),
 ((14, 15, 16, 17, 18, 19, 20),
  'System.InvalidOperationException',
  'Synchronous Prepare must retain the exact construction failure.',
  'prebuild-sync-prepare-original-error',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  526),
 ((14, 15, 16, 17, 18, 19, 20),
  'System.InvalidOperationException',
  'expected infrastructure error must be one real fault, not cancellation or an unknown sibling graph',
  'prebuild-one-original-fault-graph',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  554),
 ((14, 15, 16, 17, 18, 19, 20),
  'System.InvalidOperationException',
  'terminal audit cannot silently become an unbounded join',
  'prebuild-terminal-audit-no-hidden-join',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  630),
 ((14, 15, 16, 17, 18, 19, 20),
  'System.InvalidOperationException',
  'Original task exit is unknown; fixture roots retained. Isolate/terminate this test process.',
  'prebuild-owner-outcome-unknown',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  289),
 ((14, 15, 16, 17, 18, 19, 20),
  'System.TimeoutException',
  'Both pending original BindContractDemand tasks were not registered.',
  'prebuild-original-binds-unregistered',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  180),
 ((14, 15, 16, 17, 18, 19, 20),
  'System.TimeoutException',
  'The exact cold publication predicate was not observed.',
  'prebuild-view-predicate-timeout',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  367),
 ((14, 15, 16, 17, 18, 19, 20),
  'System.TimeoutException',
  'The original candidate cleanup Task was not registered.',
  'prebuild-cleanup-owner-unregistered',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  403),
 ((14, 15, 16, 17, 18, 19, 20),
  'System.TimeoutException',
  'Original cold writer barrier was not released.',
  'prebuild-cold-writer-release-timeout',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  666),
 ((14, 15, 16, 17, 18, 19),
  'System.InvalidOperationException',
  'R23 exact targets prebuild marker',
  'prebuild-injected-stage-marker',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  69),
 ((14, 15, 16, 17, 18, 19),
  'System.InvalidOperationException',
  'R23 exact buckets prebuild marker',
  'prebuild-injected-stage-marker',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  69),
 ((14, 15, 16, 17, 18, 19),
  'System.InvalidOperationException',
  'R23 exact providers prebuild marker',
  'prebuild-injected-stage-marker',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  69),
 ((14, 15, 16, 17, 18, 19),
  'System.InvalidOperationException',
  'R23 exact state prebuild marker',
  'prebuild-injected-stage-marker',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  69),
 ((14, 15, 16, 17, 18, 19),
  'System.InvalidOperationException',
  'R23 exact signal prebuild marker',
  'prebuild-injected-stage-marker',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  69),
 ((7, 8, 9, 10),
  'System.InvalidOperationException',
  'Missing actual diagnostic field _error',
  'response-core-error-null-or-field-absent',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  420),
 ((2,),
  'SharpLink.Runtime.SharpLinkProtocolViolationException',
  'Cancel payload must contain exactly one reason byte.',
  'cancel-payload-one-reason-byte',
  'src/SharpLink.Runtime/ProtocolV2/ProtocolV2PayloadCodec.cs',
  390))
EXTRA_FRAMES = (((7, 8, 9, 10),
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client.InterceptedResponseLifetimeRecoveryTests',
  'Ensure',
  'Boolean condition, String message',
  423,
  423),
 ((7, 8, 9, 10),
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client.InterceptedResponseLifetimeRecoveryTests',
  'Failure',
  'Task task',
  424,
  429),
 ((7, 8, 9, 10),
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client.InterceptedResponseLifetimeRecoveryTests',
  'Until',
  'Func`1 predicate, String label',
  430,
  434),
 ((7, 8, 9, 10),
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client.InterceptedResponseLifetimeRecoveryTests',
  'WithFixture',
  'Interceptor interceptor, Func`2 body',
  159,
  165),
 ((7, 8, 9, 10),
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client.InterceptedResponseLifetimeRecoveryTests.Fixture',
  'InitializeAsync',
  '',
  183,
  210),
 ((7, 8, 9, 10),
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client.InterceptedResponseLifetimeRecoveryTests+Fixture',
  'InitializeAsync',
  '',
  183,
  210),
 ((7, 8, 9, 10),
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client.InterceptedResponseLifetimeRecoveryTests.Fixture',
  'RequestAsync',
  'Boolean duplex',
  222,
  229),
 ((7, 8, 9, 10),
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client.InterceptedResponseLifetimeRecoveryTests+Fixture',
  'RequestAsync',
  'Boolean duplex',
  222,
  229),
 ((7, 8, 9, 10),
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client.InterceptedResponseLifetimeRecoveryTests.Fixture',
  'CleanupAsync',
  'Exception primary',
  238,
  276),
 ((7, 8, 9, 10),
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client.InterceptedResponseLifetimeRecoveryTests+Fixture',
  'CleanupAsync',
  'Exception primary',
  238,
  276),
 ((7, 8, 9, 10),
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client.InterceptedResponseLifetimeRecoveryTests.HeldCodec',
  'Deserialize',
  'ReadOnlySequence`1& bytes',
  361,
  366),
 ((7, 8, 9, 10),
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client.InterceptedResponseLifetimeRecoveryTests+HeldCodec',
  'Deserialize',
  'ReadOnlySequence`1& bytes',
  361,
  366),
 ((11, 12, 13),
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  'SharpLink.UnitTests.Runtime.SharpLinkTimerCleanupOwnershipTests',
  'Ensure',
  'Boolean condition, String message',
  239,
  243),
 ((11, 12, 13),
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  'SharpLink.UnitTests.Runtime.SharpLinkTimerCleanupOwnershipTests',
  'CaptureFailureAsync',
  'Task task',
  233,
  237),
 ((11, 12, 13),
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  'SharpLink.UnitTests.Runtime.SharpLinkTimerCleanupOwnershipTests.TrackingTimeProvider.TrackingTimer',
  'Change',
  'TimeSpan dueTime, TimeSpan period',
  307,
  319),
 ((11, 12, 13),
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  'SharpLink.UnitTests.Runtime.SharpLinkTimerCleanupOwnershipTests+TrackingTimeProvider+TrackingTimer',
  'Change',
  'TimeSpan dueTime, TimeSpan period',
  307,
  319),
 ((11, 12, 13),
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  'SharpLink.UnitTests.Runtime.SharpLinkTimerCleanupOwnershipTests.TrackingTimeProvider.TrackingTimer',
  'Invoke',
  '',
  321,
  345),
 ((11, 12, 13),
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  'SharpLink.UnitTests.Runtime.SharpLinkTimerCleanupOwnershipTests+TrackingTimeProvider+TrackingTimer',
  'Invoke',
  '',
  321,
  345),
 ((11, 12, 13),
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  'SharpLink.UnitTests.Runtime.SharpLinkTimerCleanupOwnershipTests.TrackingTimeProvider.TrackingTimer',
  'DisposeAsync',
  '',
  355,
  369),
 ((11, 12, 13),
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  'SharpLink.UnitTests.Runtime.SharpLinkTimerCleanupOwnershipTests+TrackingTimeProvider+TrackingTimer',
  'DisposeAsync',
  '',
  355,
  369),
 ((14, 15, 16, 17, 18, 19, 20),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls',
  'WithFixture',
  'Func`2 body',
  138,
  145),
 ((14, 15, 16, 17, 18, 19, 20),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls',
  'Ensure',
  'Boolean condition, String message',
  687,
  690),
 ((14, 15, 16, 17, 18, 19, 20),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls.FailureFixture',
  'CleanupAsync',
  'Exception primary',
  185,
  276),
 ((14, 15, 16, 17, 18, 19, 20),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls+FailureFixture',
  'CleanupAsync',
  'Exception primary',
  185,
  276),
 ((14, 15, 16, 17, 18, 19, 20),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls.FailureFixture',
  'ThrowCleanupFailures',
  'List`1 failures',
  293,
  297),
 ((14, 15, 16, 17, 18, 19, 20),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls+FailureFixture',
  'ThrowCleanupFailures',
  'List`1 failures',
  293,
  297),
 ((14, 15, 16, 17, 18, 19, 20),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls.FailureFixture',
  'ExpectFailureAsync',
  'OwnedOperation operation, Func`2 expected',
  542,
  575),
 ((14, 15, 16, 17, 18, 19, 20),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls+FailureFixture',
  'ExpectFailureAsync',
  'OwnedOperation operation, Func`2 expected',
  542,
  575),
 ((14, 15, 16, 17, 18, 19, 20),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls.FailureFixture',
  'ObserveAsync',
  'OwnedOperation operation',
  589,
  604),
 ((14, 15, 16, 17, 18, 19, 20),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls+FailureFixture',
  'ObserveAsync',
  'OwnedOperation operation',
  589,
  604),
 ((14, 15, 16, 17, 18, 19, 20),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls.FailureFixture',
  'JoinAsync',
  'OwnedOperation operation, List`1 failures',
  606,
  626),
 ((14, 15, 16, 17, 18, 19, 20),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls+FailureFixture',
  'JoinAsync',
  'OwnedOperation operation, List`1 failures',
  606,
  626),
 ((14, 15, 16, 17, 18, 19, 20),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls.FailureFixture',
  'CaptureTerminalFailureAsync',
  'Task source',
  628,
  633),
 ((14, 15, 16, 17, 18, 19, 20),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls+FailureFixture',
  'CaptureTerminalFailureAsync',
  'Task source',
  628,
  633),
 ((14, 15, 16, 17, 18, 19, 20),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls.FailureFixture',
  'CaptureCandidateCleanupAsync',
  '',
  393,
  406),
 ((14, 15, 16, 17, 18, 19, 20),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls+FailureFixture',
  'CaptureCandidateCleanupAsync',
  '',
  393,
  406),
 ((14, 15, 16, 17, 18, 19, 20),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls.FailureFixture',
  'FinishCandidateCleanupAsync',
  'OwnedOperation cleanup, Int32 endpoint',
  408,
  420),
 ((14, 15, 16, 17, 18, 19, 20),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls+FailureFixture',
  'FinishCandidateCleanupAsync',
  'OwnedOperation cleanup, Int32 endpoint',
  408,
  420),
 ((14, 15, 16, 17, 18, 19, 20),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls.FailureFixture',
  'AssertCapturedNotificationStillFails',
  'Task`1 notification, Exception marker',
  483,
  499),
 ((14, 15, 16, 17, 18, 19, 20),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls+FailureFixture',
  'AssertCapturedNotificationStillFails',
  'Task`1 notification, Exception marker',
  483,
  499),
 ((14, 15, 16, 17, 18, 19, 20),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls.ColdBarrier',
  'HoldOnce',
  '',
  661,
  667),
 ((14, 15, 16, 17, 18, 19, 20),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls+ColdBarrier',
  'HoldOnce',
  '',
  661,
  667),
 ((0, 1, 2, 3, 4, 5, 6),
  'src/SharpLink.Runtime/ProtocolV2/ProtocolV2PayloadCodec.cs',
  'SharpLink.Runtime.ProtocolV2PayloadCodec',
  'WriteHandshakeRequest',
  'IBufferWriter`1 writer, ProtocolV2HandshakeRequest& request, SharpLinkProtocolOptions limits',
  18,
  69),
 ((0, 1, 2, 3, 4, 5, 6),
  'src/SharpLink.Runtime/ProtocolV2/ProtocolV2PayloadCodec.cs',
  'SharpLink.Runtime.ProtocolV2PayloadCodec',
  'WriteHandshakeResponse',
  'IBufferWriter`1 writer, ProtocolV2HandshakeResponse& response',
  141,
  174),
 ((2,),
  'src/SharpLink.Runtime/ProtocolV2/ProtocolV2PayloadCodec.cs',
  'SharpLink.Runtime.ProtocolV2PayloadCodec',
  'ReadCancelReason',
  'ReadOnlySequence`1 payload',
  387,
  397),
 ((0, 1, 2, 4, 6),
  'test/SharpLink.UnitTests/Protocol/ProtocolV2Tests.cs',
  'SharpLink.UnitTests.Protocol.ProtocolV2Tests',
  'Ensure',
  'Boolean condition, String message',
  900,
  904),
 ((3, 5),
  'test/SharpLink.UnitTests/Protocol/ResponseCompressionPreferenceProtocolTests.cs',
  'SharpLink.UnitTests.Protocol.ResponseCompressionPreferenceProtocolTests',
  'Ensure',
  'Boolean condition, String description',
  83,
  87),
 ((7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20),
  'src/SharpLink.Client/SharpLinkClient.cs',
  'SharpLink.Client.SharpLinkClient',
  'StopCoreAsync',
  '',
  161,
  218),
 ((7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20),
  'src/SharpLink.Client/SharpLinkClient.cs',
  'SharpLink.Client.SharpLinkClient',
  'ThrowStopCleanupFailures',
  'List`1 failures',
  255,
  262),
 ((7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20),
  'src/SharpLink.Runtime/FrameworkTaskSupervisor.cs',
  'SharpLink.Runtime.FrameworkTaskSupervisor',
  'DrainCoreAsync',
  'Task signal',
  409,
  436))
CODES = frozenset(('PD_HELPERS','PD_SOURCE','PD_SDK','PD_RUNTIME','PD_SELECTION','PD_PREPARATION','PD_COMMANDS','PD_ARGV','PD_SHAPE','PD_LIMIT','PD_EXCEPTION','PD_JUNIT','PD_REPORTS','PD_OWNER','PD_EXIT','PD_RETRY','PD_CASE_FAILED','PD_INCOMPLETE','PD_BUDGET','PD_PUBLIC'))


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    obj = importlib.util.module_from_spec(spec); spec.loader.exec_module(obj)
    return obj


def verify_source(r, root):
    catalog = r.read_json(root / 'eng/unit-census.json')
    r.require(r.sha(root / 'eng/unit-census.json') == HELPERS['eng/unit-census.json'], 'PD_SOURCE')
    for name, digest in catalog['sourcePins'].items():
        r.require(r.sha(root / name) == digest, 'PD_SOURCE')
    for name, digest in SOURCE_PINS.items():
        r.require(r.sha(root / name) == digest, 'PD_SOURCE')


def verify_isolated_sdk(lane, census):
    r = lane.r
    dotnet = Path(census.shutil.which('dotnet', path=lane.env['PATH']) or '').resolve()
    root = dotnet.parent
    temporary = os.environ.get('RUNNER_TEMP')
    r.require(type(temporary) is str and Path(temporary).is_absolute() and
              root == Path(temporary) / 'protocol-lifetime-dotnet' and dotnet.name == 'dotnet' and dotnet.is_file(), 'PD_SDK')
    r.legacy.directory_identity_strict(root)
    for name in ('DOTNET_ROOT', 'DOTNET_ROOT_X64'):
        if name in lane.env: r.require(Path(lane.env[name]) == root, 'PD_SDK')
    r.require(lane.env.get('DOTNET_MULTILEVEL_LOOKUP') == '0', 'PD_SDK')


def verify_helpers():
    for name, expected in HELPERS.items():
        path = ROOT / name
        if path.is_symlink() or hashlib.sha256(path.read_bytes()).hexdigest() != expected:
            raise RuntimeError('PD_HELPERS')


def group_for(index):
    return next(group for group in GROUPS if group['first'] <= index < group['first'] + group['count'])


def expected_displays(group):
    method=CASES[group['first']][3]
    return {method+'('+('True' if value is True else 'False' if value is False else value)+')':(value,line)
            for value,line in zip(group['literals'],group['literalLines'])}


def selected(r,ordinary):
    output=[]
    for group in GROUPS:
        index=group['first'];source,namespace,cls,method,start,end=CASES[index]
        matches=[(uid,row) for uid,row in ordinary.items() if row['type']['namespace']==namespace and
                 row['type']['typeName']==cls and row['type']['methodName']==method]
        r.require(len(matches)==group['count'],'PD_SELECTION')
        matches.sort(key=lambda pair:hashlib.sha256(pair[0].encode()).hexdigest())
        for uid,row in matches:
            meta=row['type'];loc=row['location']
            r.require(set(meta)=={'namespace','typeName','methodName','assemblyFullName','methodArity','returnTypeFullName','parameterTypeFullNames'} and
                      meta['assemblyFullName']=='SharpLink.UnitTests, Version=3.0.0.0, Culture=neutral, PublicKeyToken=null' and
                      type(meta['methodArity']) is int and meta['methodArity']==0 and meta['parameterTypeFullNames']==PARAMETER_TYPES[index] and
                      meta['returnTypeFullName']==RETURN_TYPES[index],'PD_SELECTION')
            r.require(row['uid']==uid and type(uid) is str and uid and len(uid)<=4096 and not uid.startswith(('-','@')) and
                      not any(ord(c)<32 for c in uid),'PD_SELECTION')
            r.require(set(loc)=={'file','lineStart','lineEnd'} and loc['file']=='/_/'+source and
                      type(loc['lineStart']) is int and loc['lineStart']==ATTRIBUTE_LINES[index] and
                      type(loc['lineEnd']) is int and loc['lineEnd']==end,'PD_SELECTION')
            output.append((uid,row))
    r.require(len(output)==REQUIRED_CASES and len({uid for uid,_ in output})==REQUIRED_CASES,'PD_SELECTION')
    return output


def argument_proofs(selections):
    proof={}
    for group in GROUPS:
        if not group['literals']:continue
        rows=selections[group['first']:group['first']+group['count']]
        names=[row['displayName'] for _,row in rows]
        proof[group['first']]=len(names)==group['count'] and set(names)==set(expected_displays(group))
    return proof


def argument_identity(index,selection,proofs=None):
    uid,row=selection;group=group_for(index);parameter=group['parameter']
    result={'caseID':hashlib.sha256(uid.encode()).hexdigest(),'kind':'opaque-parameter-case' if parameter else 'parameterless',
            'parameter':parameter,'value':None,'limitation':'source-literal-set-not-proven' if parameter else 'none'}
    if parameter and (proofs or {}).get(group['first']) is True:
        known=expected_displays(group)
        if row['displayName'] in known:
            value,line=known[row['displayName']]
            result.update(kind='source-literal-boolean' if type(value) is bool else 'source-literal-string',value=value,limitation='none',
                          literalSource={'source':CASES[index][0],'line':line},proof='pinned-default-display-name-exact-complete-set')
    return result


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
    r.require(test['status'] in ('passed', 'failed', 'error', 'timedOut', 'cancelled'), 'PD_EXCEPTION')
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


def source_frames(index,stack):
    source,namespace,cls,method,start,end=CASES[index];group=group_for(index)
    signature='Boolean duplex' if group['parameter']=='duplex' else 'String stage' if group['parameter']=='stage' else ''
    frame_map={}
    for line in range(start,end+1):frame_map[f'   at {namespace}.{cls}.{method}({signature}) in /_/{source}:line {line}']=(source,method,line)
    for indexes,path,typ,member,signature,first,last in EXTRA_FRAMES:
        if index in indexes:
            for line in range(first,last+1):frame_map[f'   at {typ}.{member}({signature}) in /_/{path}:line {line}']=(path,member,line)
    # Async bodies inside these unchanged source lambdas have compiler names.
    # Accept only anchored known class+method, bounded compiler ordinals, exact
    # source path, exact parameter signature and source-body bounds. Never emit
    # generated names or captured ordinals; unknown formatting remains unknown.
    patterns=[]
    if group['kind'] in ('response-lease','prebuild'):
        param='Fixture fixture' if group['kind']=='response-lease' else 'FailureFixture fixture'
        prefix=re.escape(namespace+'.'+cls)+r'\.(?:<>c__DisplayClass[0-9]{1,3}_[0-9]{1,3}|<>c)\.'
        method_pattern=re.escape(method)
        patterns.append(re.compile(r'^   at '+prefix+r'<'+method_pattern+r'>b__[0-9]{1,3}(?:_[0-9]{1,3})?\('+re.escape(param)+r'\) in /_/'+re.escape(source)+r':line ([0-9]{1,4})$'))
        patterns.append(re.compile(r'^   at '+prefix+r'<<'+method_pattern+r'>b__[0-9]{1,3}(?:_[0-9]{1,3})?>d\.MoveNext\(\) in /_/'+re.escape(source)+r':line ([0-9]{1,4})$'))
    lines=stack.splitlines()
    if len(lines)>4096:raise ValueError('PD_LIMIT')
    output=[];known=0
    for raw in lines:
        value=frame_map.get(raw)
        if value is None:
            for pattern in patterns:
                match=pattern.fullmatch(raw)
                if match and start<=int(match.group(1))<=end:
                    value=(source,method+'-body-lambda',int(match.group(1)));break
        if value is not None:
            known+=1
            if len(output)<64:output.append(dict(zip(('source','member','line'),value)))
    return output,len(lines)-known,max(0,known-64)


def projected_exception(r, index, exception):
    exception_shape(r, exception)
    result = []; current = exception
    while current is not None:
        frames, unknown, omitted = source_frames(index, current.get('stackTrace', ''))
        row = {'depth': len(result), 'category': EXCEPTION_CATEGORIES.get(current['type'], 'unknown'),
               'assertionTag': 'unknown', 'sourceFrames': frames, 'unrecognizedStackLineCount': unknown, 'omittedRecognizedStackLineCount': omitted}
        for indexes, typ, message, tag, path, line in EXCEPTION_TAGS:
            if index in indexes and (current['type'], current['message']) == (typ, message):
                row.update(assertionTag=tag, exactMessageSource={'source': path, 'line': line})
        result.append(row); current = current.get('innerException')
    return result


def classify(execution, native, junit, index, selection, proofs=None):
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
            'argumentIdentity': argument_identity(index, selection, proofs),
            'exceptionEvidenceScope': 'serialized-innerException-chain-only',
            'aggregateSiblingCausesObserved': False,
            'caseGroup': group_for(index)['kind'],
            'bodyVersusCleanup': 'inconclusive' if group_for(index)['kind'] != 'protocol' and test['status'] != 'passed' else 'not-applicable',
            'privateRouteFailurePayloadObserved': False,
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


def prepare_sdk_metadata(lane,sdkroot):
    # Fresh pinned SDKs create this empty directory during Build. Prepare only
    # that exact absent directory, then seal it independently before any SDK
    # command. Neither workload markers nor arbitrary preexisting content pass.
    r=lane.r;sdkroot=Path(sdkroot)
    parent_identity=r.legacy.directory_identity_strict(sdkroot)
    metadata=sdkroot/'metadata'
    r.require(not os.path.lexists(metadata),'UE_SDK_METADATA')
    metadata.mkdir(mode=0o755)
    r.legacy.verify_directory(sdkroot,parent_identity)
    identity=r.legacy.directory_identity_strict(metadata)
    r.legacy.verify_directory(metadata,identity,empty=True)
    lane.trees[metadata]=r.snapshot(metadata);lane.seal_domains[metadata]=('sdk',None)
    return metadata


def prepare(lane,census,results):
    r=lane.r
    verify_isolated_sdk(lane,census)
    verify_helpers()
    r.require(r.sha(ROOT/'eng/unit-census.json')==results.CATALOG_SHA256,'UE_CATALOG')
    dotnet=Path(census.shutil.which('dotnet',path=lane.env['PATH']) or '').resolve()
    r.require(dotnet.is_file(),'UC_SDK_MISSING')
    prepare_sdk_metadata(lane,dotnet.parent)
    census.execute(lane)
    lane.check()
    first=r.read_json(lane.evidence/'ledger1'/'ledger.json')
    second=r.read_json(lane.evidence/'ledger2'/'ledger.json')
    ordinary,explicit=results.expected_ordinary(first,second)
    lane.check()
    r.require(lane.stable and lane.unfiltered and lane.durable and not lane.errors and time.monotonic()<lane.stop,'UE_PREPARATION')
    r.require(lane.sequence==32,'UE_COMMANDS')
    r.require(lane.identity['sdk']=='10.0.114' and sorted(lane.identity['installedRuntimes'],key=lambda row:row['framework'])==RUNTIMES,'PD_RUNTIME')
    certificate={'identity':lane.identity,'metadataQualified':True,'ordinaryCount':len(ordinary),'explicitCount':len(explicit),
        'canonicalLedgerSHA256':r.digest(first['actual']),'preparationDeadlineMonotonic':lane.stop}
    r.write_json(lane.evidence,'preparation.json',certificate);lane.extend_evidence(['preparation.json'])
    lane.check()
    r.require(time.monotonic()<lane.stop,'UE_PREPARATION')
    return ordinary,explicit


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
    verify_helpers(); verify_source(r, lane.source)
    lane.check()
    now = time.monotonic()
    r.require(now < lane.stop and now < overall_stop, 'PD_BUDGET')
    lane.preparation_complete = True
    lane.stop = min(overall_stop, now + 4800)
    # The original checkout remains an independently sealed, unbuilt input.
    # Rebase controls must use it, not the clone containing generated bin/obj.
    lane.run('protocol-lifetime-python-controls', [sys.executable, '-B', ROOT / 'eng/tests/test_protocol_lifetime_diagnostic.py'], ROOT, 90)
    r.require(lane.sequence == 33, 'PD_COMMANDS')
    dotnet = next(path for path in lane.inputs if path.name == 'dotnet' and path.parent in lane.trees)
    dll = lane.source / 'test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll'
    lane.env['TUNIT_ENABLE_JUNIT_REPORTER'] = '1'
    proofs = argument_proofs(selections)
    for index, selection in enumerate(selections):
        lane.check(); verify_source(r, lane.source)
        r.require(r.sha(dll) == lane.identity['binarySHA256'], 'UC_INPUT_CHANGED')
        uid, _ = selection; label = 'protocol-lifetime-case-' + str(index + 1)
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
        result = classify(execution, native, junit, index, selection, proofs)
        exit_code = state['receipt']['exitCode']
        r.require(type(exit_code) is int and exit_code == (0 if result['selectedPassed'] else 2), 'PD_EXIT')
        result.update(ownerClosed=True, bodyExitCode=exit_code,
                      nativeFileSHA256=r.sha(report / 'session.tunit-report.json'), junitFileSHA256=r.sha(report / 'junit.xml'))
        if not result['selectedPassed']:
            lane.validated_failure_receipts.append(state['receipt'])
        lane.case_facts.append(result)
        lane.check()
        if not result['selectedPassed']: lane.record(r.Rejected('PD_CASE_FAILED'))
    r.require(lane.sequence == BODY_COMMAND_COUNT and len(lane.case_facts) == REQUIRED_CASES, 'PD_COMMANDS')
    lane.diagnostic_complete = True


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


def infrastructure_qualified(lane, complete):
    if not complete or not lane.durable or time.monotonic() >= lane.stop: return False
    queue = list(lane.errors); seen = set()
    while queue:
        error = queue.pop()
        if id(error) in seen: continue
        seen.add(id(error))
        if isinstance(error, BaseExceptionGroup): queue.extend(error.exceptions)
        elif type(error) is lane.r.Rejected and error.code == 'PD_CASE_FAILED': pass
        elif isinstance(error, lane.owner.CommandFailure) and any(error.outcome is receipt for receipt in lane.validated_failure_receipts): pass
        else: return False
        if error.__cause__ is not None: queue.append(error.__cause__)
        if error.__context__ is not None: queue.append(error.__context__)
    return True


def finish(lane, census, execution):
    r = lane.r
    try: lane.check()
    except BaseException as error: lane.record(error)
    complete = lane.diagnostic_complete and len(lane.case_facts) == REQUIRED_CASES
    qualified = bool(complete and not lane.errors and lane.durable and time.monotonic() < lane.stop)
    try:
        summary = {'schemaVersion': 1, 'scope': 'twenty-one-ordinary-protocol-lifetime-diagnostic',
            'identity': public_identity(r, lane.identity), 'sourceTrees': dict(SOURCE_TREES),
            'candidateCatalogSHA256': HELPERS['eng/unit-census.json'],
            'sourceUnchangedFromHead': '1f5315ca23753258c002ebd6bed294f0226af603',
            'requiredSdk': '10.0.114', 'requiredRuntime': '10.0.14',
            'preparationQualifiedAtHandoff': lane.preparation_complete,
            'diagnosticComplete': complete, 'diagnosticQualified': qualified,
            'infrastructureQualified': infrastructure_qualified(lane, complete),
            'observedNonpassingCaseCount': sum(not row['selectedPassed'] for row in lane.case_facts),
            'allSelectedPassed': bool(complete and all(row['selectedPassed'] for row in lane.case_facts)),
            'fullSuiteQualified': False, 'requiredSelectedCaseCount': REQUIRED_CASES,
            'expectedOrdinaryMetadataCount': 2879, 'expectedExplicitMetadataCount': 5,
            'historicalFullRun': {'runId': 41970862705, 'passed': 2614, 'errors': 265, 'qualified': False,
                'evidenceSHA256': HELPERS['eng/protocol-lifetime-history/current-full-result.json']},
            'originalFullRun': {'runId': 41940224644, 'passed': 2589, 'errors': 290, 'qualified': False},
            'historicalArgumentMappingAvailable': False,
            'exceptionEvidenceScope': 'serialized-innerException-chain-only',
            'aggregateSiblingCausesObserved': False,
            'bodyVersusCleanupInference': 'known-frames-only-no-causal-inference',
            'privateRouteFailurePayloadObserved': False,
            'caseGroupCounts': {'protocol':7,'response-lease':4,'timer':3,'prebuild':7},
            'errorOccurrences': len(lane.errors), 'rejections': rejections(lane, execution),
            'evidenceDurable': lane.durable, 'cases': lane.case_facts, 'exit': 0 if qualified else 1}
        r.write_json(lane.evidence, 'protocol-lifetime-diagnostic-summary.json', summary)
        lane.extend_evidence(['protocol-lifetime-diagnostic-summary.json'])
        lane.check(); rendered = json.dumps(summary, sort_keys=True, allow_nan=False)
        r.require(time.monotonic() < lane.stop, 'PD_BUDGET')
        print(rendered, flush=True)
        r.require(time.monotonic() < lane.stop, 'PD_BUDGET')
        return 0 if qualified else 1
    except BaseException as error:
        lane.record(error)
        try: os.write(2, b'{"terminalDiagnostic":true,"diagnosticQualified":false,"fullSuiteQualified":false,"exit":1}\n')
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
        work = Path(tempfile.mkdtemp(prefix='slpl-', dir='/tmp')); os.chmod(work, 0o700); ACTIVE['work'] = work
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
        lane.validated_failure_receipts = []; lane.case_facts = []; lane.diagnostic_complete = False; lane.preparation_complete = False
        lane.stop = min(lane.stop, started + 3600)
        try:
            for name, expected in HELPERS.items(): lane.r.require(lane.r.sha(ROOT / name) == expected, 'PD_HELPERS')
            verify_isolated_sdk(lane, census)
            ordinary, explicit = prepare(lane, census, execution)
            selections = handoff_selections(lane, ordinary, explicit)
            run_cases(lane, execution, selections, started + 8400)
        except BaseException as error: lane.record(error)
        return finish(lane, census, execution)
    except BaseException as error:
        if lane is not None: lane.record(error)
        else:
            ACTIVE['errors'].append(error)
            if census is not None and ACTIVE['work'] is not None: census.persist(ACTIVE['work'], ACTIVE['errors'], 999)
            elif execution_runner is not None: execution_runner.bootstrap_persist(ACTIVE['work'], ACTIVE['errors'])
        try: os.write(2, b'PROTOCOL_LIFETIME_DIAGNOSTIC_BOOTSTRAP_FAILED\n')
        except BaseException as secondary: ACTIVE['errors'].append(secondary)
        return 1


def guarded_main():
    try: return main()
    except BaseException as error:
        ACTIVE['errors'].append(error)
        try: os.write(2, b'PROTOCOL_LIFETIME_DIAGNOSTIC_OUTERMOST_FAILED\n')
        except BaseException as secondary: ACTIVE['errors'].append(secondary)
        return 1


if __name__=='__main__':raise SystemExit(guarded_main())
